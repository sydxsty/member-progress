using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace StudentProgress;

public sealed partial class AppService
{
    readonly HashSet<string> backupFiles=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Action<string>> backupValidators = new(StringComparer.OrdinalIgnoreCase);
    public void RegisterBackupValidator(string relativePath, Action<string> validator)
    {
        var path = relativePath.Replace('\\', '/');
        lock (gate)
        {
            if (!backupFiles.Contains(path)) throw new InvalidOperationException("备份资料尚未登记。");
            backupValidators[path] = validator;
        }
    }

    public void RegisterBackupFiles(IEnumerable<string> relativePaths)
    {
        var paths=relativePaths.Select(path=>path.Replace('\\','/')).ToArray();
        foreach(var path in paths)
        {
            var pieces=path.Split('/');
            if(Path.IsPathRooted(path)||pieces.Length<2||pieces.Any(x=>x.Length==0||x is "." or ".."||x.Any(c=>!char.IsLetterOrDigit(c)&&c is not ('.' or '-' or '_')))
                ||pieces.Any(x=>x.Equals("attachments",StringComparison.OrdinalIgnoreCase)||x.Equals("runtime",StringComparison.OrdinalIgnoreCase)||x.Equals("work",StringComparison.OrdinalIgnoreCase))
                ||pieces[^1].Contains("secret",StringComparison.OrdinalIgnoreCase)||pieces[^1].Contains("credential",StringComparison.OrdinalIgnoreCase)||pieces[^1].Contains("key",StringComparison.OrdinalIgnoreCase)||pieces[^1].Equals("auth.json",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("备份登记路径无效。");
        }
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            foreach(var path in paths)backupFiles.Add(path);
        }
    }

    JsonObject ExportBackup(string destination)
    {
        destination=Path.GetFullPath(destination);
        if(destination.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请选择 Data 文件夹以外的位置保存备份。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp=destination+".writing";
        try
        {
            using(var zip=ZipFile.Open(temp,ZipArchiveMode.Create))
            {
                var entry=zip.CreateEntry("state.json",CompressionLevel.Optimal);
                using(var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false)))writer.Write(state.ToJsonString());
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var msg in AllArchiveMessages(state))foreach(var f in (msg["attachments"] as JsonArray??new()).OfType<JsonObject>())
                {
                    var name=S(f,"storedName");if(!SafeStoredName(name)||!names.Add(name))continue;
                    var path=Path.Combine(root,"attachments",name);if(!File.Exists(path))throw new InvalidOperationException("有附件缺失，备份未完成。请保留当前 Data 文件夹。");
                    zip.CreateEntryFromFile(path,"attachments/"+name,CompressionLevel.Optimal);
                }
                foreach(var relative in backupFiles)
                {
                    var path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
                    if(File.Exists(path))zip.CreateEntryFromFile(path,relative,CompressionLevel.Optimal);
                }
            }
            File.Move(temp,destination,true);
            return new JsonObject{["path"]=destination};
        }
        catch{try{File.Delete(temp);}catch{}throw;}
    }

    void ImportBackup(string source)
    {
        source=Path.GetFullPath(source);
        if(!File.Exists(source))throw new InvalidOperationException("备份文件不存在。");
        var stage=Path.Combine(root,"restore-"+NewId());Directory.CreateDirectory(stage);Directory.CreateDirectory(Path.Combine(stage,"attachments"));
        try
        {
            using(var zip=ZipFile.OpenRead(source))
            {
                if(zip.Entries.Count>20000)throw new InvalidDataException("备份包含过多文件。");
                long total=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in zip.Entries)
                {
                    var name=entry.FullName;
                    if(name=="attachments/"&&entry.Length==0)continue;
                    if(name!="state.json"&&!(name.StartsWith("attachments/",StringComparison.Ordinal)&&SafeStoredName(name[12..]))&&!backupFiles.Contains(name))throw new InvalidDataException("备份包含无效路径。");
                    if(!seen.Add(name))throw new InvalidDataException("备份包含重复文件。");
                    total+=entry.Length;if(total>64L*1024*1024*1024||entry.Length>16L*1024*1024*1024)throw new InvalidDataException("备份解压后过大。");
                    var canonical=backupFiles.TryGetValue(name,out var registered)?registered:name;
                    var target=Path.Combine(stage,canonical.Replace('/',Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var from=entry.Open();using var to=new FileStream(target,FileMode.CreateNew,FileAccess.Write);
                    var buffer=new byte[81920];long copied=0;int count;
                    while((count=from.Read(buffer,0,buffer.Length))>0){copied+=count;if(copied>entry.Length||copied>16L*1024*1024*1024)throw new InvalidDataException("备份文件大小异常。");to.Write(buffer,0,count);}
                }
            }
            foreach (var (relative, validate) in backupValidators)
            {
                var path = Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path)) validate(path);
            }
            var incoming=ParseState(File.ReadAllText(Path.Combine(stage,"state.json")));
            foreach(var msg in AllArchiveMessages(incoming))foreach(var f in (msg["attachments"] as JsonArray??new()).OfType<JsonObject>())
                if(!File.Exists(Path.Combine(stage,"attachments",S(f,"storedName"))))throw new InvalidDataException("备份缺少附件，尚未恢复。");
            // Credentials and their destination remain the current user's settings, never archive-controlled.
            incoming["settings"]=state["settings"]!.DeepClone();incoming["status"]=state["status"]!.DeepClone();
            foreach(var scope in ((JsonArray)incoming["students"]!).Concat((JsonArray)incoming["projects"]!).OfType<JsonObject>())
                if(S(scope,"summaryStatus") is "running" or "pending"){scope["summaryStatus"]="error";scope["summaryError"]="恢复的摘要尚未完成，可点击重试。";}
            var recovery=Path.Combine(root,"recovery-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+NewId()[..6]);Directory.CreateDirectory(recovery);
            if(File.Exists(Path.Combine(root,"state.json")))File.Copy(Path.Combine(root,"state.json"),Path.Combine(recovery,"state.json"));
            var attachments=Path.Combine(root,"attachments");
            var restoredFiles=new List<(string Relative,bool Existed)>();
            Directory.Move(attachments,Path.Combine(recovery,"attachments"));
            try
            {
                Directory.Move(Path.Combine(stage,"attachments"),attachments);
                foreach(var relative in backupFiles)
                {
                    var relativePath=relative.Replace('/',Path.DirectorySeparatorChar);
                    var from=Path.Combine(stage,relativePath);if(!File.Exists(from))continue;
                    var destination=Path.Combine(root,relativePath);var existed=File.Exists(destination);
                    if(existed)
                    {
                        var saved=Path.Combine(recovery,"registered",relativePath);Directory.CreateDirectory(Path.GetDirectoryName(saved)!);File.Copy(destination,saved);
                    }
                    ReplaceRegisteredFile(from,destination);
                    restoredFiles.Add((relative,existed));
                }
                AdvanceStudentGrades(incoming,DateTimeOffset.Now);
                state=incoming;Save();
            }
            catch(Exception restoreError)
            {
                var rollbackErrors=new List<Exception>();
                void Rollback(Action action){try{action();}catch(Exception ex){rollbackErrors.Add(ex);}}
                foreach(var entry in restoredFiles.AsEnumerable().Reverse())
                {
                    var relativePath=entry.Relative.Replace('/',Path.DirectorySeparatorChar);
                    var destination=Path.Combine(root,relativePath);
                    Rollback(()=>
                    {
                        if(entry.Existed)ReplaceRegisteredFile(Path.Combine(recovery,"registered",relativePath),destination);
                        else if(File.Exists(destination))File.Delete(destination);
                    });
                }
                Rollback(()=>
                {
                    if(Directory.Exists(attachments))Directory.Delete(attachments,true);
                    Directory.Move(Path.Combine(recovery,"attachments"),attachments);
                });
                Rollback(()=>{if(File.Exists(Path.Combine(recovery,"state.json")))File.Copy(Path.Combine(recovery,"state.json"),Path.Combine(root,"state.json"),true);});
                if(rollbackErrors.Count>0)throw new InvalidOperationException("恢复未完成，部分文件暂时无法回滚。原始资料已保留在 Data 的 recovery 文件夹中。",new AggregateException(new[]{restoreError}.Concat(rollbackErrors)));
                throw;
            }
        }
        finally{try{Directory.Delete(stage,true);}catch{}}
    }

    static void ReplaceRegisteredFile(string source,string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary=destination+".restoring";
        try
        {
            File.Copy(source,temporary,true);File.Move(temporary,destination,true);
        }
        finally { if(File.Exists(temporary))File.Delete(temporary); }
    }
}
