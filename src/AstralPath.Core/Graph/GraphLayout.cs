namespace AstralPath.Core.Graph;

/// <summary>
/// 分层布局（Sugiyama 简化）：x = 拓扑层，同层按重心法排序减交叉，
/// 供识网 DAG / 思维导图渲染。输出坐标在 [0,1]² 归一化。
/// </summary>
public static class GraphLayout
{
    public sealed record Placed(string Id, double X, double Y, int Layer);

    public static IReadOnlyList<Placed> Layered(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges)
    {
        if (nodes.Count == 0) return Array.Empty<Placed>();
        var layer = AstralPath.Core.Algorithms.GraphTopology.Layers(nodes, edges);
        var maxLayer = layer.Values.DefaultIfEmpty(0).Max();

        var groups = nodes
            .GroupBy(n => layer[n])
            .OrderBy(g => g.Key)
            .ToList();

        // 同层顺序：按前驱平均位置（重心）排，减少交叉
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var prevPos = nodes.ToDictionary(n => n, _ => 0.0, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var incoming = edges.ToLookup(e => e.To, e => e.From, StringComparer.Ordinal);
            var sorted = g.OrderBy(n => incoming[n].Any()
                    ? incoming[n].Average(p => prevPos[p])
                    : 0.0)
                .ThenBy(n => n, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                order[sorted[i]] = i;
                prevPos[sorted[i]] = i;
            }
        }

        var result = new List<Placed>(nodes.Count);
        foreach (var g in groups)
        {
            var count = Math.Max(g.Count(), 1);
            foreach (var n in g)
            {
                var x = maxLayer == 0 ? 0.5 : (double)layer[n] / maxLayer;
                // 同层垂直均分，重心居中
                var y = count == 1 ? 0.5 : (order[n] + 0.5) / count;
                result.Add(new Placed(n, x, y, layer[n]));
            }
        }
        return result;
    }

    /// <summary>边交叉数（用于评估布局质量）。</summary>
    public static int Crossings(
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, Placed> pos)
    {
        var crossings = 0;
        var list = edges.ToList();
        for (var i = 0; i < list.Count; i++)
        for (var j = i + 1; j < list.Count; j++)
        {
            if (!pos.TryGetValue(list[i].From, out var a1)) continue;
            if (!pos.TryGetValue(list[i].To, out var a2)) continue;
            if (!pos.TryGetValue(list[j].From, out var b1)) continue;
            if (!pos.TryGetValue(list[j].To, out var b2)) continue;
            if (SegmentsIntersect(a1, a2, b1, b2)) crossings++;
        }
        return crossings;
    }

    private static bool SegmentsIntersect(Placed p1, Placed p2, Placed p3, Placed p4)
    {
        double d(Placed a, Placed b, Placed c) => (c.Y - a.Y) * (b.X - a.X) - (c.X - a.X) * (b.Y - a.Y);
        var d1 = d(p3, p4, p1);
        var d2 = d(p3, p4, p2);
        var d3 = d(p1, p2, p3);
        var d4 = d(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
               && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
