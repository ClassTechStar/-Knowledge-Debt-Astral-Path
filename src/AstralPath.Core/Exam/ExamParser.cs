using System.Text.RegularExpressions;

namespace AstralPath.Core.Exam;

/// <summary>试卷题目（结构化产物，与源 items 对象同构）。</summary>
public sealed record ExamItem(
    int No,
    int? RawNo,
    string? Section,
    string Stem,
    IReadOnlyList<string> Options,
    string Answer,
    string QType,
    string Raw,
    IReadOnlyList<string> Kps);

/// <summary>板块统计。</summary>
public sealed record ExamSection(string Title, int Items);

/// <summary>结构化结果。</summary>
public sealed record ExamStructure(IReadOnlyList<ExamItem> Items, IReadOnlyList<ExamSection> Sections);

/// <summary>
/// 试卷 / 作业 / 错题本地结构化（P2 迁移，源：Astralpath structureExamText + 辅助函数，L3501-3634）。
/// 确定性规则、不依赖模型：板块识别 → 题头切分 → 选项切分 → 答案批量分发 → 题型推断。
/// 单卷题目上限 300。
/// </summary>
public static class ExamParser
{
    public const int MaxItems = 300;

    public static string PaperTypeLabel(string? t)
        => t == "hw" ? "作业" : (t == "wrong" ? "错题" : "试卷");

    public static string QTypeLabel(string? t)
        => t == "choice" ? "选择题" : t == "blank" ? "填空题" : t == "judge" ? "判断题" : t == "solve" ? "解答题" : "题目";

    /// <summary>行预处理：统一换行、剥离 [page N]/[slide N]/[image N]/[media N]/[备注 N] 标记（源 t10ExamLines，L3509-3513）。</summary>
    public static IReadOnlyList<string> ExamLines(string? text)
    {
        return (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n')
            .Select(l => Regex.Replace(l, "^\\s*\\[(?:page|slide|image|media|备注)\\s*\\d+\\]\\s*", "").TrimEnd())
            .Where(l => l.Trim().Length > 0)
            .ToList();
    }

    /// <summary>板块标题识别（源 t10SectionOf，L3515-3532）。</summary>
    public static string? SectionOf(string? line)
    {
        var s = (line ?? "").Trim();
        if (s.Length == 0 || s.Length > 26) return null;
        if (Regex.IsMatch(s, "[?？。；，,]$")) return null;
        if (Regex.IsMatch(s, "^第\\s*[一二三四五六七八九十]+\\s*[大部分大题篇]")) return s;
        if (Regex.IsMatch(s, "^[（(][一二三四五六七八九十]+[）)]\\s*\\S")) return s;
        var m = Regex.Match(s, "^[一二三四五六七八九十]+、\\s*(.{1,22})$");
        if (m.Success)
        {
            var body = m.Groups[1].Value;
            if (body.Length <= 10 && !Regex.IsMatch(body, "[?？]")) return s;
            if (Regex.IsMatch(body, "(选择|填空|判断|解答|计算|应用|证明|简答|论述|综述|分析|改错|写作|翻译|名词解释|案例|听力|阅读|综合|基础|拓展|题目|试题|部分)")) return s;
            return null;
        }
        if (Regex.IsMatch(s, "^(选择|填空|判断|解答|计算|应用|证明|简答|论述|综述|分析|改错|写作|翻译|名词解释|案例)题?$")) return s;
        return null;
    }

    /// <summary>题头识别（源 t10QuestionHead，L3534-3543）：1. / 1、/ 1）/ (1) / 第1题。
    /// 注：元素名用 Body 而非 Rest——Rest 是 ValueTuple 第 8 元素的保留字段名，命名会触发 CS8126。</summary>
    public static (int No, string Body)? QuestionHead(string? line)
    {
        var s = (line ?? "").Trim();
        var m = Regex.Match(s, "^(\\d{1,3})\\s*[.、．)]\\s*(\\S.*)$");
        if (m.Success) return (int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim());
        m = Regex.Match(s, "^[（(](\\d{1,3})[）)]\\s*(\\S.*)$");
        if (m.Success) return (int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim());
        m = Regex.Match(s, "^第\\s*(\\d{1,3})\\s*题\\s*[:：]?\\s*(.*)$");
        if (m.Success) return (int.Parse(m.Groups[1].Value), (m.Groups[2].Value ?? "").Trim());
        return null;
    }

    /// <summary>答案行解析（源 t10AnswerOf，L3545-3559）：支持 "1.A 2.C" 批量分发。</summary>
    public static IReadOnlyList<(int? No, string Text)> AnswerOf(string? line)
    {
        var s = (line ?? "").Trim();
        var m = Regex.Match(s, "^(?:【\\s*答案\\s*】|参考答案|答案|答)\\s*[:：]?\\s*(\\S.*)$");
        if (!m.Success) return Array.Empty<(int?, string)>();
        var body = m.Groups[1].Value.Trim();
        var multi = Regex.Matches(body, "\\d{1,3}\\s*[.、．)]\\s*\\S{1,24}").Select(x => x.Value).ToList();
        if (multi.Count >= 2)
        {
            var list = new List<(int?, string)>();
            foreach (var item in multi)
            {
                var mm = Regex.Match(item.Trim(), "^(\\d{1,3})\\s*[.、．)]\\s*(.+)$");
                if (mm.Success) list.Add((int.Parse(mm.Groups[1].Value), mm.Groups[2].Value.Trim()));
            }
            return list;
        }
        var one = Regex.Match(body, "^(\\d{1,3})\\s*[.、．)]\\s*(.+)$");
        if (one.Success) return new[] { ((int?)int.Parse(one.Groups[1].Value), one.Groups[2].Value.Trim()) };
        return new[] { ((int?)null, body) };
    }

    /// <summary>同行内联选项切分（源 t10InlineOptions，L3561-3568）。</summary>
    public static (string Head, IReadOnlyList<string> Options)? InlineOptions(string? stem)
    {
        var s = stem ?? "";
        var marks = Regex.Matches(s, "(?:^|\\s)[A-Da-d]\\s*[.、．)]");
        if (marks.Count < 2) return null;
        var parts = Regex.Split(s, "(?:^|\\s)[A-Da-d]\\s*[.、．)]").Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (parts.Count < 3) return null;
        var options = new List<string>();
        for (var i = 1; i < parts.Count; i++) options.Add((char)('A' + (i - 1)) + "." + parts[i]);
        return (parts[0], options);
    }

    /// <summary>题型推断（源 t10QType，L3569-3576）。</summary>
    public static string QType(ExamItem item, string? section)
    {
        var s = (section ?? "") + " " + (item.Stem ?? "").Substring(0, Math.Min(40, (item.Stem ?? "").Length));
        if (item.Options.Count >= 2) return "choice";
        if (Regex.IsMatch(s, "填空|填写")) return "blank";
        if (Regex.IsMatch(s, "判断")) return "judge";
        if (Regex.IsMatch(s, "解答|计算|应用|证明|简答|论述|综述|分析|改错|案例|翻译|写作")) return "solve";
        return "unknown";
    }

    /// <summary>
    /// 结构化主流程（源 structureExamText，L3577-3634）。
    /// 逐行扫描：板块 → 题头（flush 当前题）→ 选项 → 答案（单题归属或入池）→ 正文续行；
    /// 收尾：无选项题目做内联选项补切 → 题型推断 → 答案池按编号匹配 → 顺序兜底分发。
    /// </summary>
    public static ExamStructure Structure(string? text)
    {
        var lines = ExamLines(text);
        var items = new List<ExamItem>();
        string? section = null;
        var secOrder = new List<string>();
        var ansPool = new List<(int? No, string Text)>();

        int? curNo = null, curRawNo = null;
        string? curSection = null, curStem = null, curAnswer = "";
        var curOptions = new List<string>();
        var curRaw = new List<string>();

        void Flush()
        {
            if (curStem != null || curRaw.Count > 0)
            {
                items.Add(new ExamItem(items.Count + 1, curRawNo, curSection,
                    curStem ?? "", curOptions.ToList(), curAnswer ?? "", "",
                    string.Join("\n", curRaw), new List<string>()));
            }
            curNo = null; curRawNo = null; curSection = null; curStem = null; curAnswer = "";
            curOptions = new List<string>(); curRaw = new List<string>();
        }

        for (var i = 0; i < lines.Count && items.Count < MaxItems; i++)
        {
            var ln = lines[i];
            var sec = SectionOf(ln);
            if (sec != null)
            {
                section = sec;
                if (!secOrder.Contains(sec)) secOrder.Add(sec);
                continue;
            }
            var qh = QuestionHead(ln);
            if (qh != null)
            {
                Flush();
                curNo = items.Count + 1;
                curRawNo = qh.Value.No;
                curSection = section;
                curStem = qh.Value.Body;
                curRaw.Add(ln);
                continue;
            }
            var om = Regex.Match(ln, "^\\s*([A-Da-d])\\s*[.、．)]\\s*(.+)$");
            if (om.Success && curStem != null)
            {
                curOptions.Add(char.ToUpperInvariant(om.Groups[1].Value[0]) + "." + om.Groups[2].Value.Trim());
                curRaw.Add(ln);
                continue;
            }
            var an = AnswerOf(ln);
            if (an.Count > 0)
            {
                if (curStem != null && an.Count == 1 && an[0].No == null)
                {
                    curAnswer = an[0].Text;
                    curRaw.Add(ln);
                }
                else
                {
                    ansPool.AddRange(an);
                }
                continue;
            }
            if (curStem != null)
            {
                curStem += (curStem.Length > 0 ? "\n" : "") + ln;
                curRaw.Add(ln);
            }
        }
        Flush();

        // 硬上限：循环条件在 Flush 前判定，末题可能越界一条（源 JS 同为 301 行为）；
        // 此处按业务约束（附录 A.3「单卷题目上限三百」）最终截断，保证真实 ≤ MaxItems
        if (items.Count > MaxItems) items = items.Take(MaxItems).ToList();

        // 内联选项补切（无独立选项行的题目）
        var result = new List<ExamItem>();
        foreach (var it in items)
        {
            var fixedItem = it;
            if (fixedItem.Options.Count == 0)
            {
                var io = InlineOptions(fixedItem.Stem);
                if (io != null) fixedItem = fixedItem with { Stem = io.Value.Head, Options = io.Value.Options };
            }
            result.Add(fixedItem);
        }
        items = result;

        // 题型推断 + 文本规整
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var stem = Regex.Replace(it.Stem ?? "", "\n{2,}", "\n").Trim();
            if (stem.Length > 600) stem = stem.Substring(0, 600);
            var raw = it.Raw ?? "";
            if (raw.Length > 900) raw = raw.Substring(0, 900);
            var typed = it with { QType = QType(it, it.Section), Stem = stem, Raw = raw };
            items[i] = typed;
        }

        // 答案池：按编号匹配
        foreach (var a in ansPool)
        {
            if (a.No == null) continue;
            var idx = items.FindIndex(x => x.RawNo == a.No && x.Answer.Length == 0);
            if (idx >= 0) items[idx] = items[idx] with { Answer = a.Text };
        }

        // 顺序兜底：全部无编号答案且数量等于未答题目数
        var unanswered = items.Where(it => it.Answer.Length == 0).ToList();
        if (unanswered.Count > 0 && ansPool.Count > 0 && ansPool.All(a => a.No == null) && ansPool.Count == unanswered.Count)
        {
            for (var i = 0; i < unanswered.Count; i++)
            {
                var idx = items.IndexOf(unanswered[i]);
                items[idx] = items[idx] with { Answer = ansPool[i].Text };
            }
        }

        var sections = secOrder.Select(t => new ExamSection(t, items.Count(it => it.Section == t))).ToList();
        if (sections.Count == 0 && items.Count > 0) sections.Add(new ExamSection("全部题目", items.Count));
        return new ExamStructure(items, sections);
    }

    /// <summary>
    /// 知识点匹配（源 matchExamKps，L3661-3687）：长词优先 + 位置不重叠贪心，单题上限默认 4（T10.1 同题不重复命中）。
    /// </summary>
    public static IReadOnlyList<ExamKpEntry> MatchKps(string? text, IReadOnlyList<ExamKpEntry> lex, int limit = 4)
    {
        var s = text ?? "";
        if (s.Trim().Length == 0 || lex.Count == 0) return Array.Empty<ExamKpEntry>();
        var hits = new List<(ExamKpEntry T, int Pos, int End)>();
        for (var i = 0; i < lex.Count && hits.Count < 600; i++)
        {
            var t = lex[i];
            var idx = s.IndexOf(t.Title, StringComparison.Ordinal);
            while (idx >= 0)
            {
                hits.Add((t, idx, idx + t.Title.Length));
                idx = s.IndexOf(t.Title, idx + 1, StringComparison.Ordinal);
            }
        }
        hits = hits.OrderByDescending(h => h.T.Title.Length).ThenByDescending(h => h.T.W).ToList();
        var taken = new List<(int Pos, int End)>();
        var outList = new List<ExamKpEntry>();
        var seenT = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in hits)
        {
            if (outList.Count >= limit) break;
            if (taken.Any(tk => h.Pos < tk.End && h.End > tk.Pos)) continue;
            if (!seenT.Add(h.T.Title)) continue;
            taken.Add((h.Pos, h.End));
            outList.Add(h.T);
        }
        return outList;
    }
}

/// <summary>知识点词库条目（源 examKpLexicon 条目结构）。</summary>
public sealed record ExamKpEntry(
    string Title,
    double W,
    string? ChTitle = null,
    string? ChId = null,
    string? NodeId = null);
