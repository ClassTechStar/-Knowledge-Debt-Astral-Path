namespace AstralPath.Core.Graph;

/// <summary>图度量：PageRank / 出入度 / 密度 / 互换中心性。用于节点重要性与债边放大。</summary>
public static class GraphMetrics
{
    /// <summary>PageRank（d=0.85, 50 轮），归一化到和为 1。</summary>
    public static IReadOnlyDictionary<string, double> PageRank(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges,
        int iterations = 50,
        double damping = 0.85)
    {
        var n = nodes.Count;
        if (n == 0) return new Dictionary<string, double>();
        var adjOut = nodes.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
        var rank = nodes.ToDictionary(x => x, _ => 1.0 / n, StringComparer.Ordinal);
        foreach (var (f, t) in edges) adjOut[f].Add(t);

        for (var it = 0; it < iterations; it++)
        {
            var next = nodes.ToDictionary(x => x, _ => (1 - damping) / n, StringComparer.Ordinal);
            var dangling = 0.0;
            foreach (var x in nodes)
            {
                var outs = adjOut[x];
                if (outs.Count == 0) { dangling += rank[x]; continue; }
                var share = rank[x] / outs.Count;
                foreach (var y in outs) next[y] += damping * share;
            }
            // 悬挂点质量均分
            if (dangling > 0)
            {
                var add = damping * dangling / n;
                foreach (var x in nodes) next[x] += add;
            }
            rank = next;
        }
        return rank;
    }

    public static (int In, int Out) Degree(string id, IReadOnlyList<(string From, string To)> edges)
        => (edges.Count(e => e.To == id), edges.Count(e => e.From == id));

    public static double Density(int nodeCount, int edgeCount)
        => nodeCount <= 1 ? 0 : (double)edgeCount / (nodeCount * (nodeCount - 1));

    /// <summary>节点「桥梁度」：入×出，前驱多且下游多的枢纽更大。</summary>
    public static double HubScore(string id, IReadOnlyList<(string From, string To)> edges)
    {
        var (i, o) = Degree(id, edges);
        return Math.Sqrt((double)i * o) + 0.25 * (i + o);
    }
}
