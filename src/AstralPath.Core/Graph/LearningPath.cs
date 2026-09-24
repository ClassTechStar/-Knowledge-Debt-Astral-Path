namespace AstralPath.Core.Graph;

/// <summary>
/// 学习路径 v3：瓶颈（阻塞下游质量）优先 + 关键路径强化 + 分层推进 + 归一化 PageRank。
///
/// v2 的问题：
///   ① PageRank 全图求和为 1，250 个节点时单节点只有 ~0.004，加进优先级等于没加
///   ② 瓶颈用「删除该点后多少点不可达」的模拟删除，O(V·(V+E)) 且对链式图区分度差
///   ③ 排序里 layer 既当第一关键字又参与打分，重复计权
/// v3：主看下游阻塞质量（DownstreamMass，传递闭包规模），关键路径加成，
///     PageRank 按最大值归一化到 [0,1] 后再参与。
/// </summary>
public static class LearningPath
{
    public sealed record PathStep(
        string KpId,
        string Title,
        int Layer,
        string Reason,
        double Priority,
        int Blocked);

    public static IReadOnlyList<PathStep> Order(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, string>? titles = null)
        => Order(nodes, edges, titles, blockWeight: 2.5, criticalWeight: 1.5, rankWeight: 0.8);

    public static IReadOnlyList<PathStep> Order(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, string>? titles,
        double blockWeight,
        double criticalWeight,
        double rankWeight)
    {
        if (nodes.Count == 0) return Array.Empty<PathStep>();

        var layer = Algorithms.GraphTopology.Layers(nodes, edges);
        var critical = new HashSet<string>(
            Algorithms.GraphTopology.CriticalPath(nodes, edges), StringComparer.Ordinal);
        var mass = Algorithms.GraphTopology.DownstreamMass(nodes, edges);
        var bottlenecks = Algorithms.GraphTopology.Bottlenecks(nodes, edges)
            .ToDictionary(b => b.Id, b => b.Blocked, StringComparer.Ordinal);

        var rank = GraphMetrics.PageRank(nodes, edges);
        var maxRank = rank.Values.DefaultIfEmpty(0).Max();
        var maxMass = Math.Max(1, mass.Values.DefaultIfEmpty(0).Max());

        var steps = nodes.Select(n =>
        {
            var blocked = mass.GetValueOrDefault(n);
            var blockScore = (double)blocked / maxMass;                     // 阻塞下游质量
            var critScore = critical.Contains(n) ? 1.0 : 0.0;
            var rankScore = maxRank > 0 ? rank.GetValueOrDefault(n) / maxRank : 0; // 归一化到 [0,1]
            var prio = blockWeight * blockScore
                       + criticalWeight * critScore
                       + rankWeight * rankScore;

            var isBottleneck = bottlenecks.GetValueOrDefault(n) > 0;
            var reason = critical.Contains(n) && isBottleneck ? "关键路径瓶颈"
                : critical.Contains(n) ? "关键路径"
                : isBottleneck ? "多下游依赖"
                : blocked > 0 ? "阻塞后续知识点"
                : "分层推进";

            return new PathStep(n, titles?.GetValueOrDefault(n) ?? n, layer[n], reason, prio, blocked);
        })
        // 先分层保证拓扑可行，层内按优先级；同分用 id 稳定排序
        .OrderBy(s => s.Layer)
        .ThenByDescending(s => s.Priority)
        .ThenBy(s => s.KpId, StringComparer.Ordinal)
        .ToList();

        return steps;
    }
}
