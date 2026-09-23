namespace AstralPath.Core.Graph;

/// <summary>
/// 从文本构图：共现 PMI 边 + 依赖句式边 + TextRank 重要性。
/// 替代「章节串链 + 词频挂靠」的 v1 启发式。
/// </summary>
public static class GraphInference
{
    public sealed record Concept(string Id, string Title, string Kind);

    /// <summary>TextRank：共现图为边，迭代节点重要性。</summary>
    public static IReadOnlyDictionary<string, double> TextRank(
        IReadOnlyList<string> tokenList,
        int window = 5,
        int iterations = 30,
        double damping = 0.85)
    {
        var tokens = tokenList.ToArray();
        var vocab = tokens.Distinct(StringComparer.Ordinal).ToList();
        if (vocab.Count == 0) return new Dictionary<string, double>();
        var idx = vocab.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i, StringComparer.Ordinal);
        var w = new double[vocab.Count, vocab.Count];
        for (var i = 0; i < tokens.Length; i++)
        {
            for (var j = i + 1; j < Math.Min(tokens.Length, i + window); j++)
            {
                var a = idx[tokens[i]];
                var b = idx[tokens[j]];
                w[a, b] += 1.0 / (j - i); // 近距权重更高
                w[b, a] = w[a, b];
            }
        }
        var rank = new double[vocab.Count];
        Array.Fill(rank, 1.0 / vocab.Count);
        for (var it = 0; it < iterations; it++)
        {
            var next = new double[vocab.Count];
            for (var i = 0; i < vocab.Count; i++)
            {
                var s = 0.0;
                for (var j = 0; j < vocab.Count; j++)
                {
                    if (w[j, i] <= 0) continue;
                    var row = 0.0;
                    for (var k = 0; k < vocab.Count; k++) row += w[j, k];
                    if (row > 0) s += (w[j, i] / row) * rank[j];
                }
                next[i] = (1 - damping) / vocab.Count + damping * s;
            }
            rank = next;
        }
        return vocab.Select((t, i) => (t, r: rank[i])).ToDictionary(x => x.t, x => x.r, StringComparer.Ordinal);
    }

    /// <summary>PMI 边权：共现远超独立期望时才连边。</summary>
    public static double Pmi(int co, int a, int b, int total)
    {
        if (co <= 0 || a <= 0 || b <= 0 || total <= 0) return 0;
        var pmi = Math.Log2((co * (double)total) / ((double)a * b));
        return Math.Max(0, pmi); // 只留正关联
    }

    /// <summary>
    /// 依赖句式抽取边：A 是 B 的先修 / 基于 A 的 B / 学 A 之前必须…
    /// </summary>
    public static IEnumerable<(string From, string To)> DependencyPatterns(string text, IEnumerable<string> concepts)
    {
        var list = concepts.ToList();
        foreach (var c in list)
        {
            // 基于/先学 X ，再/然后 Y
            var rx = new System.Text.RegularExpressions.Regex(
                $@"(?:基于|先学|掌握|了解){{0,2}}\s*{System.Text.RegularExpressions.Regex.Escape(c)}\s*(?:后|之后|再|然后|才能|才能理解)\s*([一-鿿A-Za-z0-9_·、]{{2,12}})");
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(text))
            {
                var target = m.Groups[1].Value.Trim();
                var hit = list.FirstOrDefault(x => target.Contains(x, StringComparison.Ordinal));
                if (hit is not null && hit != c) yield return (c, hit);
            }
        }
    }

    /// <summary>
    /// 一键构图：章节作 backbone，概念按 TextRank 挑选，
    /// 边 = 共现 PMI（≥阈值）∪ 依赖句式，权重取二者较大，confidence=归一化 PMI。
    /// </summary>
    public static WeightedKg BuildFromText(
        string text,
        IReadOnlyList<Concept> chapters,
        int maxConcepts = 24,
        double pmiThreshold = 1.0)
    {
        var kg = new WeightedKg();
        foreach (var ch in chapters)
            kg.AddNode(ch.Id, ch.Title, ch.Kind);

        // 粗分词：连续中英文片段
        var tokens = System.Text.RegularExpressions.Regex
            .Matches(text, @"[一-鿿]{2,8}|[A-Za-z][A-Za-z0-9_\.]{1,20}")
            .Select(m => m.Value)
            .Where(t => t.Length >= 2)
            .ToArray();

        var ranks = TextRank(tokens);
        var top = ranks.OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .Where(t => chapters.All(c => c.Title.IndexOf(t, StringComparison.Ordinal) < 0))
            .Take(maxConcepts)
            .ToList();

        foreach (var (t, i) in top.Select((t, i) => (t, i)))
            kg.AddNode($"T{i + 1:00}", t, "term", ranks[t]);

        // 章节 backbone：按顺序弱先修
        for (var i = 0; i < chapters.Count - 1; i++)
            kg.AddEdge(chapters[i].Id, chapters[i + 1].Id, "prerequisite", 0.8, 0.7);

        // 共现 PMI 边（窗口共现计数）
        var co = new Dictionary<(string, string), int>();
        var cnt = top.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        var window = 8;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!cnt.ContainsKey(tokens[i])) continue;
            cnt[tokens[i]]++;
            for (var j = i + 1; j < Math.Min(tokens.Length, i + window); j++)
            {
                if (!cnt.ContainsKey(tokens[j])) continue;
                var key = Ord(tokens[i], tokens[j]);
                co[key] = co.GetValueOrDefault(key) + 1;
            }
        }
        var total = Math.Max(cnt.Values.Sum(), 1);
        foreach (var (key, c) in co)
        {
            var a = cnt[key.Item1];
            var b = cnt[key.Item2];
            var pmi = Pmi(c, a, b, total);
            if (pmi < pmiThreshold) continue;
            var idA = NodeIdOf(kg, key.Item1);
            var idB = NodeIdOf(kg, key.Item2);
            if (idA is null || idB is null) continue;
            kg.AddEdge(idA, idB, "related", Math.Min(pmi / 6.0, 2.0), Math.Min(pmi / 8.0, 1.0));
        }

        // 依赖句式 → prerequisite
        foreach (var (fromTitle, toTitle) in DependencyPatterns(text, top))
        {
            var idA = NodeIdOf(kg, fromTitle);
            var idB = NodeIdOf(kg, toTitle);
            if (idA is null || idB is null) continue;
            kg.AddEdge(idA, idB, "prerequisite", 1.3, 0.9);
        }

        // 每个概念挂到 TextRank 最高章节，模拟「归属」
        foreach (var n in kg.Nodes.Where(n => n.Kind == "term").ToList())
        {
            var ch = chapters.FirstOrDefault(c =>
                text.IndexOf(c.Title, StringComparison.Ordinal) >= 0);
            if (ch is not null) kg.AddEdge(ch.Id, n.Id, "prerequisite", 0.5, 0.5);
        }
        return kg;
    }

    private static (string, string) Ord(string a, string b)
        => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    private static string? NodeIdOf(WeightedKg kg, string title)
        => kg.Nodes.FirstOrDefault(n => n.Title == title)?.Id;
}
