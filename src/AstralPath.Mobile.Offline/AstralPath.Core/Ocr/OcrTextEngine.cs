using System.Text.RegularExpressions;

namespace AstralPath.Core.Ocr;

/// <summary>
/// OCR v3 文本引擎：混淆修复（带标识符保护）/ 页眉页脚去除 / 英文断字还原 /
/// 中文软断行合并（带分栏与列表保护）/ TSV 置信度重建 / 多配置模糊投票 / 质量评分。
/// 纯函数，可单测；不依赖 IO / 网络。
///
/// 相对 v2 的关键修复：
/// ① FixConfusions 的 0→O / 1→l / 5→S / 8→B 是「无差别替换」，
///    "Win10"→"WinlO"、"ISO9001"→"ISO9OOl"，把真实标识符直接腐蚀。
///    v3 先做「标识符/版本号/代码」识别，命中就跳过替换。
/// ② 新增 RemoveRepeatedBoilerplate：跨页重复的页眉页脚自动去除（教材 OCR 最大噪声源）
/// ③ 新增 Dehyphenate：英文行末断字还原（inter-national → international）
/// ④ ReconstructCjkLines 增加保护：不合并列表项 / 编号行 / 疑似标题
/// ⑤ ReconstructFromTsvWords：v2 拿「本行第一个词的 Y」做聚类基准，
///    遇到基线漂移（扫描歪斜）就串行；v3 改为按 Y 排序后的「间隙聚类」
/// ⑥ VotePasses：v2 用整行精确匹配投票，三路 PSM 稍有差异就全部投不中；
///    v3 先归一化（去空白/全角）再分组投票，并用 HashSet 修掉 O(n²) 的 Contains
/// </summary>
public static class OcrTextEngine
{
    // ── 1. 混淆修复（带保护）────────────────────────────
    private static readonly (string Bad, string Good)[] FullWidthPairs =
    {
        ("（", "("), ("）", ")"), ("［", "["), ("］", "]"),
        ("，", ","), ("；", ";"), ("：", ":"), ("？", "?"), ("！", "!"),
        ("“", "\""), ("”", "\""), ("＝", "="), ("＋", "+"), ("－", "-"), ("×", "*")
    };

    /// <summary>标识符/版本号/代码类 token：数字是真实语义，禁止替换为字母。</summary>
    private static bool IsIdentifierLike(string w)
    {
        if (w.Contains('_')) return true;                              // 代码标识符
        if (Regex.IsMatch(w, @"^[A-Za-z]{1,4}\d{1,3}$")) return true;  // C02 / GPT4 / H2
        if (Regex.IsMatch(w, @"^\d{2,}")) return true;                 // 2024 开头
        if (Regex.IsMatch(w, @"\d{2,}")) return true;                  // 含连续数字（版本/编号）
        if (Regex.IsMatch(w, @"[a-z][A-Z]")) return true;              // camelCase → 代码
        if (Regex.IsMatch(w, @"^[A-Z]{2,}\d")) return true;            // ISO9001 / UTF8
        return false;
    }

    public static string FixConfusions(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text.Replace('　', ' ').Replace('\x00', ' ');
        foreach (var (bad, good) in FullWidthPairs) s = s.Replace(bad, good);

        // 数字→字母：仅对「非标识符 + 长度≥4」的普通词生效，且只改单个数位
        s = Regex.Replace(
            s,
            @"\b[A-Za-z][A-Za-z0-9]{2,}\b",
            m =>
            {
                var w = m.Value;
                var hasAlpha = w.Any(char.IsLetter);
                var hasDigit = w.Any(char.IsDigit);
                if (!hasAlpha || !hasDigit) return w;
                if (IsIdentifierLike(w)) return w;          // ★ v3：保护真实标识符
                if (w.Length < 4) return w;                 // 短词证据不足，不动
                return w.Replace('0', 'O').Replace('1', 'l').Replace('5', 'S').Replace('8', 'B');
            });

        // 汉字与字母数字粘连处插空格
        s = Regex.Replace(s, @"([一-鿿])([A-Za-z0-9])", "$1 $2");
        s = Regex.Replace(s, @"([A-Za-z0-9])([一-鿿])", "$1 $2");
        return s;
    }

    // ── 2. 页眉页脚（跨页重复行）────────────────────────
    private static readonly string[] PageSeparators = { "\f", "\u000c" };

    /// <summary>
    /// 去页眉页脚：把文本按分页符切成页，统计每行出现页数占比，
    /// 超过 threshold（默认 30%）的短行判为页眉/页脚并删除。
    /// 页数 &lt; 3 时不做（样本不足）。
    /// </summary>
    public static string RemoveRepeatedBoilerplate(string text, double threshold = 0.3, int maxLineLen = 40)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        var pages = text.Split(PageSeparators, StringSplitOptions.None).ToList();
        if (pages.Count < 3)
        {
            // 没有分页符时，退而求其次：按固定行数（40 行/页）估算
            var lines0 = text.Replace("\r", "").Split('\n');
            if (lines0.Length < 120) return text;
            pages = Chunk(lines0, 40).Select(c => string.Join("\n", c)).ToList();
        }

        var pageOf = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var i = 0; i < pages.Count; i++)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in pages[i].Replace("\r", "").Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.Length > maxLineLen) continue;
                if (Regex.IsMatch(l, @"^\d{1,4}$")) continue; // 纯页码：单页出现很正常
                if (!seen.Add(l)) continue;
                if (!pageOf.TryGetValue(l, out var set)) pageOf[l] = set = new HashSet<int>();
                set.Add(i);
            }
        }

        var boilerplate = new HashSet<string>(StringComparer.Ordinal);
        var need = Math.Ceiling(pages.Count * threshold);
        foreach (var kv in pageOf)
            if (kv.Value.Count >= need && kv.Value.Count >= 3) boilerplate.Add(kv.Key);

        if (boilerplate.Count == 0) return text;

        // 逐页过滤后重新拼接：分页符不是换行符，
        // 若直接把整篇按 '\n' 切开过滤，跨页粘连的行会漏掉页眉（v3 修掉这个坑）
        var sep = text.Contains('\f') ? "\f" : null;
        var keptPages = pages.Select(p =>
            string.Join("\n", p.Replace("\r", "").Split('\n')
                               .Where(l => !boilerplate.Contains(l.Trim()))));
        return string.Join(sep ?? "\n", keptPages);
    }

    private static IEnumerable<string[]> Chunk(string[] src, int size)
    {
        for (var i = 0; i < src.Length; i += size)
            yield return src.Skip(i).Take(size).ToArray();
    }

    // ── 3. 英文断字还原 ─────────────────────────────────
    /// <summary>行末连字符 + 下一行小写开头 → 合并并去掉连字符（inter-/national）。</summary>
    public static string Dehyphenate(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r", "").Split('\n').ToList();
        var outLines = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var cur = lines[i].TrimEnd();
            if (i + 1 < lines.Count
                && (cur.EndsWith('-') || cur.EndsWith('‐') || cur.EndsWith('‑'))
                && cur.Length > 1)
            {
                var next = lines[i + 1].TrimStart();
                if (next.Length > 0 && char.IsLower(next[0]))
                {
                    outLines.Add(cur[..^1] + next);
                    i++;
                    continue;
                }
            }
            outLines.Add(cur);
        }
        return string.Join("\n", outLines);
    }

    // ── 4. 中文软断行合并（带保护）──────────────────────
    private static bool IsMostlyCjk(string s)
    {
        if (s.Length == 0) return false;
        var cjk = s.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        return cjk * 2 >= s.Length;
    }

    private static readonly Regex ListStartRe = new(
        @"^\s*(?:[•·●○▪]|[（(]?\d{1,2}[)）.、]|第[一二三四五六七八九十百\d]+[章节条]|[一二三四五六七八九十]+[、．.]|[a-zA-Z][)）.、])",
        RegexOptions.Compiled);

    private static bool EndsSentence(string s)
        => s.Length > 0 && "。！？.!?:：;；".Contains(s[^1]);

    /// <summary>
    /// 中文段落软断行合并。v3 增加三道保护：
    ///   · 下行是列表项/编号/章节开头 → 不合并
    ///   · 上段很短（≤15 字）且下行明显更长 → 判为标题，不合并
    ///   · 合并后长度上限 120 字（v2 是 80 且判断用的是合并前长度）
    /// </summary>
    public static string ReconstructCjkLines(string text, int maxLine = 120)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r", "").Split('\n')
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

            // 保护 1：列表项/编号不并入上段
            if (ListStartRe.IsMatch(line)) { outLines.Add(buf); buf = line; continue; }

            // 保护 2：疑似标题（短行 + 无句末标点 + 下行明显更长）
            if (buf.Length <= 15 && !EndsSentence(buf) && line.Length > buf.Length * 2)
            { outLines.Add(buf); buf = line; continue; }

            // 合并 1：中文段落软断
            if (bufIsCjk && lineIsCjk && !EndsSentence(buf) && buf.Length < maxLine)
            {
                buf += line.TrimStart();
                continue;
            }
            // 合并 2：短的英文小尾巴（公式/题号）并入
            if (bufIsCjk && !lineIsCjk && !EndsSentence(buf)
                && !(line.Length > 0 && char.IsLower(line[0])) && line.Length < 24)
            {
                buf += " " + line;
                continue;
            }
            outLines.Add(buf);
            buf = line;
        }
        if (buf.Length > 0) outLines.Add(buf);
        return string.Join("\n", outLines);
    }

    // ── 5. 噪声过滤 ─────────────────────────────────────
    private static readonly HashSet<string> NoiseExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "ocr", "tesseract", "scanned by", "copyrighted material",
        "更多书籍请关注", "仅供个人使用", "—", "…", "@", "#", "$"
    };

    /// <summary>去页眉页脚式噪声行；保留数字行（页码/公式）。</summary>
    public static string FilterNoise(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var kept = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;
            if (s.Length == 1 && !char.IsDigit(s[0])) continue;
            if (s.Length <= 8 && Regex.IsMatch(s, @"^[\W_]+$")) continue;
            if (NoiseExact.Contains(s)) continue;
            if (Regex.IsMatch(s, @"^scanned by .{0,24}$", RegexOptions.IgnoreCase)) continue;
            // v3：装饰性页码 "- 37 -" / "·37·" / "第 37 页"
            // 注意：纯数字行（"37"）必须保留——它可能是公式或题号（金样 OC03）
            if (Regex.IsMatch(s, @"^[-–—~·*|]{1,3}\s*\d{1,4}\s*[-–—~·*|]{1,3}$")) continue;
            if (Regex.IsMatch(s, @"^第\s*\d{1,4}\s*页$")) continue;
            // 全大写长页眉
            if (s.Length > 20 && s.All(c => !char.IsLetter(c) || char.IsUpper(c)) && s.Count(char.IsLetter) > 30)
                continue;
            kept.Add(s);
        }
        return string.Join("\n", kept);
    }

    // ── 6. 置信度加权重建 ───────────────────────────────
    public sealed record WordTok(string Text, double Confidence, double X, double Y);

    /// <summary>
    /// 按 TSV 词框重建文本：conf&lt;minConf 丢弃；行内按 X 排序，行按 Y 聚类。
    /// v3：聚类改为「按 Y 排序后的间隙聚类」，不再拿行内第一个词的 Y 当基准
    /// （v2 的做法在扫描歪斜 / 基线漂移时会把整页串成一行）。
    /// </summary>
    public static string ReconstructFromTsvWords(IReadOnlyList<WordTok> words, double minConf = 40, double rowTolerance = 8)
    {
        var kept = words
            .Where(w => w.Text.Length > 0 && w.Confidence >= minConf)
            .OrderBy(w => w.Y)
            .ThenBy(w => w.X)
            .ToList();
        if (kept.Count == 0) return "";

        var tol = Math.Clamp(rowTolerance, 1, 40);
        var rows = new List<List<WordTok>>();
        var cur = new List<WordTok> { kept[0] };
        var prevY = kept[0].Y;
        for (var i = 1; i < kept.Count; i++)
        {
            if (kept[i].Y - prevY > tol)
            {
                rows.Add(cur);
                cur = new List<WordTok>();
            }
            cur.Add(kept[i]);
            prevY = kept[i].Y;
        }
        rows.Add(cur);

        var lines = rows.Select(r => string.Join(" ", r.OrderBy(w => w.X).Select(w => w.Text)));
        return string.Join("\n", lines);
    }

    // ── 7. 多配置投票（模糊）────────────────────────────
    /// <summary>投票用的归一化键：去空白、全角转半角、小写化。</summary>
    internal static string VoteKey(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch)) continue;
            var c = ch;
            if (c >= 0xFF01 && c <= 0xFF5E) c = (char)(c - 0xFEE0);
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 多配置投票：同一行（归一化后）在 ≥⌈n×keepRatio⌉ 路中出现才保留。
    /// v3 用归一化键分组，容忍三路 PSM 的细微差异；保留首次出现顺序。
    /// </summary>
    public static string VotePasses(IReadOnlyList<string> passes, double keepRatio = 0.5)
    {
        if (passes.Count == 0) return "";
        var lists = passes.Select(p => (p ?? "").Replace("\r", "").Split('\n')
                                        .Select(l => l.Trim())
                                        .Where(l => l.Length > 1)
                                        .ToList())
                           .ToList();
        if (lists.Count == 1) return string.Join("\n", lists[0]);

        // 组 → 支持它的「路数」
        var support = new Dictionary<string, int>(StringComparer.Ordinal);
        var variantCount = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var lines in lists)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in lines)
            {
                var k = VoteKey(l);
                if (k.Length == 0 || !seen.Add(k)) continue;
                support[k] = support.GetValueOrDefault(k) + 1;
                if (!variantCount.TryGetValue(k, out var vc)) variantCount[k] = vc = new Dictionary<string, int>(StringComparer.Ordinal);
                vc[l] = vc.GetValueOrDefault(l) + 1;
            }
        }

        var need = Math.Ceiling(lists.Count * keepRatio);
        bool Keep(string line)
        {
            var k = VoteKey(line);
            return k.Length > 0 && support.GetValueOrDefault(k) >= need;
        }
        // 组内选出现最多的原文形态
        string Best(string line)
        {
            var k = VoteKey(line);
            if (!variantCount.TryGetValue(k, out var vc)) return line;
            return vc.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
        }

        var order = new List<string>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lists[0])
        {
            if (!Keep(line)) continue;
            var k = VoteKey(line);
            if (!emitted.Add(k)) continue;
            order.Add(Best(line));
        }
        // 补上其它路中达标但首路缺失的行
        foreach (var lines in lists.Skip(1))
        foreach (var line in lines)
        {
            if (!Keep(line)) continue;
            var k = VoteKey(line);
            if (!emitted.Add(k)) continue;
            order.Add(Best(line));
        }
        return string.Join("\n", order);
    }

    // ── 8. 质量评分 ─────────────────────────────────────
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
        var noise = lines.Count(l => l.Length <= 8 && Regex.IsMatch(l, @"^[\W_]+$"));
        var noiseRatio = lines.Length == 0 ? 0 : (double)noise / lines.Length;
        var score = 0.5 * meanConf + 0.3 * Math.Min(cjkRatio / 0.6, 1.0) + 0.2 * (1 - noiseRatio);
        var grade = score >= 0.845 ? "A" : score >= 0.7 ? "B" : score >= 0.5 ? "C" : "D";
        return new QualityReport(Math.Round(meanConf, 4), Math.Round(cjkRatio, 4),
            Math.Round(noiseRatio, 4), lines.Length, grade);
    }

    // ── 9. 一站式 ───────────────────────────────────────
    /// <summary>
    /// 一站式清洗：混淆修复 → 噪声过滤 → 页眉页脚去除 → 断字还原 → 中文断行合并。
    /// </summary>
    public static string Refine(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = FixConfusions(raw);
        s = FilterNoise(s);
        s = RemoveRepeatedBoilerplate(s);
        s = Dehyphenate(s);
        s = ReconstructCjkLines(s);
        return s;
    }
}
