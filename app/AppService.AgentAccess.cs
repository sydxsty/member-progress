using StudentProgress.Assistant;

namespace StudentProgress
{
    public sealed partial class AppService
    {
        long assistantReadRevision = DateTime.UtcNow.Ticks;
        internal Func<string, CancellationToken, AssistantReadSession>? OpenReadSession { get; set; }
        internal AssistantNativeReadAccess? NativeReads { get; set; }
    }
}

namespace StudentProgress.WeChat
{
    public sealed partial class WeChatService
    {
        internal Func<string, CancellationToken, AssistantReadSession>? OpenReadSession { get; set; }
        internal AssistantNativeReadAccess? NativeReads { get; set; }
    }
}
