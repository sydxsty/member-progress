using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Cloud.Unum.USearch;

namespace StudentProgress.WeChat;

// Embeddings are a local search index only. Raw messages and originals remain authoritative.
internal sealed class WeChatEmbedding : IDisposable
{
    readonly string modelRoot,indexPath;
    readonly object gate=new();
    InferenceSession? session;
    SentencePieceTokenizer? tokenizer;
    USearchIndex? index;
    bool disposed;
    internal const int Dimensions=1024;
    internal WeChatEmbedding(string baseDir,string dataRoot){modelRoot=Path.Combine(baseDir,"runtime","embedding","bge-m3-fp32");indexPath=Path.Combine(dataRoot,"similarity.usearch");}
    internal bool Available=>File.Exists(Path.Combine(modelRoot,"model.onnx"))&&File.Exists(Path.Combine(modelRoot,"model.onnx_data"))&&File.Exists(Path.Combine(modelRoot,"sentencepiece.bpe.model"));
    void EnsureModel()
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(session!=null)return;
        if(!Available)throw new InvalidOperationException("安装包缺少本地相似度模型；仍可使用关键词搜索。");
        using var options=new SessionOptions{IntraOpNumThreads=Math.Clamp(Environment.ProcessorCount/2,1,4),InterOpNumThreads=1,GraphOptimizationLevel=GraphOptimizationLevel.ORT_ENABLE_ALL};
        session=new InferenceSession(Path.Combine(modelRoot,"model.onnx"),options);
        using var stream=File.OpenRead(Path.Combine(modelRoot,"sentencepiece.bpe.model"));tokenizer=SentencePieceTokenizer.Create(stream,false,false);
    }
    internal void PrepareModel(CancellationToken cancellation=default){lock(gate){cancellation.ThrowIfCancellationRequested();EnsureModel();cancellation.ThrowIfCancellationRequested();}}
    internal long[] Tokenize(string text)
    {
        lock(gate){EnsureModel();var pieces=tokenizer!.EncodeToIds(text,false,false).Take(1022);return new long[]{0}.Concat(pieces.Select(x=>x==0?3L:x+1L)).Append(2).ToArray();}
    }
    internal float[] Encode(string text,CancellationToken cancellation=default)
    {
        lock(gate)
        {
            cancellation.ThrowIfCancellationRequested();EnsureModel();var ids=Tokenize(text);var shape=new[]{1,ids.Length};
            var inputs=new List<NamedOnnxValue>{NamedOnnxValue.CreateFromTensor("input_ids",new DenseTensor<long>(ids,shape)),NamedOnnxValue.CreateFromTensor("attention_mask",new DenseTensor<long>(Enumerable.Repeat(1L,ids.Length).ToArray(),shape))};
            if(session!.InputMetadata.ContainsKey("token_type_ids"))inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",new DenseTensor<long>(new long[ids.Length],shape)));
            using var runOptions=new RunOptions();using var registration=cancellation.Register(()=>runOptions.Terminate=true);
            using var result=session.Run(inputs,session.OutputMetadata.Keys.ToArray(),runOptions);
            // Official BAAI ONNX already exports dense_vecs/sentence_embedding. A hidden-state graph uses token 0 (CLS).
            var output=result.FirstOrDefault(x=>x.Name is "sentence_embedding" or "dense_vecs")??result.First();
            var tensor=output.AsTensor<float>();var values=tensor.ToArray();if(values.Length<Dimensions)throw new InvalidDataException("相似度模型输出维度不正确。");
            var vector=values.Take(Dimensions).ToArray();double norm=Math.Sqrt(vector.Sum(x=>(double)x*x));if(norm<=0||!double.IsFinite(norm))throw new InvalidDataException("相似度模型没有生成有效向量。");for(int i=0;i<vector.Length;i++)vector[i]/=(float)norm;cancellation.ThrowIfCancellationRequested();return vector;
        }
    }
    internal void EnsureIndex(WeChatStore store,bool rebuild=false)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(index!=null&&!rebuild)return;
            index?.Dispose();index=null;
            if(!rebuild&&store.Meta("vectorRevision","0")==store.Meta("indexRevision","-1")&&File.Exists(indexPath))try{index=new USearchIndex(indexPath);if(index.Dimensions()!=Dimensions){index.Dispose();index=null;}}catch{index=null;}
            if(index!=null)return;
            index=new USearchIndex(metricKind:MetricKind.Cos,quantization:ScalarKind.Float32,dimensions:Dimensions,connectivity:32,expansionAdd:128,expansionSearch:128);
            var snapshot=store.VectorSnapshot();foreach(var (id,v) in snapshot.Vectors)index.Add((ulong)id,v);SaveIndex();store.MarkIndexSaved(snapshot.Revision,clearDirty:true);
        }
    }
    internal void Add(long id,float[] vector){lock(gate){if(index==null)throw new InvalidOperationException("相似度索引尚未打开。");if(index.Contains((ulong)id))index.Remove((ulong)id);index.Add((ulong)id,vector);}}
    internal List<long> Search(float[] vector,int count=160){lock(gate){if(index==null||index.Size()==0)return [];index.Search(vector,count,out ulong[] keys,out float[] _);return keys.Select(x=>(long)x).ToList();}}
    internal void SaveIndex(){lock(gate){if(index==null)return;string temp=indexPath+".writing";index.Save(temp);File.Move(temp,indexPath,true);}}
    internal void Reset(){lock(gate){index?.Dispose();index=null;if(File.Exists(indexPath))File.Delete(indexPath);}}
    public void Dispose(){lock(gate){disposed=true;index?.Dispose();session?.Dispose();}}
}
