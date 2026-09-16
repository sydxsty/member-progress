using Microsoft.Web.WebView2.Core;

namespace StudentProgress;

public sealed partial class MainForm
{
    // This host intentionally has no folder mapping: WebView2 resolves mapped
    // hosts directly and bypasses WebResourceRequested for virtual files.
    internal const string MediaHost = "student-progress-media.local";

    bool TryServeImage(CoreWebView2WebResourceRequestedEventArgs request, CoreWebView2Environment environment)
    {
        if (!Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            !uri.Host.Equals(MediaHost, StringComparison.OrdinalIgnoreCase)) return false;
        bool wechat = uri.AbsolutePath.StartsWith("/wechat-image/", StringComparison.Ordinal);
        bool archive = uri.AbsolutePath.StartsWith("/archive-image/", StringComparison.Ordinal);
        bool assistant = uri.AbsolutePath.StartsWith("/assistant-image/", StringComparison.Ordinal);
        if (!wechat && !archive && !assistant) return false;
        try
        {
            string id = Uri.UnescapeDataString(uri.AbsolutePath[(wechat ? "/wechat-image/".Length : assistant ? "/assistant-image/".Length : "/archive-image/".Length)..]);
            (string Path, string Mime)? image = null;
            if (id.Length is > 0 and <= 200 && !id.Any(c => c is '/' or '\\'))
            {
                if (wechat) image = _wechat?.ResolvePreviewImage(id);
                else
                {
                    var path = assistant ? _assistant?.ResolveChatAttachment(id) : _service.ResolveAttachment(id);
                    if (path != null && MediaPreview.Mime(path) is { Length: > 0 } mime) image = (path, mime);
                }
            }
            if (image is { } found)
            {
                var stream = new FileStream(found.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                request.Response = environment.CreateWebResourceResponse(stream, 200, "OK",
                    "Content-Type: " + found.Mime + "\r\nCache-Control: private, max-age=3600\r\nX-Content-Type-Options: nosniff");
                return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        request.Response = environment.CreateWebResourceResponse(Stream.Null, 404, "Not Found", "Content-Type: text/plain\r\nCache-Control: no-store");
        return true;
    }
}
