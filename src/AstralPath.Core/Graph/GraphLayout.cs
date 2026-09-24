namespace AstralPath.Core.Graph;

/// <summary>
/// 分层布局 v3（Sugiyama 简化但可用）：
///   x = 拓扑层；同层顺序用「重心法多趟迭代」——下扫（看前驱）+ 上扫（看后继）交替，
///   每趟算真实交叉数，保留交叉最少的一套顺序。
///
/// v2 的三个问题：
///   ① 只做单趟下扫，交叉数明显偏高
///   ② edges.ToLookup 写在循环里 → 每层重建一次，O(层×边)
///   ③ 没有「择优」机制，扫完什么样就什么样
/// </summary>
public static class GraphLayout
{
    public sealed record Placed(string Id, double X, double Y, int Layer);

    /// <summary>默认扫描趟数。</summary>
    public const int DefaultSweeps = 8;

    public static IReadOnlyList<Placed> Layered(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges)
        => Layered(nodes, edges, DefaultSweeps);

    public static IReadOnlyList<Placed> Layered(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges,
        int sweeps)
    {
        if (nodes.Count == 0) return Array.Empty<Placed>();

        var layer = AstralPath.Core.Algorithms.GraphTopology.Layers(nodes, edges);
        var maxLayer = layer.Values.DefaultIfEmpty(0).Max();

        // 归一化边：只保留两端都在节点集内的
        var nodeSet = new HashSet<string>(nodes, StringComparer.Ordinal);
        var normEdges = edges.Where(e => nodeSet.Contains(e.From) && nodeSet.Contains(e.To)).ToList();

        // 邻接表只建一次（v2 在每层循环里重建）
        var preds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var succs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in nodes) { preds[n] = new List<string>(); succs[n] = new List<string>(); }
        foreach (var (from, to) in normEdges)
        {
            if (from == to) continue;
            succs[from].Add(to);
            preds[to].Add(from);
        }

        var byLayer = nodes
            .GroupBy(n => layer[n])
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(n => n, StringComparer.Ordinal).ToList())
            .ToList();

        // ── 初始化顺序 ──────────────────────────────────
        var pos = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var g in byLayer)
            for (var i = 0; i < g.Count; i++) pos[g[i]] = i;

        int TotalCrossings()
        {
            var sum = 0;
            for (var li = 0; li < byLayer.Count - 1; li++)
            {
                var upper = byLayer[li];
                var lower = byLayer[li + 1];
                var lowerSet = new HashSet<string>(lower, StringComparer.Ordinal);
                var seq = new List<int>();
                foreach (var u in upper)
                    foreach (var v in succs[u])
                        if (lowerSet.Contains(v)) seq.Add(pos[v]);
                sum += CountInversions(seq);
            }
            return sum;
        }

        void Sweep(bool downward)
        {
            var indices = downward
                ? Enumerable.Range(0, byLayer.Count)
                : Enumerable.Range(0, byLayer.Count).Reverse();
            foreach (var li in indices)
            {
                var g = byLayer[li];
                var key = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var n in g)
                {
                    var related = downward ? preds[n] : succs[n];
                    var vals = related.Where(pos.ContainsKey).Select(p => (double)pos[p]).ToList();
                    // 无相邻节点者保持原位，避免被挤到边上乱跳
                    key[n] = vals.Count > 0 ? vals.Average() : pos[n];
                }
                var sorted = g.OrderBy(n => key[n]).ThenBy(n => n, StringComparer.Ordinal).ToList();
                for (var i = 0; i < sorted.Count; i++) pos[sorted[i]] = i;
                byLayer[li] = sorted;
            }
        }

        var bestOrder = byLayer.Select(g => g.ToList()).ToList();
        var bestCross = int.MaxValue;

        // 首趟下扫后评估
        Sweep(true);
        var cur = TotalCrossings();
        if (cur < bestCross) { bestCross = cur; bestOrder = byLayer.Select(g => g.ToList()).ToList(); }

        var s = Math.Max(sweeps, 1);
        for (var it = 0; it < s; it++)
        {
            Sweep(false); // 上扫
            cur = TotalCrossings();
            if (cur < bestCross) { bestCross = cur; bestOrder = byLayer.Select(g => g.ToList()).ToList(); }

            Sweep(true);  // 下扫
            cur = TotalCrossings();
            if (cur < bestCross) { bestCross = cur; bestOrder = byLayer.Select(g => g.ToList()).ToList(); }

            if (bestCross == 0) break; // 已最优，提前收敛
        }

        // ── 输出坐标 ────────────────────────────────────
        var result = new List<Placed>(nodes.Count);
        for (var li = 0; li < bestOrder.Count; li++)
        {
            var g = bestOrder[li];
            var count = Math.Max(g.Count, 1);
            for (var i = 0; i < g.Count; i++)
            {
                var n = g[i];
                var x = maxLayer == 0 ? 0.5 : (double)layer[n] / maxLayer;
                var y = count == 1 ? 0.5 : (i + 0.5) / count;
                result.Add(new Placed(n, x, y, layer[n]));
            }
        }
        return result;
    }

    /// <summary>统计一次布局方案的实际交叉数（相邻层之间）。</summary>
    public static int CountCrossings(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges)
    {
        var placed = Layered(nodes, edges);
        var map = placed.ToDictionary(p => p.Id, StringComparer.Ordinal);
        return Crossings(edges, map);
    }

    /// <summary>边交叉数（几何判定，用于评估布局质量；保留 v2 契约）。</summary>
    public static int Crossings(
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlyDictionary<string, Placed> pos)
    {
        var crossings = 0;
        var list = edges.Where(e => e.From != e.To).ToList();
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

    /// <summary>归并排序数逆序对 —— 用于 O(E log E) 计层间交叉数。</summary>
    internal static int CountInversions(List<int> seq)
    {
        if (seq.Count < 2) return 0;
        var buf = new int[seq.Count];
        var arr = seq.ToArray();
        return MergeSort(arr, buf, 0, arr.Length - 1);
    }

    private static int MergeSort(int[] a, int[] buf, int lo, int hi)
    {
        if (lo >= hi) return 0;
        var mid = (lo + hi) / 2;
        var inv = MergeSort(a, buf, lo, mid) + MergeSort(a, buf, mid + 1, hi);
        var i = lo; var j = mid + 1; var k = lo;
        while (i <= mid && j <= hi)
        {
            if (a[i] <= a[j]) buf[k++] = a[i++];
            else { buf[k++] = a[j++]; inv += mid - i + 1; }
        }
        while (i <= mid) buf[k++] = a[i++];
        while (j <= hi) buf[k++] = a[j++];
        for (var t = lo; t <= hi; t++) a[t] = buf[t];
        return inv;
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
