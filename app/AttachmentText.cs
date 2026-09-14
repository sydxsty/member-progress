using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace StudentProgress;

internal static class AttachmentText
{
    const int Limit=16000;
    internal sealed record Result(string Status,string Text);
    public static Result Extract(byte[] bytes,string ext)
    {
        try
        {
            string text;
            switch(ext.ToLowerInvariant())
            {
                case ".txt":case ".md":case ".markdown":
                    using(var r=new StreamReader(new MemoryStream(bytes),Encoding.UTF8,true))
                    {var chars=new char[Limit+1];var length=r.ReadBlock(chars,0,chars.Length);text=new string(chars,0,length);}
                    break;
                case ".docx":
                    using(var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read))
                    {
                        var entry=zip.GetEntry("word/document.xml");
                        if(entry==null||entry.Length>10*1024*1024)return new("仅归档：文档内容过大或格式不支持","");
                        using var stream=entry.Open();
                        using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=10*1024*1024});
                        var doc=XDocument.Load(reader);XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                        text=string.Join("\n",doc.Descendants(w+"p").Select(p=>string.Concat(p.Descendants(w+"t").Select(t=>t.Value))));
                    }
                    break;
                case ".pdf":
                    using(var doc=PdfDocument.Open(bytes))
                    {
                        var sb=new StringBuilder();
                        foreach(var page in doc.GetPages().Take(30)){sb.AppendLine(page.Text);if(sb.Length>Limit)break;}
                        text=sb.ToString();
                        if(string.IsNullOrWhiteSpace(text))return new("仅归档：PDF 未检测到可读取文字","");
                        if(doc.NumberOfPages>30&&text.Length<=Limit)return new("已提取前 30 页文字",text);
                    }
                    break;
                default:return new("仅归档：此格式不参与摘要","");
            }
            if(string.IsNullOrWhiteSpace(text))return new("仅归档：没有可读取文字","");
            return text.Length>Limit?new("已提取前 16000 字",text[..Limit]):new("已读取文字",text);
        }
        catch{return new("仅归档：文字读取失败，原文件已保存","");}
    }
}
