using System.Text.RegularExpressions;
using AstralPath.Core.Graph;

namespace AstralPath.Core.Exam;

/// <summary>试卷图谱节点（三层：主题 / 知识点 / 题目）。</summary>
public sealed record ExamNode(
    string Id,
    string Title,
    string Kind,   // topic | kp | item
    int Diff,
    string Course,
    string Body,
    string? Source = null,
    string? QType = null,
    int? No = null);

/// <summary>试卷图谱边（四类：attach 归属 / tested 考查 / related 同题共现 / prerequisite 先修）。</summary>
public sealed record ExamEdge(string From, string To, double W, string EType, bool Tg, double Conf);

/// <summary>三元组（源 t10Triples）。</summary>
public sealed record ExamTriple(string Subject, string Relation, string Object, double Weight, double? Confidence);

/// <summary>图谱统计。</summary>
public sealed record ExamKgStats(int Items, int Kps, int Topics, int Edges, int ShownItems, int Cut);

/// <summary>试卷图谱产物。</summary>
public sealed record ExamKg(
    IReadOnlyList<ExamNode> Nodes,
    IReadOnlyList<ExamEdge> Edges,
    IReadOnlyList<ExamTriple> Triples,
    ExamKgStats Stats);

/// <summary>教材先修边输入（用于试卷图谱继承）。</summary>
public sealed record BookEdge(string From, string To, bool Tg);

/// <summary>
/// 试卷知识点层级图谱（P2 迁移，源：Astralpath buildExamKG + t10Triples，L3688-3799）。
/// 三层节点：主题（教材章节归属优先，否则试卷板块）→ 知识点（词库匹配）→ 题目（上限 120）；
/// 四类边：attach 归属 / tested 考查 / related 同题共现 / prerequisite 继承教材先修；
/// 构图后按拓扑序清环。全部为确定性规则，不依赖模型。
/// </summary>
public static class ExamKgBuilder
{
    /// <summary>题目节点上限（源 L3752：shown = min(items, 120)）。</summary>
    public const int MaxItemNodes = 120;

    /// <summary>单题知识点上限（源 L3710：matchExamKps limit=4）。</summary>
    public const int MaxKpsPerItem = 4;

    public static ExamKg Build(
        string paperName,
        IReadOnlyList<ExamItem> items,
        string? preview,
        IReadOnlyList<ExamKpEntry> lex,
        IReadOnlyList<BookEdge>? bookEdges = null,
        IReadOnlyList<string>? fallbackKps = null)
    {
        var nodes = new List<ExamNode>();
        var edges = new List<ExamEdge>();
        var seenEk = new HashSet<string>(StringComparer.Ordinal);
        var topics = new List<(string Id, string Title, string Source)>();
        var kpMap = new Dictionary<string, (string Id, ExamKpEntry? Lex)>(StringComparer.Ordinal);

        string Ek(string a, string b)
        {
            var arr = new[] { a, b };
            Array.Sort(arr, StringComparer.Ordinal);
            return arr[0] + ">" + arr[1];
        }

        string TopicNode(string title, string source)
        {
            var id = "TP" + (topics.Count + 1).ToString("D2");
            topics.Add((id, title, source));
            nodes.Add(new ExamNode(id, title, "topic", 2, paperName, source, Source: source));
            return id;
        }

        // ① 题目 → 知识点（词库匹配，源 L3709-3712）
        var itemKps = new List<List<string>>();
        foreach (var it in items)
        {
            var text = (it.Stem ?? "") + "\n" + string.Join("\n", it.Options) + "\n" + (it.Answer ?? "");
            itemKps.Add(ExamParser.MatchKps(text, lex, MaxKpsPerItem).Select(t => t.Title).ToList());
        }

        var kpTitles = new List<string>();
        var kpSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kps in itemKps)
            foreach (var k in kps)
                if (kpSeen.Add(k)) kpTitles.Add(k);

        // ② 兜底：词库未命中时用试卷自身抽取的知识点（源 L3718-3725）
        if (kpTitles.Count == 0 && fallbackKps != null)
        {
            foreach (var t in fallbackKps)
                if (!string.IsNullOrWhiteSpace(t) && kpSeen.Add(t)) kpTitles.Add(t);
        }

        // ③ 知识点节点（源 L3727-3733）
        for (var i = 0; i < kpTitles.Count; i++)
        {
            var k = kpTitles[i];
            var id = "KP" + (i + 1).ToString("D2");
            var lexEntry = lex.FirstOrDefault(l => l.Title == k);
            kpMap[k] = (id, lexEntry);
            nodes.Add(new ExamNode(id, k, "kp", 2, paperName, ""));
        }

        // ④ 主题节点：优先教材章节归属，否则试卷板块（源 L3735-3750）
        var topicByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        string TopicFor(string k, ExamKpEntry? lexEntry)
        {
            string? key = null, src = null, title = null;
            if (lexEntry?.ChTitle != null) { key = "B:" + lexEntry.ChTitle; src = "教材章节"; title = lexEntry.ChTitle; }
            if (key == null)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    if (itemKps[i].Contains(k) && !string.IsNullOrEmpty(items[i].Section))
                    {
                        key = "S:" + items[i].Section;
                        src = "试卷板块";
                        title = items[i].Section;
                        break;
                    }
                }
            }
            if (key == null) { key = "S:全部题目"; src = "试卷板块"; title = "全部题目"; }
            if (!topicByKey.TryGetValue(key, out var tid)) topicByKey[key] = tid = TopicNode(title!, src!);
            return tid;
        }

        foreach (var k in kpTitles)
        {
            var tid = TopicFor(k, kpMap[k].Lex);
            edges.Add(new ExamEdge(tid, kpMap[k].Id, 0.6, "attach", false, 0.8));
        }

        // ⑤ 题目节点（上限 120，源 L3752-3761）
        var shown = Math.Min(items.Count, MaxItemNodes);
        for (var i = 0; i < shown; i++)
        {
            var it = items[i];
            var id = "Q" + (i + 1).ToString("D3");
            var stemFlat = Regex.Replace(it.Stem ?? "", "\\s+", " ");
            var title = "题" + (i + 1) + " " + (stemFlat.Length <= 14 ? stemFlat : stemFlat.Substring(0, 14));
            var body = (it.Stem ?? "").Length <= 120 ? it.Stem ?? "" : (it.Stem ?? "").Substring(0, 120);
            nodes.Add(new ExamNode(id, title, "item", 2, paperName, body, QType: it.QType, No: i + 1));
            foreach (var k in itemKps[i])
                if (kpMap.ContainsKey(k))
                    edges.Add(new ExamEdge(kpMap[k].Id, id, 0.5, "tested", false, 0.7));
        }

        // ⑥ 知识点间关联：同题共现（源 L3763-3777）
        var co = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kps in itemKps)
        {
            for (var a = 0; a < kps.Count; a++)
                for (var b = a + 1; b < kps.Count; b++)
                {
                    var arr = new[] { kps[a], kps[b] };
                    Array.Sort(arr, StringComparer.Ordinal);
                    var key = arr[0] + "\t" + arr[1];
                    co[key] = co.GetValueOrDefault(key) + 1;
                }
        }
        foreach (var kv in co)
        {
            var p = kv.Key.Split('\t');
            if (!kpMap.ContainsKey(p[0]) || !kpMap.ContainsKey(p[1])) continue;
            var kk = Ek(kpMap[p[0]].Id, kpMap[p[1]].Id);
            if (!seenEk.Add(kk)) continue;
            edges.Add(new ExamEdge(kpMap[p[0]].Id, kpMap[p[1]].Id, Math.Min(0.4 + kv.Value * 0.2, 1.2), "related", false, 0.6));
        }

        // 继承教材先修边（源 L3778-3791）
        if (bookEdges != null)
        {
            foreach (var e in bookEdges)
            {
                string? ft = null, tt = null;
                foreach (var l in lex)
                {
                    if (l.NodeId == e.From && ft == null) ft = l.Title;
                    if (l.NodeId == e.To && tt == null) tt = l.Title;
                }
                if (ft == null || tt == null || !kpMap.ContainsKey(ft) || !kpMap.ContainsKey(tt)) continue;
                var kk = Ek(kpMap[ft].Id, kpMap[tt].Id);
                if (!seenEk.Add(kk)) continue;
                edges.Add(new ExamEdge(kpMap[ft].Id, kpMap[tt].Id, 1.1, "prerequisite", e.Tg, 0.85));
            }
        }

        // 清环（源 L3792，泛型复用 P1 清环语义）
        var cleaned = AiGraphMerger.RemoveCyclesTopo(nodes, edges, n => n.Id, e => e.From, e => e.To);
        var finalEdges = cleaned.Edges;
        var triples = ToTriples(nodes, finalEdges);
        var stats = new ExamKgStats(items.Count, kpTitles.Count, topics.Count, finalEdges.Count, shown, cleaned.Cut);
        return new ExamKg(nodes, finalEdges, triples, stats);
    }

    /// <summary>三元组派生（源 t10Triples，L3688-3696）：关系标签 attach→归属 / tested→考查 / related→同题共现 / prerequisite→先修于。</summary>
    public static IReadOnlyList<ExamTriple> ToTriples(IReadOnlyList<ExamNode> nodes, IReadOnlyList<ExamEdge> edges)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in nodes) titles[n.Id] = n.Title;
        var label = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prerequisite"] = "先修于", ["related"] = "同题共现", ["attach"] = "归属", ["tested"] = "考查"
        };
        return edges.Select(e => new ExamTriple(
            titles.GetValueOrDefault(e.From, e.From),
            label.GetValueOrDefault(e.EType, string.IsNullOrEmpty(e.EType) ? "关联" : e.EType),
            titles.GetValueOrDefault(e.To, e.To),
            Math.Round(e.W, 3),
            Math.Round(e.Conf, 3))).ToList();
    }
}
