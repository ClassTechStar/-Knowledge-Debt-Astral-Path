using System.Text;
using System.Text.Json;
using AstralPath.Core.Parsing;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// P1 迁移验证：多格式文档解析 golden 契约（eval/golden/doc_parse.json）。
/// 覆盖 docx 标题层级、pptx 幻灯片/备注块、epub spine 顺序、旧版二进制游程提取（含序列噪声过滤）、
/// html 去标签与实体解码、可读字符计数。
/// </summary>
public class GoldenDocParseTests
{
    private static JsonDocument Load()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_GOLDEN_DIR");
        var dir = !string.IsNullOrWhiteSpace(env) && Directory.Exists(env) ? env : FindGoldenDir();
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "doc_parse.json")));
    }

    private static string FindGoldenDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var full = Path.Combine(dir, "eval", "golden");
            if (Directory.Exists(full)) return Path.GetFullPath(full);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        }
        throw new DirectoryNotFoundException("eval/golden not found");
    }

    private static void AssertContainsAll(string? id, string text, JsonElement expect)
    {
        if (expect.TryGetProperty("expectContains", out var arr))
            foreach (var needle in arr.EnumerateArray())
                Assert.Contains(needle.GetString()!, text);
        if (expect.TryGetProperty("expectNotContains", out var nArr))
            foreach (var needle in nArr.EnumerateArray())
                Assert.DoesNotContain(needle.GetString()!, text);
    }

    [Fact]
    public void Golden_DocParse_Docx_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("docxCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (c.GetProperty("documentXml").ValueKind == JsonValueKind.String)
                entries["word/document.xml"] = Encoding.UTF8.GetBytes(c.GetProperty("documentXml").GetString()!);
            var result = DocParsers.ParseDocx(entries);
            Assert.Equal(c.GetProperty("expectEngine").GetString(), result.Engine);
            if (c.TryGetProperty("expectBestEffort", out var be))
                Assert.Equal(be.GetBoolean(), result.BestEffort);
            AssertContainsAll(id, result.Text, c);
        }
    }

    [Fact]
    public void Golden_DocParse_Pptx_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("pptxCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var kv in c.GetProperty("slides").EnumerateObject())
                entries[kv.Name] = Encoding.UTF8.GetBytes(kv.Value.GetString()!);
            foreach (var kv in c.GetProperty("notes").EnumerateObject())
                entries[kv.Name] = Encoding.UTF8.GetBytes(kv.Value.GetString()!);
            var result = DocParsers.ParsePptx(entries);
            Assert.Equal(c.GetProperty("expectEngine").GetString(), result.Engine);
            AssertContainsAll(id, result.Text, c);
        }
    }

    [Fact]
    public void Golden_DocParse_Epub_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("epubCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            if (c.TryGetProperty("expectThrows", out var th) && th.GetBoolean())
            {
                var empty = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                if (c.GetProperty("container").ValueKind == JsonValueKind.Null)
                {
                    Assert.ThrowsAny<Exception>(() => DocParsers.ParseEpub(empty));
                    continue;
                }
            }
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            entries["META-INF/container.xml"] = Encoding.UTF8.GetBytes(c.GetProperty("container").GetString()!);
            entries["OEBPS/content.opf"] = Encoding.UTF8.GetBytes(c.GetProperty("opf").GetString()!);
            foreach (var kv in c.GetProperty("xhtml").EnumerateObject())
                entries[kv.Name] = Encoding.UTF8.GetBytes(kv.Value.GetString()!);
            var result = DocParsers.ParseEpub(entries);
            Assert.Equal(c.GetProperty("expectEngine").GetString(), result.Engine);
            AssertContainsAll(id, result.Text, c);
            if (c.TryGetProperty("expectOrder", out var order))
            {
                var seq = order.EnumerateArray().Select(x => x.GetString()!).ToList();
                var indices = seq.Select(s => result.Text.IndexOf(s, StringComparison.Ordinal)).ToList();
                Assert.All(indices, ix => Assert.True(ix >= 0));
                for (var i = 1; i < indices.Count; i++)
                    Assert.True(indices[i - 1] < indices[i], $"{id}: spine 顺序错误 {string.Join(",", seq)}");
            }
        }
    }

    [Fact]
    public void Golden_DocParse_Legacy_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("legacyCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            using var ms = new MemoryStream();
            // 文本游程：UTF-16LE，游程间以非法码位（0x0001）分隔
            void WriteRun(string s)
            {
                foreach (var ch in s) { var b = BitConverter.GetBytes((ushort)ch); ms.Write(b); }
                ms.WriteByte(0x01); ms.WriteByte(0x00); // 非法码位 → flush
            }
            foreach (var run in c.GetProperty("textRuns").EnumerateArray())
                WriteRun(run.GetString()!);
            // 序列噪声游程：码位连续递增（+1）的伪 UTF-16 文本（字体表噪声）→ 须被过滤
            foreach (var nr in c.GetProperty("noiseRuns").EnumerateArray())
            {
                var noise = nr.GetString()!;
                for (var i = 0; i < noise.Length; i++)
                {
                    var b = BitConverter.GetBytes((ushort)noise[i]);
                    ms.Write(b);
                }
                ms.WriteByte(0x01); ms.WriteByte(0x00);
            }
            var result = DocParsers.ParseLegacyOffice(ms.ToArray());
            Assert.Equal(c.GetProperty("expectEngine").GetString(), result.Engine);
            Assert.True(result.BestEffort, $"{id}: legacy 须标注尽力模式");
            AssertContainsAll(id, result.Text, c);
        }
    }

    [Fact]
    public void Golden_DocParse_Html_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("htmlCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var result = DocParsers.ParseHtml(c.GetProperty("input").GetString()!);
            Assert.Equal(c.GetProperty("expectEngine").GetString(), result.Engine);
            AssertContainsAll(id, result.Text, c);
        }
    }

    [Fact]
    public void Golden_DocParse_CountReadable_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("countReadableCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var actual = DocParsers.CountReadable(c.GetProperty("input").GetString());
            Assert.True(actual == c.GetProperty("expect").GetInt32(),
                $"{id}: expect={c.GetProperty("expect").GetInt32()} actual={actual}");
        }
    }

    [Fact]
    public void DocParsers_ParseDispatch_ByExtension()
    {
        // 统一入口分发：docx → word/document.xml 检测；不支持的扩展名抛 NotSupported
        var xml = Encoding.UTF8.GetBytes("<w:document><w:body><w:p><w:r><w:t>正文</w:t></w:r></w:p></w:body></w:document>");
        using (var ms = new MemoryStream())
        {
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var e = zip.CreateEntry("word/document.xml");
                using var es = e.Open();
                es.Write(xml);
            }
            var r = DocParsers.Parse(ms.ToArray(), "test.docx");
            Assert.Equal("docx", r.Engine);
            Assert.Contains("正文", r.Text);
        }
        Assert.Throws<NotSupportedException>(() => DocParsers.Parse(new byte[] { 1 }, "test.xyz"));
    }
}
