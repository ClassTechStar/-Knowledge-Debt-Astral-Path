using System.IO.Compression;
using System.Text;

namespace AstralPath.Core.Parsing;

/// <summary>
/// ZIP 容器读取（P1 迁移，源：Astralpath readZipEntries，L3210-3249）。
/// OOXML/EPUB 通用，零依赖；条目数/单条/总量上限保护；存储式直读 + 标准解压回退。
/// </summary>
public static class ZipReader
{
    public static IReadOnlyDictionary<string, byte[]> Read(byte[] zipBytes, int maxEntries = 4000, long maxSingle = 32 * 1024 * 1024, long maxTotal = 96 * 1024 * 1024)
    {
        var outMap = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var ms = new MemoryStream(zipBytes, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        if (zip.Entries.Count == 0) throw new InvalidDataException("ZIP 容器内没有可读取的条目");
        if (zip.Entries.Count > maxEntries) throw new InvalidDataException($"ZIP 条目数异常（{zip.Entries.Count}）");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrEmpty(name) || name.EndsWith("/")) continue;
            if (entry.Length > maxSingle) continue;
            byte[] data;
            using (var es = entry.Open())
            using (var buf = new MemoryStream())
            {
                es.CopyTo(buf);
                data = buf.ToArray();
            }
            if (data.Length == 0) continue;
            total += data.Length;
            if (total > maxTotal) break;
            outMap[name] = data;
        }
        if (outMap.Count == 0) throw new InvalidDataException("ZIP 容器内没有可读取的条目");
        return outMap;
    }

    /// <summary>字节流 → 文本（UTF-8 优先，BOM UTF-16LE 支持，回退 Latin-1）。</summary>
    public static string Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return "";
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            try { return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); } catch { }
        }
        try { return new UTF8Encoding(false).GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>HTML/XML 实体解码（数字 + 命名实体）。</summary>
    public static string DecodeEntities(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s, "&#x([0-9a-fA-F]+);", m =>
        {
            try { return char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Value, 16)); } catch { return ""; }
        });
        s = System.Text.RegularExpressions.Regex.Replace(s, "&#(\\d+);", m =>
        {
            try { return char.ConvertFromUtf32(int.Parse(m.Groups[1].Value)); } catch { return ""; }
        });
        return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
    }

    /// <summary>OOXML → 文本：段落/换行转 \n，其余标签剥离，实体解码在去标签之后（源 t10XmlText，L3262-3271）。</summary>
    public static string XmlText(string xml)
    {
        var s = xml ?? "";
        s = System.Text.RegularExpressions.Regex.Replace(s, "<(?:w|a):p\\b[^>]*/>", "\n");
        s = System.Text.RegularExpressions.Regex.Replace(s, "</(?:w|a):p>", "\n");
        s = System.Text.RegularExpressions.Regex.Replace(s, "<(?:w|a):br\\b[^>]*/>", "\n");
        s = s.Replace("<w:tab/>", "\t");
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        s = DecodeEntities(s);
        s = System.Text.RegularExpressions.Regex.Replace(s, "[ \t ]+", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, " ?\n ?", "\n");
        return System.Text.RegularExpressions.Regex.Replace(s, "\n{3,}", "\n\n").Trim();
    }

    /// <summary>HTML → 文本（源 t10HtmlText，L3272-3281）。</summary>
    public static string HtmlText(string html)
    {
        var s = html ?? "";
        s = System.Text.RegularExpressions.Regex.Replace(s, "<!--[\\s\\S]*?-->", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, "<script\\b[\\s\\S]*?</script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<style\\b[\\s\\S]*?</style>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "</(?:p|div|li|tr|h[1-6]|blockquote|section|article|figcaption)\\s*>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<br\\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        s = DecodeEntities(s);
        s = System.Text.RegularExpressions.Regex.Replace(s, "[ \t ]+", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, " ?\n ?", "\n");
        return System.Text.RegularExpressions.Regex.Replace(s, "\n{3,}", "\n\n").Trim();
    }
}
