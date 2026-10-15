using System.Text;
using System.Text.RegularExpressions;

namespace AstralPath.Core.Parsing;

/// <summary>文档解析结果（引擎标识 + 尽力模式标注 + 文本）。</summary>
public sealed record DocParseResult(string Text, string Engine, bool BestEffort = false, string? Note = null);

/// <summary>
/// 多格式文档解析（P1 迁移，源：Astralpath extractDocxText/extractPptxText/extractEpubText/extractLegacyOfficeText）。
/// 覆盖 Word(.docx)、PowerPoint(.pptx)、EPUB、旧版二进制 Office(.doc/.ppt)；
/// 解析为确定性规则（不依赖模型），便于 golden 断言。
/// </summary>
public static class DocParsers
{
    // ── Word(.docx)（源 extractDocxText，L3283-3311）────────────────────
    public static DocParseResult ParseDocx(IReadOnlyDictionary<string, byte[]> entries)
    {
        if (!entries.TryGetValue("word/document.xml", out var main))
            return new DocParseResult("", "docx", BestEffort: true, Note: "缺少 word/document.xml");
        var xml = ZipReader.Decode(main);
        var lines = new List<string>();
        var re = new Regex("<w:p\\b[^>]*>([\\s\\S]*?)</w:p>");
        foreach (Match m in re.Matches(xml))
        {
            var body = m.Groups[1].Value;
            var lv = 0;
            var st = Regex.Match(body, "<w:pStyle\\s+w:val=\"([^\"]+)\"");
            if (st.Success)
            {
                var v = st.Groups[1].Value;
                var hm = Regex.Match(v, "^(?:Heading|标题)\\s*([1-3])$", RegexOptions.IgnoreCase);
                if (hm.Success) lv = int.Parse(hm.Groups[1].Value);
                else if (Regex.IsMatch(v, "^[1-3]$")) lv = int.Parse(v);
                else if (Regex.IsMatch(v, "^(?:heading|标题)", RegexOptions.IgnoreCase)) lv = 1;
            }
            var t = ZipReader.XmlText(body);
            if (t.Length == 0) continue;
            lines.Add(lv > 0 ? new string('#', lv) + " " + t : t);
        }
        var doc = string.Join("\n", lines);
        if (CountReadable(doc) < 200)
        {
            // 正文过薄：补充页眉/页脚/脚注（源 L3303-3308）
            var extra = new List<string>();
            foreach (var kv in entries)
                if (Regex.IsMatch(kv.Key, "^word/(?:header|footer|footnotes|endnotes)\\d*\\.xml$"))
                    extra.Add(ZipReader.XmlText(ZipReader.Decode(kv.Value)));
            if (extra.Count > 0) doc += "\n" + string.Join("\n", extra.Where(x => x.Length > 0));
        }
        return new DocParseResult(doc, "docx");
    }

    // ── PowerPoint(.pptx)（源 extractPptxText，L3313-3322）─────────────
    public static DocParseResult ParsePptx(IReadOnlyDictionary<string, byte[]> entries)
    {
        var slides = entries.Keys.Where(n => Regex.IsMatch(n, "^ppt/slides/slide\\d+\\.xml$"))
            .OrderBy(n => SlideNumber(n)).ToList();
        var outParts = new List<string>();
        for (var i = 0; i < slides.Count; i++)
            outParts.Add("[slide " + (i + 1) + "]\n" + ZipReader.XmlText(ZipReader.Decode(entries[slides[i]])));
        var notes = entries.Keys.Where(n => Regex.IsMatch(n, "^ppt/notesSlides/notesSlide\\d+\\.xml$"))
            .OrderBy(n => SlideNumber(n)).ToList();
        for (var i = 0; i < notes.Count; i++)
        {
            var t = ZipReader.XmlText(ZipReader.Decode(entries[notes[i]]));
            if (t.Length > 0) outParts.Add("[备注 " + (i + 1) + "]\n" + t);
        }
        return new DocParseResult(string.Join("\n", outParts), "pptx");
    }

    private static int SlideNumber(string name)
    {
        var m = Regex.Match(name, "(\\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    // ── EPUB（源 extractEpubText，L3324-3355）──────────────────────────
    public static DocParseResult ParseEpub(IReadOnlyDictionary<string, byte[]> entries)
    {
        if (!entries.TryGetValue("META-INF/container.xml", out var cont))
            throw new InvalidDataException("EPUB 缺少 META-INF/container.xml");
        var rootfile = Regex.Match(ZipReader.Decode(cont), "full-path\\s*=\\s*\"([^\"]+)\"");
        if (!rootfile.Success) throw new InvalidDataException("EPUB container.xml 未声明 rootfile");
        var opfPath = rootfile.Groups[1].Value.TrimStart('/');
        if (!entries.TryGetValue(opfPath, out var opf))
            throw new InvalidDataException("EPUB 缺少 OPF 文件（" + opfPath + "）");
        var xml = ZipReader.Decode(opf);
        var dir = Regex.Replace(opfPath, "[^/]*$", "");

        var man = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(xml, "<item\\b[^>]*/?>"))
        {
            var id = Regex.Match(m.Value, "id\\s*=\\s*\"([^\"]+)\"");
            var href = Regex.Match(m.Value, "href\\s*=\\s*\"([^\"]+)\"");
            if (id.Success && href.Success) man[id.Groups[1].Value] = ZipReader.DecodeEntities(href.Groups[1].Value);
        }

        var order = new List<string>();
        foreach (Match ms in Regex.Matches(xml, "<itemref\\b[^>]*/?>"))
        {
            var idr = Regex.Match(ms.Value, "idref\\s*=\\s*\"([^\"]+)\"");
            if (idr.Success && man.TryGetValue(idr.Groups[1].Value, out var h)) order.Add(h);
        }
        if (order.Count == 0) order.AddRange(man.Values);

        var outParts = new List<string>();
        foreach (var item in order.Take(600))
        {
            var h = item.TrimStart('/');
            var key = (dir + h).Replace("/./", "/");
            byte[]? f = entries.TryGetValue(key, out var e1) ? e1 : (entries.TryGetValue(h, out var e2) ? e2 : null);
            if (f is null) continue;
            var t = ZipReader.HtmlText(ZipReader.Decode(f));
            if (t.Length > 0) outParts.Add(t);
        }
        if (outParts.Count == 0) throw new InvalidDataException("EPUB 未解析出正文（spine 为空或资源缺失）");
        return new DocParseResult(string.Join("\n\n", outParts), "epub");
    }

    // ── 旧版二进制 Office(.doc/.ppt)（源 extractLegacyOfficeText，L3357-3385）──
    // UTF-16LE 文本游程尽力提取；序列噪声（码位连续递增 +1/+2 ≥60%）过滤字体表/二进制区伪文本。
    public static DocParseResult ParseLegacyOffice(byte[] bytes)
    {
        var runs = new List<string>();
        var cur = new List<int>();

        bool SequentialNoise(List<int> a)
        {
            if (a.Count < 8) return false;
            var seq = 0;
            for (var i = 1; i < a.Count; i++)
            {
                var d = a[i] - a[i - 1];
                if (d == 1 || d == 2) seq++;
            }
            return (double)seq / (a.Count - 1) >= 0.6;
        }

        void Flush()
        {
            if (cur.Count >= 4)
            {
                var s = new string(cur.Select(c => (char)c).ToArray());
                if (Regex.IsMatch(s, "[一-鿿A-Za-z0-9]") && !SequentialNoise(cur))
                    runs.Add(s);
            }
            cur.Clear();
        }

        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            var cp = bytes[i] | (bytes[i + 1] << 8);
            var ok = (cp >= 0x20 && cp <= 0x7E) || (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3000 && cp <= 0x303F)
                || (cp >= 0xFF00 && cp <= 0xFFEF) || (cp >= 0x2010 && cp <= 0x2027) || cp == 0x0A || cp == 0x0D || cp == 0x09;
            if (!ok) { Flush(); continue; }
            if (cp == 0x0D) continue;
            cur.Add(cp == 0x0A ? 0x0A : cp);
            if (cur.Count > 20000) Flush();
        }
        Flush();
        var text = Regex.Replace(string.Join("\n", runs), "\n{3,}", "\n\n").Trim();
        return new DocParseResult(text, "legacy-binary", BestEffort: true,
            Note: "旧版二进制格式为尽力提取（格式逆向不完整），建议核对结果");
    }

    // ── 纯文本类（HTML/Markdown/TXT）──────────────────────────────────
    public static DocParseResult ParseHtml(string html) => new(ZipReader.HtmlText(html), "html");
    public static DocParseResult ParseText(string text, string engine = "text") => new((text ?? "").Replace("\r\n", "\n"), engine);

    /// <summary>统一入口（源 resolveFileText 的非 OCR 分发，L1011-1037）：按扩展名路由。</summary>
    public static DocParseResult Parse(byte[] bytes, string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? "";
        switch (ext)
        {
            case ".docx":
            {
                var entries = ZipReader.Read(bytes);
                return entries.ContainsKey("word/document.xml")
                    ? ParseDocx(entries)
                    : throw new InvalidDataException("ZIP 容器不是受支持的 Office/EPUB 格式");
            }
            case ".pptx":
            {
                var entries = ZipReader.Read(bytes);
                return entries.Keys.Any(n => Regex.IsMatch(n, "^ppt/slides/slide\\d+\\.xml$"))
                    ? ParsePptx(entries)
                    : throw new InvalidDataException("ZIP 容器不是受支持的 Office/EPUB 格式");
            }
            case ".epub":
                return ParseEpub(ZipReader.Read(bytes));
            case ".doc":
            case ".ppt":
                return ParseLegacyOffice(bytes);
            case ".html":
            case ".htm":
                return ParseHtml(ZipReader.Decode(bytes));
            case ".md":
            case ".markdown":
                return ParseText(ZipReader.Decode(bytes), "markdown");
            case ".txt":
                return ParseText(ZipReader.Decode(bytes), "text");
            default:
                throw new NotSupportedException("不支持的文档格式：" + ext);
        }
    }

    /// <summary>可读字符数（源 countReadable，L353）：汉字/字母/数字。</summary>
    public static int CountReadable(string? s)
        => s is null ? 0 : Regex.Matches(s, "[一-鿿A-Za-z0-9]").Count;
}
