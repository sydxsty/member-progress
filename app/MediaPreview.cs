namespace StudentProgress;

// Serve only recognized raster bytes; file names and source metadata are not trusted MIME types.
internal static class MediaPreview
{
    internal static string Mime(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[32]; int length = input.Read(head); head = head[..length];
            if (head.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
            if (head.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
            if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return "image/gif";
            if (length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
            if (length >= 26 && head[..2].SequenceEqual("BM"u8)) return "image/bmp";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "";
    }
}
