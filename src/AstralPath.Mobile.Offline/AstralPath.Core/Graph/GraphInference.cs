using System.Text.RegularExpressions;

namespace AstralPath.Core.Graph;

/// <summary>
/// 从文本构图 v3：共现 NPMI（平滑+归一化）边 + 依赖句式边 + TextRank 重要性
/// + 就近章节锚定 + 子词去重 + DAG 收尾。
///
/// 相对 v2 的关键修复：
/// ① TextRank 由稠密 V×V 矩阵改为稀疏邻接（78 万字的书不再爆内存 / O(V²)）
/// ② 术语→章节锚定：v2 里所有术语都被挂到「同一个」章节（FirstOrDefault 恒定值），
///    现改为按「术语首次出现位置」落在其所属章节区间内
/// ③ 依赖句式正则：v2 每个概念现场 new Regex，现改为一次编译 + 静态复用，并扩充句式
/// ④ PMI 低频偏置：加最小词频门槛 + 加一平滑 + 归一化 NPMI（∈[-1,1]）
/// ⑤ 子词去重：避免「网络 / 神经网络」这类重复概念各建一个节点
/// ⑥ DAG 收尾：成环时按置信度丢弃最弱的一条边，保证拓扑排序不会失败
/// </summary>
public static class GraphInference
{
    public sealed record Concept(string Id, string Title, string Kind);

    // ── 分词 ────────────────────────────────────────────
    private static readonly Regex TokenRe = new(
        @"[一-鿿]{2,8}|[A-Za-z][A-Za-z0-9_\.]{1,20}",
        RegexOptions.Compiled);

    public static string[] Tokenize(string text)
        => TokenRe.Matches(text).Select(m => m.Value).Where(t => t.Length >= 2).ToArray();

    // ── 1. TextRank（稀疏邻接版）────────────────────────
    /// <summary>
    /// TextRank：共现图为边，迭代节点重要性。v3 改为稀疏存储 + 词表上限，
    /// 数学语义与 v2 一致（同样的 1/(j-i) 权重、阻尼、迭代次数），仅做归一化输出。
    /// </summary>
    public static IReadOnlyDictionary<string, double> TextRank(
        IReadOnlyList<string> tokenList,
        int window = 5,
        int iterations = 30,
        double damping = 0.85,
        int maxVocab = 4000)
    {
        if (tokenList.Count == 0) return new Dictionary<string, double>(StringComparer.Ordinal);

        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokenList)
            freq[t] = freq.GetValueOrDefault(t) + 1;

        var vocab = freq.Count <= maxVocab
            ? freq.Keys.ToArray()
            : freq.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                  .Take(maxVocab).Select(kv => kv.Key).ToArray();

        var idx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < vocab.Length; i++) idx[vocab[i]] = i;
        var n = vocab.Length;
        if (n == 0) return new Dictionary<string, double>(StringComparer.Ordinal);

        // 稀疏邻接
        var nbr = new List<(int J, double W)>[n];
        for (var i = 0; i < n; i++) nbr[i] = new List<(int, double)>();
        var degree = new double[n];

        var win = Math.Max(window, 2);
        for (var i = 0; i < tokenList.Count; i++)
        {
            if (!idx.TryGetValue(tokenList[i], out var a)) continue;
            var upper = Math.Min(tokenList.Count, i + win);
            for (var j = i + 1; j < upper; j++)
            {
                if (!idx.TryGetValue(tokenList[j], out var b) || a == b) continue;
                var w = 1.0 / (j - i); // 近距权重更高
                nbr[a].Add((b, w));
                nbr[b].Add((a, w));
                degree[a] += w;
                degree[b] += w;
            }
        }

        var rank = new double[n];
        Array.Fill(rank, 1.0 / n);
        var next = new double[n];
        for (var it = 0; it < iterations; it++)
        {
            for (var i = 0; i < n; i++)
            {
                var s = 0.0;
                foreach (var (j, w) in nbr[i])
                    if (degree[j] > 0) s += (w / degree[j]) * rank[j];
                next[i] = (1 - damping) / n + damping * s;
            }
            (rank, next) = (next, rank);
        }

        var total = rank.Sum();
        if (total > 0)
            for (var i = 0; i < n; i++) rank[i] /= total; // 归一化，便于跨书比较

        var result = new Dictionary<string, double>(n, StringComparer.Ordinal);
        for (var i = 0; i < n; i++) result[vocab[i]] = rank[i];
        return result;
    }

    // ── 2. PMI ──────────────────────────────────────────
    /// <summary>经典 PMI（保留 v2 契约，金样 KG10 依赖此签名与口径）。</summary>
    public static double Pmi(int co, int a, int b, int total)
    {
        if (co <= 0 || a <= 0 || b <= 0 || total <= 0) return 0;
        var pmi = Math.Log2((co * (double)total) / ((double)a * b));
        return Math.Max(0, pmi); // 只留正关联
    }

    /// <summary>
    /// 平滑 PMI：对共现数与边缘计数做加一平滑，抑制低频词对的 PMI 爆炸。
    /// </summary>
    public static double PmiSmoothed(int co, int a, int b, int total, double alpha = 1.0)
    {
        if (co <= 0 || a <= 0 || b <= 0 || total <= 0) return 0;
        var denom = total + 4 * alpha;
        var pAb = (co + alpha) / denom;
        var pA = (a + alpha) / denom;
        var pB = (b + alpha) / denom;
        return Math.Max(0, Math.Log2(pAb / (pA * pB)));
    }

    /// <summary>
    /// 归一化 PMI（NPMI）= PMI / -log2(p_ab)，取值 [-1,1]。
    /// 裸 PMI 对低频词对天然偏高，作为边权很不稳定；NPMI 更适合直接当权重。
    /// </summary>
    public static double NormalizedPmi(int co, int a, int b, int total)
    {
        if (co <= 0 || a <= 0 || b <= 0 || total <= 0) return 0;
        var pAb = (double)co / total;
        if (pAb <= 0 || pAb >= 1) return 0;
        var pmi = Math.Log2(pAb / ((double)a / total * (b / (double)total)));
        return Math.Clamp(pmi / -Math.Log2(pAb), -1.0, 1.0);
    }

    // ── 3. 依赖句式 ─────────────────────────────────────
    // v2：每个概念现场 new Regex（O(概念数) 次编译）。v3：静态编译一次。
    private static readonly Regex SequentialRe = new(
        @"(?:基于|先学|掌握|了解|学习|先修|有了|学过)\s*(?<a>[一-鿿A-Za-z0-9_·、]{2,12})\s*" +
        @"(?:后|之后|再|然后|才能|才能理解|方可|进而)\s*(?<b>[一-鿿A-Za-z0-9_·、]{2,12})",
        RegexOptions.Compiled);

    private static readonly Regex FoundationRe = new(
        @"(?<a>[一-鿿A-Za-z0-9_·、]{2,12})\s*是\s*(?<b>[一-鿿A-Za-z0-9_·、]{2,12})\s*" +
        @"(?:的)?\s*(?:基础|前提|先修|先决条件)",
        RegexOptions.Compiled);

    private static readonly Regex DependsOnRe = new(
        @"(?<b>[一-鿿A-Za-z0-9_·、]{2,12})\s*(?:依赖|需要|要求|基于)\s*" +
        @"(?<a>[一-鿿A-Za-z0-9_·、]{2,12})",
        RegexOptions.Compiled);

    private static string? MatchConcept(string raw, IReadOnlyList<string> concepts, HashSet<string> set)
    {
        if (set.Contains(raw)) return raw;
        string? best = null; // 取最长包含匹配，避免短词误配
        foreach (var c in concepts)
            if (raw.Contains(c, StringComparison.Ordinal) && (best is null || c.Length > best.Length))
                best = c;
        return best;
    }

    /// <summary>
    /// 依赖句式抽取边 v3（三类句式）：
    ///   ①「基于 X 之后才能 Y」②「X 是 Y 的基础/前提」③「Y 依赖/需要 X」
    /// 统一返回 (先修 from, 后继 to)。
    /// </summary>
    public static IEnumerable<(string From, string To)> DependencyPatterns(string text, IEnumerable<string> concepts)
    {
        var list = concepts.Where(c => c.Length >= 2).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) yield break;
        var set = new HashSet<string>(list, StringComparer.Ordinal);

        foreach (var rx in new[] { SequentialRe, FoundationRe, DependsOnRe })
        foreach (Match m in rx.Matches(text))
        {
            if (!m.Groups.TryGetValue("a", out var ga) || !m.Groups.TryGetValue("b", out var gb)) continue;
            var a = MatchConcept(ga.Value.Trim(), list, set);
            var b = MatchConcept(gb.Value.Trim(), list, set);
            if (a is null || b is null || string.Equals(a, b, StringComparison.Ordinal)) continue;
            yield return (a, b);
        }
    }

    // ── 4. 一键构图 ─────────────────────────────────────
    /// <summary>
    /// 一键构图 v3：章节作 backbone，概念按 TextRank 挑选（去子词），
    /// 边 = 平滑 NPMI 共现 ∪ 依赖句式，术语按首次出现位置锚定到所属章节，
    /// 最后做 DAG 收尾。
    /// </summary>
    public static WeightedKg BuildFromText(
        string text,
        IReadOnlyList<Concept> chapters,
        int maxConcepts = 24,
        double pmiThreshold = 1.0,
        int minTermFreq = 2,
        bool dedupSubstrings = true)
    {
        var kg = new WeightedKg();
        foreach (var ch in chapters)
            kg.AddNode(ch.Id, ch.Title, string.IsNullOrEmpty(ch.Kind) ? "chapter" : ch.Kind);

        if (string.IsNullOrWhiteSpace(text)) return kg;

        var tokens = Tokenize(text);
        var ranks = TextRank(tokens);

        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) freq[t] = freq.GetValueOrDefault(t) + 1;

        bool IsChapterTitle(string t) =>
            chapters.Any(c => c.Title.Contains(t, StringComparison.Ordinal)
                              || t.Contains(c.Title, StringComparison.Ordinal));

        var candidates = ranks
            .Where(kv => freq.GetValueOrDefault(kv.Key) >= minTermFreq)
            .Where(kv => !IsChapterTitle(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        // 子词去重：高分候选已包含该词时丢弃
        var top = new List<string>();
        foreach (var kv in candidates)
        {
            if (top.Count >= maxConcepts) break;
            if (dedupSubstrings && top.Any(t => t.Contains(kv.Key, StringComparison.Ordinal)))
                continue;
            top.Add(kv.Key);
        }

        foreach (var (t, i) in top.Select((t, i) => (t, i)))
            kg.AddNode($"T{i + 1:00}", t, "term", ranks[t]);

        // 章节 backbone：按顺序弱先修
        for (var i = 0; i < chapters.Count - 1; i++)
            kg.AddEdge(chapters[i].Id, chapters[i + 1].Id, "prerequisite", 0.8, 0.7);

        // ── 共现 NPMI 边 ────────────────────────────────
        var co = new Dictionary<(string, string), int>();
        var cnt = top.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        var window = 8;
        var positions = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!cnt.ContainsKey(tokens[i])) continue;
            cnt[tokens[i]]++;
            positions++; // PMI 的分母基准：参与统计的位置数
            for (var j = i + 1; j < Math.Min(tokens.Length, i + window); j++)
            {
                if (!cnt.ContainsKey(tokens[j])) continue;
                var key = Ord(tokens[i], tokens[j]);
                co[key] = co.GetValueOrDefault(key) + 1;
            }
        }
        var total = Math.Max(positions, 1);
        foreach (var (key, c) in co.OrderByDescending(kv => kv.Value))
        {
            var a = cnt[key.Item1];
            var b = cnt[key.Item2];
            if (PmiSmoothed(c, a, b, total) < pmiThreshold) continue;
            var npmi = NormalizedPmi(c, a, b, total);
            var idA = NodeIdOf(kg, key.Item1);
            var idB = NodeIdOf(kg, key.Item2);
            if (idA is null || idB is null) continue;
            // 权重：NPMI 映射到 [0.2, 2.0]，比 v2 的 pmi/6 稳定
            kg.AddEdge(idA, idB, "related", 0.2 + 1.8 * Math.Clamp(npmi, 0, 1), Math.Clamp(npmi, 0.05, 1.0));
        }

        // ── 依赖句式 → prerequisite ─────────────────────
        foreach (var (fromTitle, toTitle) in DependencyPatterns(text, top))
        {
            var idA = NodeIdOf(kg, fromTitle);
            var idB = NodeIdOf(kg, toTitle);
            if (idA is null || idB is null) continue;
            kg.AddEdge(idA, idB, "prerequisite", 1.3, 0.9);
        }

        // ── 术语锚定（v2 最大 bug 修复点）────────────────
        // v2：chapters.FirstOrDefault(c => text.IndexOf(c.Title) >= 0) → 恒定同一章，
        //     所有术语挂到同一个父节点，图谱退化成星型。
        // v3：按术语首次出现位置，落在「起点不晚于它」的最后一个章节。
        var known = new List<(string Id, int Pos)>();
        foreach (var c in chapters)
        {
            var p = text.IndexOf(c.Title, StringComparison.Ordinal);
            if (p >= 0) known.Add((c.Id, p));
        }
        known = known.OrderBy(x => x.Pos).ToList();

        foreach (var n in kg.Nodes.Where(n => n.Kind == "term").ToList())
        {
            var first = text.IndexOf(n.Title, StringComparison.Ordinal);
            string? anchor = null;
            if (first >= 0 && known.Count > 0)
                foreach (var (id, pos) in known)
                    if (pos <= first) anchor = id;
            anchor ??= chapters.Count > 0 ? chapters[0].Id : null;
            if (anchor is null) continue;
            kg.AddEdge(anchor, n.Id, "prerequisite", 0.5, 0.5);
        }

        EnsureDag(kg);
        return kg;
    }

    /// <summary>
    /// DAG 收尾：检测先修边上的环，按置信度从低到高丢弃直到无环。
    /// 保证 GraphTopology.TopologicalOrder 不会拿到带环图。返回丢弃边数。
    /// </summary>
    public static int EnsureDag(WeightedKg kg)
    {
        var removed = 0;
        // 注意：GraphTopology.TopologicalOrder 遇环会抛异常，这里不能用它做探测，
        // 改用 DFS 回边检测——只要还有回边就继续删最弱的那条。
        var guard = kg.Edges.Count + 8;
        while (guard-- > 0)
        {
            var back = CycleEdges(kg);
            if (back.Count == 0) break; // 无回边 ⇔ 已是 DAG
            var victim = back.OrderBy(e => e.Confidence).ThenBy(e => e.Weight).First();
            kg.Edges.Remove(victim);
            removed++;
        }
        return removed;
    }

    /// <summary>是否已是 DAG（无回边）。</summary>
    public static bool IsDag(WeightedKg kg) => CycleEdges(kg).Count == 0;

    /// <summary>DFS 找出所有回边（环的组成部分）。</summary>
    private static List<WeightedKg.KgEdge> CycleEdges(WeightedKg kg)
    {
        var adj = new Dictionary<string, List<(string To, WeightedKg.KgEdge Edge)>>(StringComparer.Ordinal);
        foreach (var e in kg.Edges)
        {
            if (!adj.TryGetValue(e.From, out var l)) adj[e.From] = l = new List<(string, WeightedKg.KgEdge)>();
            l.Add((e.To, e));
        }
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 未访问 / 1 在栈 / 2 完成
        var back = new List<WeightedKg.KgEdge>();

        void Dfs(string u)
        {
            state[u] = 1;
            if (adj.TryGetValue(u, out var outs))
                foreach (var (v, edge) in outs)
                {
                    var st = state.GetValueOrDefault(v);
                    if (st == 0) Dfs(v);
                    else if (st == 1) back.Add(edge);
                }
            state[u] = 2;
        }

        foreach (var n in kg.Nodes)
            if (state.GetValueOrDefault(n.Id) == 0) Dfs(n.Id);

        return back;
    }

    private static (string, string) Ord(string a, string b)
        => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

    private static string? NodeIdOf(WeightedKg kg, string title)
        => kg.Nodes.FirstOrDefault(n => n.Title == title)?.Id;
}
