using Microsoft.Web.WebView2.Core;

namespace StudentProgress;

public sealed partial class MainForm
{
    bool TryServeImage(CoreWebView2WebResourceRequestedEventArgs request, CoreWebView2Environment environment)
    {
        if (!IsTrusted(request.Request.Uri) || !Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var uri)) return false;
        bool wechat = uri.AbsolutePath.StartsWith("/wechat-image/", StringComparison.Ordinal);
        bool archive = uri.AbsolutePath.StartsWith("/archive-image/", StringComparison.Ordinal);
        if (!wechat && !archive) return false;
        try
        {
            string id = Uri.UnescapeDataString(uri.AbsolutePath[(wechat ? "/wechat-image/".Length : "/archive-image/".Length)..]);
            (string Path, string Mime)? image = null;
            if (id.Length is > 0 and <= 200 && !id.Any(c => c is '/' or '\\'))
            {
                if (wechat) image = _wechat?.ResolvePreviewImage(id);
                else
                {
                    var path = _service.ResolveAttachment(id);
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
