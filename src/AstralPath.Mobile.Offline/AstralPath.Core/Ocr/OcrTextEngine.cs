namespace AstralPath.Core.Ocr;

/// <summary>
/// OCR v2 文本引擎：置信度加权、多配置投票、中文断行、常见误识修复、噪声过滤、质量评分。
/// 纯函数，可单测；不依赖 IO / 网络。
/// </summary>
public static class OcrTextEngine
{
    // ── 1. 常见误识修复 ─────────────────────────────────
    /// <summary>字符级混淆表（OCR 经典）。</summary>
    private static readonly (string Bad, string Good)[] CharConfusions =
    {
        ("0O", "OO"), // 上下文里再判
        ("l1", "11"),
        ("｜", "|"),
        ("【", "["),
        ("】", "]"),
        ("（", "("),
        ("）", ")"),
        ("，", ","),
        ("。", "."),
        ("：", ":"),
        ("；", ";"),
    };

    /// <summary>词内数字/字母混淆：治「会计等式」变「会计等式」类破损。</summary>
    private static readonly (string Pattern, string Repl)[] WordFixes =
    {
        ("0", "O"), // 在纯字母词内由 FixAlphaNumeric 处理
    };

    /// <summary>
    /// 英数混淆：字母包围的 0/O、1/l/I 仅在「明显词长≥3」时启发式替换。
    /// 保守：只在 pattern 命中时改。
    /// </summary>
    public static string FixConfusions(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        // 全角标点归一
        s = s.Replace('　', ' ').Replace('\x00', ' ');
        s = s.Replace("（", "(").Replace("）", ")")
             .Replace("［", "[").Replace("］", "]")
             .Replace("，", ",").Replace("；", ";")
             .Replace("：", ":").Replace("？", "?")
             .Replace("！", "!").Replace("“", "\"").Replace("”", "\"")
             .Replace("＝", "=").Replace("＋", "+").Replace("－", "-").Replace("×", "*");

        // 字母串中的 0 → O，1 → l（仅当串为纯字母数字且含字母）
        s = System.Text.RegularExpressions.Regex.Replace(
            s,
            @"\b[A-Za-z][A-Za-z0-9]{2,}\b",
            m =>
            {
                var w = m.Value;
                var hasAlpha = w.Any(char.IsLetter);
                var hasDigit = w.Any(char.IsDigit);
                if (!hasAlpha || !hasDigit) return w;
                // 技术词保留数字（C02、N1）
                if (System.Text.RegularExpressions.Regex.IsMatch(w, @"^[A-Z]\d+$")) return w;
                return w.Replace('0', 'O').Replace('1', 'l').Replace('5', 'S').Replace('8', 'B');
            });

        // 中文里常见：未/末、日/曰 不做全文替换（过险）
        // 断词粘连修复：汉字与字母数字粘连加空格
        s = System.Text.RegularExpressions.Regex.Replace(s, @"([一-鿿])([A-Za-z])", "$1 $2");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"([A-Za-z])([一-鿿])", "$1 $2");
        return s;
    }

    // ── 2. 中文断行 / 页眉页脚 ─────────────────────────
    /// <summary>
    /// 中文软断行合并：行尾无标点且下一行是汉字 → 去掉断字符合并。
    /// </summary>
    public static string ReconstructCjkLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        var outLines = new List<string>();
        var buf = "";
        foreach (var line in lines)
        {
            if (buf.Length == 0) { buf = line; continue; }

            var bufIsCjk = IsMostlyCjk(buf);
            var lineIsCjk = IsMostlyCjk(line);
            var endsSoft = !buf.EndsWith('。') && !buf.EndsWith('！') && !buf.EndsWith('？')
                           && !buf.EndsWith('.') && !buf.EndsWith(':') && !buf.EndsWith('：')
                           && !buf.EndsWith(';') && !buf.EndsWith('；');
            var startsLower = line.Length > 0 && char.IsLower(line[0]);
            // 中文段落软断 → 合并；英文词中断 → 加连字或空格
            if (bufIsCjk && lineIsCjk && endsSoft && buf.Length < 80)
            {
                buf += line.TrimStart();
                continue;
            }
            if (bufIsCjk && !lineIsCjk && endsSoft && !startsLower && line.Length < 24)
            {
                // 短的英文小尾巴（公式/题号）并入
                buf += " " + line;
                continue;
            }
            outLines.Add(buf);
            buf = line;
        }
        if (buf.Length > 0) outLines.Add(buf);
        return string.Join("\n", outLines);
    }

    private static bool IsMostlyCjk(string s)
    {
        if (s.Length == 0) return false;
        var cjk = s.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        return cjk * 2 >= s.Length;
    }

    // ── 3. 噪声过滤 ─────────────────────────────────────
    private static readonly HashSet<string> NoiseExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "ocr", "tesseract", "scanned by", "copyrighted material",
        "更多书籍请关注", "仅供个人使用", "—", "…", "@", "#", "$"
    };

    /// <summary>去页眉页脚式噪声行；保留数字行（页码/公式）。</summary>
    public static string FilterNoise(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r", "").Split('\n');
        var kept = new List<string>();
        foreach (var raw in lines)
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;
            if (s.Length == 1 && !char.IsDigit(s[0])) continue;
            if (s.Length <= 8 && System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\W_]+$")) continue;
            if (NoiseExact.Contains(s)) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^scanned by .{0,24}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            // 全大写长页眉
            if (s.Length > 20 && s.All(c => !char.IsLetter(c) || char.IsUpper(c)) && s.Count(char.IsLetter) > 4)
            {
                // 标题允许：含有空格分词的短标题更可能是章节
                if (s.Count(char.IsLetter) > 30) continue;
            }
            kept.Add(s);
        }
        return string.Join("\n", kept);
    }

    // ── 4. 置信度加权 ───────────────────────────────────
    public sealed record WordTok(string Text, double Confidence, double X, double Y);

    /// <summary>
    /// 按 TSV 词置信度过滤：conf&lt;minConf 丢弃；行内按 X 排序，行按 Y 聚类。
    /// </summary>
    public static string ReconstructFromTsvWords(IReadOnlyList<WordTok> words, double minConf = 40)
    {
        var kept = words
            .Where(w => w.Text.Length > 0 && w.Confidence >= minConf)
            .ToList();
        if (kept.Count == 0) return "";

        // Y 聚类（行高容差 8px）
        var rows = new List<List<WordTok>>();
        foreach (var w in kept.OrderBy(w => w.Y).ThenBy(w => w.X))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].Y - w.Y) <= 8);
            if (row is null) { row = new List<WordTok>(); rows.Add(row); }
            row.Add(w);
        }
        var lines = rows
            .OrderBy(r => r.Average(w => w.Y))
            .Select(r => string.Join(" ", r.OrderBy(w => w.X).Select(w => w.Text)));
        return string.Join("\n", lines);
    }

    /// <summary>多配置投票：同一 token 在 n 路出现 ≥⌈n/2⌉ 才保留；位置邻近合并。</summary>
    public static string VotePasses(IReadOnlyList<string> passes, double keepRatio = 0.5)
    {
        var lists = passes.Select(p => p.Split('\n')).ToList();
        if (lists.Count == 0) return "";
        if (lists.Count == 1) return lists[0][0] is null ? "" : string.Join("\n", lists[0]);

        // 用二元组（行文本）做多数票——行级投票更稳
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var lines in lists)
        {
            foreach (var distinct in lines.Select(l => l.Trim()).Where(l => l.Length > 1).Distinct())
                counts[distinct] = counts.GetValueOrDefault(distinct) + 1;
        }
        var need = Math.Ceiling(lists.Count * keepRatio);
        var order = new List<string>();
        // 保留首次出现顺序
        foreach (var lines in lists[0])
        {
            var s = lines.Trim();
            if (s.Length > 1 && counts.GetValueOrDefault(s) >= need) order.Add(s);
        }
        // 补上其它 pass 中多数但 pass0 缺的
        foreach (var kv in counts.Where(kv => kv.Value >= need))
        {
            if (!order.Contains(kv.Key)) order.Add(kv.Key);
        }
        return string.Join("\n", order);
    }

    // ── 5. 质量评分 ─────────────────────────────────────
    public sealed record QualityReport(
        double MeanConf,
        double CjkRatio,
        double NoiseRatio,
        int LineCount,
        string Grade); // A/B/C/D

    public static QualityReport Score(string text, IReadOnlyList<WordTok>? words = null)
    {
        var meanConf = words is { Count: > 0 } ? words.Average(w => w.Confidence) / 100.0 : 0.7;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var cjk = text.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        var letters = text.Count(char.IsLetter);
        var cjkRatio = letters == 0 ? 0 : (double)cjk / letters;
        var noise = lines.Count(l =>
            l.Length <= 8 && System.Text.RegularExpressions.Regex.IsMatch(l, @"^[\W_]+$"));
        var noiseRatio = lines.Length == 0 ? 0 : (double)noise / lines.Length;
        var score = 0.5 * meanConf + 0.3 * Math.Min(cjkRatio / 0.6, 1.0) + 0.2 * (1 - noiseRatio);
        var grade = score >= 0.845 ? "A" : score >= 0.7 ? "B" : score >= 0.5 ? "C" : "D";
        return new QualityReport(Math.Round(meanConf, 4), Math.Round(cjkRatio, 4),
            Math.Round(noiseRatio, 4), lines.Length, grade);
    }

    /// <summary>一站式：混淆修复 → 噪声过滤 → 中文断行合并。</summary>
    public static string Refine(string raw)
        => ReconstructCjkLines(FilterNoise(FixConfusions(raw)));
}
