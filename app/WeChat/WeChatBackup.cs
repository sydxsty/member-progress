using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace StudentProgress.WeChat;

public sealed partial class WeChatService
{
    // General backup registers only config.json and suggestions.json. Bulk records have this separate export.
    async Task<JsonObject> ExportAsync(string destination,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(destination))throw new InvalidOperationException("请选择微信资料导出位置。");destination=Path.GetFullPath(destination);if(destination.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请将微信资料导出到 Data/wechat 以外的位置。");
        await syncGate.WaitAsync(ct);try{await indexGate.WaitAsync(ct);}catch{syncGate.Release();throw;}var workspace=Path.Combine(root,"work","export-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(workspace);var temporary=destination+".writing";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);var snapshot=Path.Combine(workspace,"records.sqlite");store.BackupTo(snapshot);
            await Task.Run(()=>
            {
                using var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None);using var zip=new ZipArchive(output,ZipArchiveMode.Create);
                var manifest=zip.CreateEntry("wechat-export.json");using(var writer=new StreamWriter(manifest.Open(),new UTF8Encoding(false)))writer.Write(new JsonObject{["schemaVersion"]=1,["createdAt"]=DateTimeOffset.Now.ToString("O"),["kind"]="wechat-materials",["counts"]=store.Counts()}.ToJsonString());
                zip.CreateEntryFromFile(snapshot,"records.sqlite",CompressionLevel.Optimal);
                foreach(var path in Directory.EnumerateFiles(Path.Combine(root,"files"))){ct.ThrowIfCancellationRequested();if(Path.GetFileName(path).EndsWith(".copying"))continue;zip.CreateEntryFromFile(path,"files/"+Path.GetFileName(path),CompressionLevel.NoCompression);}
            },ct);
            File.Move(temporary,destination,true);return new(){["ok"]=true,["path"]=destination};
        }
        finally{try{Directory.Delete(workspace,true);}catch{}if(File.Exists(temporary))File.Delete(temporary);indexGate.Release();syncGate.Release();}
    }
    async Task<JsonObject> ImportAsync(string source,CancellationToken ct)
    {
        if(!File.Exists(source))throw new InvalidOperationException("微信资料备份不存在。");bool restored=false;await syncGate.WaitAsync(ct);try{await indexGate.WaitAsync(ct);}catch{syncGate.Release();throw;}var workspace=Path.Combine(root,"work","import-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(workspace);var recovery=Path.Combine(root,"recovery",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]);Directory.CreateDirectory(recovery);
        try
        {
            await Task.Run(()=>
            {
                using var zip=ZipFile.OpenRead(source);var manifest=zip.GetEntry("wechat-export.json")??throw new InvalidDataException("这不是微信资料备份。");using(var reader=new StreamReader(manifest.Open())){var metadata=JsonNode.Parse(reader.ReadToEnd());if(metadata?["schemaVersion"]?.GetValue<int>()!=1||S(metadata,"kind")!="wechat-materials")throw new InvalidDataException("微信资料备份版本不支持。");}
                var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();var name=entry.FullName;if(!seen.Add(name))throw new InvalidDataException("微信资料备份包含重复路径。");if(name=="wechat-export.json")continue;
                    if(name!="records.sqlite"&&(!name.StartsWith("files/",StringComparison.Ordinal)||name[6..].Length==0||name[6..]!=Path.GetFileName(name[6..])||name.Contains('\\')||name.Contains("..")))throw new InvalidDataException("微信资料备份包含无效路径。");
                    var target=Path.Combine(workspace,name.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(target)!);entry.ExtractToFile(target,false);
                }
                if(!File.Exists(Path.Combine(workspace,"records.sqlite")))throw new InvalidDataException("微信资料备份缺少记录库。");
                using(var candidate=new WeChatStore(workspace))
                {
                    foreach(var f in candidate.ArchiveCandidates().OfType<JsonObject>())foreach(var field in new[]{"storedName","originalStoredName"}){var stored=S(f,field);if(stored.Length>0&&(stored!=Path.GetFileName(stored)||stored.Contains('\\')||!File.Exists(Path.Combine(workspace,"files",stored))))throw new InvalidDataException("微信资料备份缺少原始文件。");}
                    candidate.BackupTo(Path.Combine(workspace,"validated.sqlite"));
                }
                // Content-addressed existing originals remain in place; restore only adds missing originals.
                var incomingFiles=Path.Combine(workspace,"files");if(Directory.Exists(incomingFiles))foreach(var f in Directory.EnumerateFiles(incomingFiles)){var target=Path.Combine(root,"files",Path.GetFileName(f));if(!File.Exists(target))File.Move(f,target);}
                store.ReplaceFrom(Path.Combine(workspace,"validated.sqlite"),Path.Combine(recovery,"records.sqlite"));embedding.Reset();store.SetMeta("vectorDirty","1");
            },ct);
            restored=true;SetStatus("idle","微信资料已恢复");return new(){["ok"]=true,["counts"]=store.Counts()};
        }
        finally{try{Directory.Delete(workspace,true);}catch{}indexGate.Release();syncGate.Release();if(restored)_=IndexInBackgroundAsync();}
    }
}
