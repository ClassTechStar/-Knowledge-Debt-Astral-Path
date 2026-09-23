namespace AstralPath.Core.Algorithms;

/// <summary>图拓扑：拓扑序 / 分层 / 关键路径 / 瓶颈 / 下游阻塞。禁止发明先修边。</summary>
public static class GraphTopology
{
    /// <summary>Kahn 拓扑序；有环时抛出并列出环节点。</summary>
    public static IReadOnlyList<string> TopologicalOrder(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges)
    {
        var indeg = nodes.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        var adj = nodes.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (from, to) in edges)
        {
            if (!indeg.ContainsKey(from) || !indeg.ContainsKey(to))
                throw new InvalidOperationException($"edge endpoint missing: {from}->{to}");
            adj[from].Add(to);
            indeg[to]++;
        }

        var queue = new Queue<string>(nodes.Where(n => indeg[n] == 0).OrderBy(n => n, StringComparer.Ordinal));
        var order = new List<string>(nodes.Count);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            node_add(order, n);
            foreach (var m in adj[n])
            {
                indeg[m]--;
                if (indeg[m] == 0) queue.Enqueue(m);
            }
        }

        if (order.Count != nodes.Count)
        {
            var cyclic = nodes.Except(order, StringComparer.Ordinal).ToList();
            throw new InvalidOperationException("graph contains cycle: " + string.Join(",", cyclic));
        }
        return order;
    }

    private static void node_add(List<string> order, string n) => order.Add(n);

    /// <summary>先修直接依赖（from → to 表示 to 依赖 from）。</summary>
    public static IReadOnlyList<string> DirectPrereqs(string kpId, IReadOnlyList<(string From, string To)> edges)
        => edges.Where(e => e.To == kpId).Select(e => e.From).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// 最长先修路径长度（0-based layer）。layer(n) = 1+max(layer(prereq))，
    /// 用于「必须先学什么」与难度代理。
    /// </summary>
    public static IReadOnlyDictionary<string, int> Layers(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges)
    {
        var order = TopologicalOrder(nodes, edges);
        var layer = nodes.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        var incoming = edges.ToLookup(e => e.To, e => e.From, StringComparer.Ordinal);
        foreach (var n in order)
        {
            var best = 0;
            foreach (var p in incoming[n])
                best = Math.Max(best, layer[p] + 1);
            layer[n] = best;
        }
        return layer;
    }

    /// <summary>关键路径：层最大且能连到终端的最长链（学习硬骨头）。</summary>
    public static IReadOnlyList<string> CriticalPath(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges)
    {
        var order = TopologicalOrder(nodes, edges);
        var layer = Layers(nodes, edges);
        var incoming = edges.ToLookup(e => e.To, e => e.From, StringComparer.Ordinal);
        // dp 记录到达该节点的最长路径
        var dp = nodes.ToDictionary(n => n, _ => 1, StringComparer.Ordinal);
        var parent = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var n in order)
        {
            foreach (var p in incoming[n])
            {
                if (dp[p] + 1 > dp[n])
                {
                    dp[n] = dp[p] + 1;
                    parent[n] = p;
                }
            }
        }
        var end = order.OrderByDescending(n => dp[n]).ThenBy(n => n, StringComparer.Ordinal).First();
        var path = new List<string>();
        var cur = end;
        while (cur is not null)
        {
            path.Add(cur);
            cur = parent.TryGetValue(cur, out var p) ? p : null;
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// 瓶颈：从「全图源点」出发，删掉该点后不可达的节点数。
    /// 源点被删 → 几乎全图阻塞；叶子被删 → 不阻塞别人。
    /// </summary>
    public static IReadOnlyList<(string Id, int Blocked)> Bottlenecks(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges)
    {
        var adj = nodes.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (f, t) in edges) adj[f].Add(t);
        var sources = nodes.Where(n => edges.All(e => e.To != n)).ToList();

        int Reachable(string? blocked)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var q = new Queue<string>();
            foreach (var s in sources)
                if (s != blocked) q.Enqueue(s);
            while (q.Count > 0)
            {
                var x = q.Dequeue();
                if (!seen.Add(x)) continue;
                foreach (var y in adj[x])
                    if (y != blocked) q.Enqueue(y);
            }
            return seen.Count;
        }

        var full = Reachable(null);
        var result = new List<(string Id, int Blocked)>();
        foreach (var n in nodes)
        {
            var reach = Reachable(n);
            result.Add((n, Math.Max(0, full - reach)));
        }
        return result.OrderByDescending(r => r.Blocked).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>下游传递闭包规模（含自身=0 的直接计数）。债边级联用。</summary>
    public static IReadOnlyDictionary<string, int> DownstreamMass(
        IReadOnlyList<string> nodes,
        IReadOnlyList<(string From, string To)> edges)
    {
        var adj = nodes.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (f, t) in edges) adj[f].Add(t);
        var memo = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        HashSet<string> Visit(string n, HashSet<string> onStack)
        {
            if (memo.TryGetValue(n, out var cached)) return cached;
            if (onStack.Contains(n)) return new HashSet<string>(StringComparer.Ordinal); // 防环
            onStack.Add(n);
            var acc = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in adj[n])
            {
                acc.Add(m);
                foreach (var x in Visit(m, onStack)) acc.Add(x);
            }
            onStack.Remove(n);
            memo[n] = acc;
            return acc;
        }

        foreach (var n in nodes) Visit(n, new HashSet<string>(StringComparer.Ordinal));
        return memo.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
    }
}
