namespace AstralPath.Infrastructure;

/// <summary>
/// 上传文件准入校验（P2-6）。
///
/// 背景：原实现接受任意扩展名——实测上传 `bad.exe` 返回 201，解析还报 `status=ready`，
/// 与提示文案「仅支持 PDF/图片/文本」矛盾，脏数据会直接进入图谱与题库。
///
/// 三级校验：① 扩展名白名单 ② 魔数（文件头）嗅探 ③ 文本可读率（纯文本类）。
/// 三者任一不过即拒绝，并回报允许的类型清单。
/// </summary>
public static class UploadGuard
{
    /// <summary>允许的扩展名（小写，含点）。</summary>
    public static readonly string[] AllowedExtensions =
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif",
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".log"
    };

    /// <summary>允许类型的展示文案（错误提示用）。</summary>
    public const string AllowedDisplay = "PDF / 图片（png/jpg/webp/bmp/tif/gif）/ 纯文本（txt/md/csv/tsv/json）";

    public sealed record Result(bool Ok, string? Error, string Kind);

    /// <summary>
    /// 校验文件名与文件头。
    /// </summary>
    /// <param name="fileName">用户原始文件名（含扩展名）。</param>
    /// <param name="head">文件起始若干字节（建议 ≥512B；不足则按实际长度）。</param>
    public static Result Validate(string fileName, ReadOnlySpan<byte> head)
    {
        var ext = Path.GetExtension(Path.GetFileName(fileName ?? "")).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
            return new Result(false, $"文件缺少扩展名。仅支持：{AllowedDisplay}", "unknown");
        if (!AllowedExtensions.Contains(ext))
            return new Result(false, $"不支持的资料类型「{ext}」。仅支持：{AllowedDisplay}", "unknown");

        // 文本类：不做魔数，改判可读率
        if (ext is ".txt" or ".md" or ".markdown" or ".csv" or ".tsv" or ".json" or ".log")
        {
            return IsMostlyText(head)
                ? new Result(true, null, "text")
                : new Result(false, "该文件扩展名是文本类型，但内容不是可读文本（可能是被改名的二进制文件）", "text");
        }

        var kind = SniffKind(head);
        if (kind == "unknown")
            return new Result(false, $"无法识别文件内容（扩展名 {ext} 与文件头不匹配）", "unknown");

        // 扩展名与文件头家族一致性
        var expected = ext switch
        {
            ".pdf" => "pdf",
            ".png" => "png",
            ".jpg" or ".jpeg" => "jpeg",
            ".webp" => "webp",
            ".bmp" => "bmp",
            ".tif" or ".tiff" => "tiff",
            ".gif" => "gif",
            _ => "unknown"
        };
        if (expected != "unknown" && kind != expected)
            return new Result(false, $"文件内容（{kind}）与扩展名（{ext}）不符，已拒绝", kind);

        return new Result(true, null, kind);
    }

    /// <summary>按文件头判断真实类型。</summary>
    public static string SniffKind(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 5 && h[0] == 0x25 && h[1] == 0x50 && h[2] == 0x44 && h[3] == 0x46) return "pdf"; // %PDF
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return "png";
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "jpeg";
        if (h.Length >= 12 && h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46
            && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50) return "webp"; // RIFF....WEBP
        if (h.Length >= 2 && h[0] == 0x42 && h[1] == 0x4D) return "bmp";
        if (h.Length >= 4 && ((h[0] == 0x49 && h[1] == 0x49 && h[2] == 0x2A) || (h[0] == 0x4D && h[1] == 0x4D && h[2] == 0x00))) return "tiff";
        if (h.Length >= 3 && h[0] == 0x47 && h[1] == 0x49 && h[2] == 0x46) return "gif";
        return "unknown";
    }

    /// <summary>可读文本判定：可打印字符（含中文）占比 ≥90%，且不含 NUL。</summary>
    public static bool IsMostlyText(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return false;
        if (head.IndexOf((byte)0x00) >= 0) return false; // 含 NUL → 二进制
        var printable = 0;
        foreach (var b in head)
        {
            // ASCII 可打印 + 制表/换行/回车；0x80 以上按「可能是 UTF-8 多字节」放宽
            if (b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E) || b >= 0x80) printable++;
        }
        return (double)printable / head.Length >= 0.90;
    }

    /// <summary>读取文件头（最多 512B）。</summary>
    public static byte[] ReadHead(Stream stream, int max = 512)
    {
        var buf = new byte[Math.Min(max, 4096)];
        var read = 0;
        while (read < buf.Length)
        {
            var n = stream.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read == buf.Length) return buf;
        var trimmed = new byte[read];
        Array.Copy(buf, trimmed, read);
        return trimmed;
    }
}
