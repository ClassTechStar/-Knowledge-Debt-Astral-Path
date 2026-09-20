using AstralPath.Contracts;

namespace AstralPath.Shared.Services;

/// <summary>图上已定位的节点（坐标由 <see cref="GraphLayout"/> 确定性计算）。</summary>
public sealed record GraphNodePos(
    string Id, string Name, string Course, string Band, double Score,
    double X, double Y, double Radius, int Layer);

/// <summary>图上已定位的债边（含两端坐标，便于直接绘制）。</summary>
public sealed record GraphEdgePos(
    string From, string To, string Status, double Impact, double Weight,
    string FromName, string ToName,
    double X1, double Y1, double X2, double Y2);

/// <summary>一帧图谱场景（节点 + 边 + 画布尺寸）。不可变，便于缓存与测试。</summary>
public sealed record GraphScene(
    IReadOnlyList<GraphNodePos> Nodes,
    IReadOnlyList<GraphEdgePos> Edges,
    double Width,
    double Height)
{
    public static GraphScene Empty { get; } = new(
        Array.Empty<GraphNodePos>(), Array.Empty<GraphEdgePos>(), 0, 0);
}

/// <summary>
/// 图谱布局（方案 §12.2 / §14.7）。
///
/// 算法：**最长路径分层**（Kahn 拓扑序 + 松弛），把前置依赖链排成一条「星穹学途」。
/// 分层方向按画布宽高比自动选择（宽 → 层沿 X；高 → 层沿 Y），从而在桌面宽栏与
/// 移动窄栏下都保持可读。
///
/// 特性：
/// <list type="bullet">
///   <item>确定性：同输入必得同输出（节点按 Id 排序），因此可写快照测试；</item>
///   <item>线性复杂度：O(V+E)，400 节点布局远低于 16ms 帧预算；</item>
///   <item>无环保护：即使图包校验被绕过，Kahn 也会安全退出，剩余节点归入末层。</item>
/// </list>
/// </summary>
public static class GraphLayout
{
    public const double NodeRadius = 14;
    public const double Padding = 30;

    public static GraphScene Build(GraphViewDto dto, double width, double height)
    {
        if (dto.Nodes.Count == 0 || width <= 1 || height <= 1)
            return GraphScene.Empty with { Width = Math.Max(0, width), Height = Math.Max(0, height) };

        var ids = new HashSet<string>(dto.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var outAdj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in dto.Nodes) inDegree[n.Id] = 0;

        var validEdges = new List<GraphViewEdgeDto>(dto.Edges.Count);
        foreach (var e in dto.Edges)
        {
            if (!ids.Contains(e.From) || !ids.Contains(e.To)) continue;
            validEdges.Add(e);
            if (!outAdj.TryGetValue(e.From, out var list)) outAdj[e.From] = list = new List<string>();
            list.Add(e.To);
            inDegree[e.To]++;
        }

        var layer = ComputeLayers(dto.Nodes.Select(n => n.Id), inDegree, outAdj);
        var layered = dto.Nodes.GroupBy(n => layer[n.Id]).OrderBy(g => g.Key).ToList();
        var layerCount = layered.Count;

        var horizontal = width >= height;
        var pos = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);

        for (var li = 0; li < layerCount; li++)
        {
            var ordered = layered[li].OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
            var count = ordered.Count;

            // 层内主轴坐标（层序号方向）
            var alongSpan = (horizontal ? width : height) - 2 * Padding;
            var along = layerCount > 1
                ? Padding + alongSpan * li / (layerCount - 1)
                : (horizontal ? width : height) / 2.0;

            // 层内展开轴坐标
            var crossSpan = (horizontal ? height : width) - 2 * Padding;
            for (var i = 0; i < count; i++)
            {
                var cross = count > 1
                    ? Padding + crossSpan * i / (count - 1)
                    : (horizontal ? height : width) / 2.0;

                pos[ordered[i].Id] = horizontal ? (along, cross) : (cross, along);
            }
        }

        var nodes = dto.Nodes
            .Select(n =>
            {
                var (x, y) = pos[n.Id];
                var radius = NodeRadius + Math.Clamp((100 - n.Score) / 100 * 6, 0, 6);
                return new GraphNodePos(n.Id, n.Name, n.Course, n.Band, n.Score, x, y, radius, layer[n.Id]);
            })
            .ToList();

        var edges = new List<GraphEdgePos>(validEdges.Count);
        foreach (var e in validEdges)
        {
            if (!pos.TryGetValue(e.From, out var a) || !pos.TryGetValue(e.To, out var b)) continue;
            edges.Add(new GraphEdgePos(e.From, e.To, e.Status, e.Impact, e.Weight,
                e.FromName, e.ToName, a.X, a.Y, b.X, b.Y));
        }

        return new GraphScene(nodes, edges, width, height);
    }

    /// <summary>最长路径分层：拓扑序上逐边松弛，未参与拓扑的节点（环内/孤立）归入末层。</summary>
    private static Dictionary<string, int> ComputeLayers(
        IEnumerable<string> nodeIds,
        Dictionary<string, int> inDegree,
        Dictionary<string, List<string>> outAdj)
    {
        var layer = nodeIds.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var remaining = new Dictionary<string, int>(inDegree, StringComparer.Ordinal);

        var queue = new Queue<string>();
        foreach (var kv in remaining.Where(kv => kv.Value == 0).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            queue.Enqueue(kv.Key);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outAdj.TryGetValue(current, out var nexts)) continue;

            foreach (var next in nexts)
            {
                if (layer[next] < layer[current] + 1) layer[next] = layer[current] + 1;
                if (--remaining[next] == 0) queue.Enqueue(next);
            }
        }

        return layer;
    }
}
