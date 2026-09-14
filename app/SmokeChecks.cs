using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudentProgress;

// One bounded integration pass, always against a new isolated data directory.
internal static class SmokeChecks
{
    public static int Run(string dataDir)
    {
        var passed = new List<string>();
        try
        {
            if (Directory.Exists(dataDir) && Directory.EnumerateFileSystemEntries(dataDir).Any())
                throw new InvalidOperationException("Smoke checks require an empty data directory.");
            Directory.CreateDirectory(dataDir);
            RunAsync(dataDir, passed).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(dataDir, "smoke-result.json"), JsonSerializer.Serialize(new { ok = true, checks = passed, liveApiCalls = 0 }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "smoke-result.json"), JsonSerializer.Serialize(new { ok = false, checks = passed, error = ex.Message }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static JsonObject Payload(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    private static JsonArray Rows(JsonObject state, string name) => state[name]!.AsArray();
    private static string Id(JsonNode? row) => row!["id"]!.GetValue<string>();
    private static void Check(bool condition, string label, List<string> passed)
    {
        if (!condition) throw new InvalidOperationException(label);
        passed.Add(label);
    }

    private static async Task RunAsync(string dataDir, List<string> passed)
    {
        using var fakeAi = new FakeAiServer();
        string attachmentId;
        int messageCount;
        using (var app = new AppService(dataDir))
        {
            async Task Do(string action, object payload) => await app.HandleAsync(action, Payload(payload));
            await Do("settings.save", new { baseUrl = fakeAi.BaseUrl, model = "smoke-model", apiMode = "responses", key = "smoke-secret-local-only" });
            Check(Rows(app.Snapshot(), "students").Count == 0, "empty_initial_roster", passed);
            await Do("student.save", new { name = "验证甲" });
            var a = Id(Rows(app.Snapshot(), "students")[0]);
            await Do("student.save", new { name = "验证乙", grade = "研二", tags = new[] { "联培" }, bio = "测试用临时记录" });
            var b = Id(Rows(app.Snapshot(), "students")[1]);
            await Do("project.save", new { name = "共享任务验证", studentIds = new[] { a, b } });
            var project = Id(Rows(app.Snapshot(), "projects")[0]);
            await Do("task.add", new { title = "整理实验结果", scopeType = "project", scopeId = project });
            var task = Id(Rows(app.Snapshot(), "tasks")[0]);
            Check(Rows(app.Snapshot(), "tasks")[0]!["studentIds"]!.AsArray().Count == 2, "single_shared_task_links_both_students", passed);
            await Do("task.toggle", new { id = task });
            Check(Rows(app.Snapshot(), "tasks")[0]!["done"]!.GetValue<bool>(), "one_click_complete", passed);
            await Do("task.toggle", new { id = task });
            Check(!Rows(app.Snapshot(), "tasks")[0]!["done"]!.GetValue<bool>(), "one_click_undo", passed);
            await Do("chat.send", new
            {
                scopeType = "project", scopeId = project, text = "初步实验已完成，请整理结果。", completeTaskId = task,
                files = new[] { new { name = "结果.txt", mime = "text/plain", dataBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("对比实验结果：两组数据已整理。")) } }
            });
            var immediately = app.Snapshot();
            Check(Rows(immediately, "messages").Any(m => m!["role"]!.GetValue<string>() == "user") && Rows(immediately, "tasks")[0]!["done"]!.GetValue<bool>(), "archive_saved_before_ai_and_submit_completes", passed);
            var userMessage = Rows(immediately, "messages").First(m => m!["role"]!.GetValue<string>() == "user")!;
            attachmentId = Id(userMessage["attachments"]!.AsArray()[0]);
            Check(File.Exists(app.ResolveAttachment(attachmentId)), "attachment_copied_to_local_archive", passed);
            var until = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < until && !Rows(app.Snapshot(), "messages").Any(m => m!["role"]!.GetValue<string>() == "assistant"))
                await Task.Delay(150);
            Check(Rows(app.Snapshot(), "messages").Any(m => m!["role"]!.GetValue<string>() == "assistant") && !string.IsNullOrWhiteSpace(Rows(app.Snapshot(), "projects")[0]!["currentSummary"]?.GetValue<string>()), "automatic_summary_appends_and_updates_current_task", passed);
            Check(fakeAi.Count == 1, "one_coalesced_summary_request", passed);
            Check(!app.Snapshot().ToJsonString().Contains("smoke-secret-local-only"), "secrets_not_exposed_to_ui", passed);

            var now = DateTimeOffset.Now;
            await Do("alarm.save", new { scopeType = "global", title = "全局验证", when = now.AddMinutes(-1).ToString("O"), repeat = "once" });
            await Do("alarm.save", new { scopeType = "student", studentId = a, title = "学生验证", when = now.AddDays(-1).ToString("O"), repeat = "daily" });
            var due = (await app.HandleAsync("reminder.poll", Payload(new { now = now.ToString("O") })))!.AsArray();
            var again = (await app.HandleAsync("reminder.poll", Payload(new { now = now.ToString("O") })))!.AsArray();
            Check(due.Count == 2 && again.Count == 0, "global_student_reminders_claimed_once", passed);
            await Do("reminder.snooze", new { id = Id(due[0]), minutes = 10 });
            var snoozed = Rows(app.Snapshot(), "alarms").First(x => Id(x) == Id(due[0]))!;
            Check(snoozed["enabled"]!.GetValue<bool>() && DateTimeOffset.Parse(snoozed["when"]!.GetValue<string>()) > now, "reminder_snooze_persists", passed);

            messageCount = Rows(app.Snapshot(), "messages").Count;
            var export = Path.Combine(Path.GetDirectoryName(dataDir)!, Path.GetFileName(dataDir) + "-backup.zip");
            await Do("backup.export", new { path = export });
            using (var zip = ZipFile.OpenRead(export))
            {
                Check(zip.Entries.Any(e => e.FullName.EndsWith("state.json")) && zip.Entries.All(e => !e.FullName.Contains("secret", StringComparison.OrdinalIgnoreCase)), "backup_contains_records_without_secret_file", passed);
            }
            await Do("task.add", new { title = "恢复时应移除的新增项", scopeType = "student", scopeId = a });
            await Do("backup.import", new { path = export });
            Check(Rows(app.Snapshot(), "tasks").Count == 1 && Rows(app.Snapshot(), "messages").Count == messageCount && File.Exists(app.ResolveAttachment(attachmentId)), "backup_restore_preserves_chat_and_files", passed);
        }
        using (var reopened = new AppService(dataDir))
        {
            Check(Rows(reopened.Snapshot(), "students").Count == 2 && Rows(reopened.Snapshot(), "messages").Count == messageCount && File.Exists(reopened.ResolveAttachment(attachmentId)), "restart_preserves_records_and_attachment_links", passed);
        }
    }

    private sealed class FakeAiServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancel = new();
        private int count;
        public int Count => Volatile.Read(ref count);
        public string BaseUrl { get; }
        public FakeAiServer()
        {
            listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            _ = Task.Run(Loop);
        }
        private async Task Loop()
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(cancel.Token);
                    using var stream = client.GetStream();
                    using var bytes = new MemoryStream();
                    var buffer = new byte[8192];
                    int headerEnd = -1, length = 0;
                    while (true)
                    {
                        int n = await stream.ReadAsync(buffer, cancel.Token);
                        if (n == 0) break;
                        bytes.Write(buffer, 0, n);
                        var received = bytes.ToArray();
                        if (headerEnd < 0)
                        {
                            var headers = Encoding.ASCII.GetString(received);
                            var pos = headers.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (pos >= 0)
                            {
                                headerEnd = pos + 4;
                                var line = headers[..pos].Split("\r\n").FirstOrDefault(s => s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                                if (line != null) int.TryParse(line.Split(':')[1].Trim(), out length);
                            }
                        }
                        if (headerEnd >= 0 && received.Length >= headerEnd + length) break;
                        if (received.Length > 1_000_000) throw new IOException("Unexpected request size");
                    }
                    Interlocked.Increment(ref count);
                    var content = JsonSerializer.Serialize(new { summary = "已完成初步实验，结果已归档。", currentTask = "整理实验结果，补充对比说明。", suggestions = new[] { new { title = "补充对比说明" } } });
                    var response = JsonSerializer.Serialize(new { id = "smoke-response", status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = content } } } } });
                    var body = Encoding.UTF8.GetBytes(response);
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, cancel.Token);
                    await stream.WriteAsync(body, cancel.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) when (cancel.IsCancellationRequested) { break; }
            }
        }
        public void Dispose() { cancel.Cancel(); listener.Stop(); cancel.Dispose(); }
    }
}
