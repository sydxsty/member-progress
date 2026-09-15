using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress;

// Opt-in integration check: one real agent invocation, operating only on a copied document.
internal static class ArchiveDocumentLiveCheck
{
    internal static int Run(string directory,string document,string? picture=null)
    {
        if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory).Any())return 1;
        Directory.CreateDirectory(directory);
        try{return RunAsync(directory,document,picture).GetAwaiter().GetResult();}
        catch(Exception e){File.WriteAllText(Path.Combine(directory,"document-live-result.json"),JsonSerializer.Serialize(new{ok=false,error=e.ToString()}));return 1;}
    }
    static async Task<int> RunAsync(string directory,string document,string? picture)
    {
        var file=new FileInfo(document);
        if(!file.Exists||file.Length>20*1024*1024)throw new InvalidOperationException("需要有效的隔离测试文档。");
        using var core=new AppService(directory);
        await core.HandleAsync("project.save",new(){["name"]="文档分析隔离联调",["studentIds"]=new JsonArray()});
        string id=core.Snapshot()["projects"]![0]!["id"]!.GetValue<string>();
        var files=new JsonArray(new JsonObject{["name"]="材料"+file.Extension,["mime"]="application/pdf",["dataBase64"]=Convert.ToBase64String(await File.ReadAllBytesAsync(document))});
        if(!string.IsNullOrEmpty(picture))files.Add(new JsonObject{["name"]="附图.png",["mime"]="image/png",["dataBase64"]=Convert.ToBase64String(await File.ReadAllBytesAsync(picture))});
        await core.HandleAsync("chat.send",new(){["scopeType"]="project",["scopeId"]=id,["text"]="请分析已归档材料，给出有依据的进展和具体下一步。"+(string.IsNullOrEmpty(picture)?"":"也请说明附图的主色和图标形状。"),["files"]=files});
        var deadline=DateTime.UtcNow.AddMinutes(13);JsonObject snapshot;
        do
        {
            await Task.Delay(400);snapshot=core.Snapshot();
            if(snapshot["projects"]![0]!["summaryStatus"]?.GetValue<string>() is not ("pending" or "running"))break;
        }while(DateTime.UtcNow<deadline);
        var summary=snapshot["messages"]!.AsArray().LastOrDefault(m=>m?["role"]?.GetValue<string>()=="assistant");
        bool ok=summary!=null&&snapshot["projects"]![0]!["summaryStatus"]?.GetValue<string>()=="idle"&&
            summary["documentAnalysis"]?["engine"]?.GetValue<string>()=="codex"&&
            (summary["documentAnalysis"]?["toolCalls"]?.GetValue<int>()??0)>0;
        File.WriteAllText(Path.Combine(directory,"document-live-result.json"),JsonSerializer.Serialize(new{ok,agentInvocations=1,summary,error=snapshot["projects"]![0]!["summaryError"]}));
        return ok?0:1;
    }
}
