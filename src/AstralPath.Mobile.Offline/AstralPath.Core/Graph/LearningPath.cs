namespace AstralPath.Core.Graph;

/// <summary>学习路径：瓶颈优先 + 关键路径强化 + 分层推进。</summary>
public static class LearningPath
{
    public sealed record PathStep(string KpId, string Title, int Layer, string Reason, double Priority);

    /// <summary>
    /// 排序：① 瓶颈（阻塞下游多）② 关键路径 ③ 层（先低后高）④ Hub/PageRank。
    /// 保证拓扑可行（层低的永远不晚于层高的前置）。
    /// </summary>
    public static IReadOnlyList<PathStep> Order(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, string>? titles = null)
    {
        if (nodes.Count == 0) return Array.Empty<PathStep>();
        var layer = Algorithms.GraphTopology.Layers(nodes, edges);
        var critical = new HashSet<string>(
            Algorithms.GraphTopology.CriticalPath(nodes, edges), StringComparer.Ordinal);
        var blocked = Algorithms.GraphTopology.Bottlenecks(nodes, edges)
            .ToDictionary(b => b.Id, b => b.Blocked, StringComparer.Ordinal);
        var rank = GraphMetrics.PageRank(nodes, edges);
        var maxBlock = Math.Max(1, blocked.Values.DefaultIfEmpty(0).Max());

        var steps = nodes.Select(n =>
        {
            var blockScore = (double)blocked.GetValueOrDefault(n) / maxBlock;
            var critScore = critical.Contains(n) ? 1.0 : 0.0;
            var prio = 2.5 * blockScore + 1.5 * critScore + 0.5 * layer[n] + rank.GetValueOrDefault(n);
            var reason = critical.Contains(n) && blocked.GetValueOrDefault(n) > 0
                ? "关键路径瓶颈"
                : critical.Contains(n) ? "关键路径"
                : blocked.GetValueOrDefault(n) > 0 ? "多下游依赖"
                : "分层推进";
            var title = titles?.GetValueOrDefault(n) ?? n;
            return new PathStep(n, title, layer[n], reason, prio);
        }).OrderBy(s => s.Layer).ThenByDescending(s => s.Priority).ThenBy(s => s.KpId, StringComparer.Ordinal).ToList();

        return steps;
    }
}
