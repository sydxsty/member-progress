using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace StudentProgress;

internal static class AttachmentText
{
    internal const string Version = "full-text-v2";
    const int PreviewLimit = 16000;
    internal const int AnalysisLimit = 1_000_000;
    const long XmlLimit = 64 * 1024 * 1024;
    internal sealed record Result(string Status, string Text);

    // The state file contains a small preview; analysis separately reads the archived original.
    public static Result Extract(byte[] bytes, string ext) => Read(bytes, ext, PreviewLimit, CancellationToken.None);
    internal static Result ExtractFull(byte[] bytes, string ext, CancellationToken cancellation = default)
        => Read(bytes, ext, AnalysisLimit, cancellation);

    static Result Read(byte[] bytes, string ext, int limit, CancellationToken cancellation)
    {
        try
        {
            var output = new StringBuilder();
            bool truncated = false;
            void Append(string value)
            {
                if (value.Length == 0) return;
                int remaining = limit - output.Length;
                if (value.Length > remaining) { output.Append(value.AsSpan(0, Math.Max(0, remaining))); truncated = true; }
                else output.Append(value);
            }
            string coverage = "已读取全部可提取文字";
            switch (ext.ToLowerInvariant())
            {
                case ".txt": case ".md": case ".markdown": case ".csv":
                    using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true))
                    {
                        char[] buffer = new char[8192]; int n;
                        while ((n = reader.Read(buffer)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested(); Append(new string(buffer, 0, n));
                            if (truncated) break;
                        }
                    }
                    break;
                case ".docx":
                    using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                    {
                        var main = zip.GetEntry("word/document.xml");
                        if (main == null) return new("仅归档：未找到文档正文", "");
                        foreach (var name in new[] { "word/document.xml", "word/footnotes.xml", "word/endnotes.xml" })
                        {
                            var entry = zip.GetEntry(name); if (entry == null) continue;
                            Append(name == "word/document.xml" ? "[文档正文]\n" : "\n[脚注/尾注]\n");
                            foreach (var paragraph in Paragraphs(entry, "http://schemas.openxmlformats.org/wordprocessingml/2006/main"))
                            { cancellation.ThrowIfCancellationRequested(); Append(paragraph + "\n"); if (truncated) break; }
                            if (truncated) break;
                        }
                    }
                    break;
                case ".pptx":
                    using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
                    {
                        var slides = zip.Entries.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
                            .OrderBy(e => int.Parse(System.Text.RegularExpressions.Regex.Match(e.FullName, @"\d+").Value)).ToList();
                        foreach (var slide in slides)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            Append("\n[" + Path.GetFileNameWithoutExtension(slide.FullName) + "]\n");
                            foreach (var paragraph in Paragraphs(slide, "http://schemas.openxmlformats.org/drawingml/2006/main"))
                            { Append(paragraph + "\n"); if (truncated) break; }
                            if (truncated) break;
                        }
                        coverage = $"已读取 {slides.Count} 张幻灯片中的文字；图片和讲者备注未提取";
                    }
                    break;
                case ".pdf":
                    using (var doc = PdfDocument.Open(bytes))
                    {
                        int readPages = 0; var emptyPages = new List<int>();
                        foreach (var page in doc.GetPages())
                        {
                            cancellation.ThrowIfCancellationRequested();
                            readPages++;
                            string pageText = page.Text;
                            if (string.IsNullOrWhiteSpace(pageText)) emptyPages.Add(readPages);
                            else Append($"\n[第 {readPages} 页]\n" + pageText + "\n");
                            if (truncated) break;
                        }
                        coverage = $"已检查 {readPages}/{doc.NumberOfPages} 页";
                        if (emptyPages.Count > 0)
                            coverage += $"；{emptyPages.Count} 页未检出文字（第 {string.Join("、", emptyPages.Take(30))}{(emptyPages.Count > 30 ? " 等" : "")} 页），未做 OCR";
                        coverage += "；图像中的文字、公式和图表细节可能未提取";
                    }
                    break;
                default: return new("仅归档：此格式没有可用的文字提取器", "");
            }
            if (string.IsNullOrWhiteSpace(output.ToString())) return new(coverage + "；没有可读取文字", "");
            if (truncated) coverage += $"；仅保留前 {limit:N0} 字，超出部分尚未读取";
            return new(coverage, output.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch { return new("仅归档：文字读取失败，原文件已保存", ""); }
    }

    static IEnumerable<string> Paragraphs(ZipArchiveEntry entry, string xmlNamespace)
    {
        if (entry.Length > XmlLimit) throw new InvalidDataException("文档 XML 超出读取限制。");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = XmlLimit });
        XNamespace ns = xmlNamespace;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "p" || reader.NamespaceURI != xmlNamespace) continue;
            using var subtree = reader.ReadSubtree();
            var paragraph = XElement.Load(subtree);
            yield return string.Concat(paragraph.Descendants(ns + "t").Select(t => t.Value));
        }
    }
}
