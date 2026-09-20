namespace AstralPath.Core.Graph;

/// <summary>CSR 邻接（§48.1）：offsets + dst + type，段内按 (type, dst) 排序。</summary>
public sealed class CsrGraph
{
    public int NodeCount { get; }
    public IReadOnlyDictionary<string, int> IdToIndex { get; }
    public IReadOnlyList<string> IndexToId { get; }
    public IReadOnlyList<int> Offsets { get; }
    public IReadOnlyList<int> AdjDst { get; }
    public IReadOnlyList<int> AdjType { get; }
    public IReadOnlyList<uint> TypeBitmap { get; }
    public IReadOnlyList<string> TypeNames { get; }

    private CsrGraph(
        int nodeCount,
        Dictionary<string, int> idToIndex,
        List<string> indexToId,
        List<int> offsets,
        List<int> adjDst,
        List<int> adjType,
        List<uint> typeBitmap,
        List<string> typeNames)
    {
        NodeCount = nodeCount;
        IdToIndex = idToIndex;
        IndexToId = indexToId;
        Offsets = offsets;
        AdjDst = adjDst;
        AdjType = adjType;
        TypeBitmap = typeBitmap;
        TypeNames = typeNames;
    }

    public static CsrGraph Build(
        IEnumerable<(string From, string To, string EdgeType)> edges,
        IEnumerable<string>? extraNodes = null)
    {
        var edgeList = edges.ToList();
        var ids = new List<string>();
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string id)
        {
            if (string.IsNullOrEmpty(id) || map.ContainsKey(id)) return;
            map[id] = ids.Count;
            ids.Add(id);
        }
        foreach (var n in extraNodes ?? Enumerable.Empty<string>()) Add(n);
        foreach (var e in edgeList) { Add(e.From); Add(e.To); }

        var typeNames = edgeList.Select(e => e.EdgeType ?? "prerequisite").Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var typeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < typeNames.Count; i++) typeIndex[typeNames[i]] = i;

        var buckets = new List<(int From, int To, int Type)>[ids.Count];
        for (var i = 0; i < ids.Count; i++) buckets[i] = new List<(int, int, int)>();
        foreach (var e in edgeList)
        {
            if (!map.TryGetValue(e.From, out var f) || !map.TryGetValue(e.To, out var t)) continue;
            var ti = typeIndex.GetValueOrDefault(e.EdgeType ?? "prerequisite", 0);
            buckets[f].Add((f, t, ti));
        }

        var offsets = new List<int>(ids.Count + 1) { 0 };
        var dst = new List<int>();
        var typ = new List<int>();
        var bitmap = new List<uint>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var adj = buckets[i].OrderBy(x => x.Type).ThenBy(x => x.To).ToList();
            uint bits = 0;
            foreach (var a in adj)
            {
                dst.Add(a.To);
                typ.Add(a.Type);
                bits |= 1u << (a.Type % 32);
            }
            bitmap.Add(bits);
            offsets.Add(dst.Count);
        }

        return new CsrGraph(ids.Count, map, ids, offsets, dst, typ, bitmap, typeNames);
    }

    public bool TryIndex(string id, out int index) => IdToIndex.TryGetValue(id, out index);

    /// <summary>单跳邻接。</summary>
    public IReadOnlyList<int> Neighbors(int node)
    {
        if (node < 0 || node >= NodeCount) return Array.Empty<int>();
        var lo = Offsets[node];
        var hi = Offsets[node + 1];
        if (hi <= lo) return Array.Empty<int>();
        var span = AdjDst.Skip(lo).Take(hi - lo).ToList();
        return span;
    }

    /// <summary>按关系类型过滤（段内二分 + 位图 O(1) 空结果）。</summary>
    public IReadOnlyList<int> NeighborsOfType(int node, string edgeType)
    {
        if (node < 0 || node >= NodeCount) return Array.Empty<int>();
        var ti = TypeNames.ToList().IndexOf(edgeType);
        if (ti < 0) return Array.Empty<int>();
        if ((TypeBitmap[node] & (1u << (ti % 32))) == 0) return Array.Empty<int>();
        var lo = Offsets[node];
        var hi = Offsets[node + 1];
        var result = new List<int>();
        for (var i = lo; i < hi; i++)
        {
            if (AdjType[i] == ti) result.Add(AdjDst[i]);
        }
        return result;
    }

    /// <summary>二跳邻居。</summary>
    public IReadOnlyList<int> TwoHop(int node)
    {
        var first = Neighbors(node);
        var set = new HashSet<int>();
        foreach (var n in first)
        foreach (var m in Neighbors(n))
        {
            if (m != node) set.Add(m);
        }
        return set.ToList();
    }

    /// <summary>对象图结果与 CSR 结果逐节点等价（§48 GO-C1）。</summary>
    public static bool EquivalentTo(
        IEnumerable<(string From, string To, string EdgeType)> edges,
        IEnumerable<string> nodes)
    {
        var obj = new Dictionary<string, List<(string To, string Type)>>(StringComparer.Ordinal);
        var idSet = nodes.ToHashSet(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            idSet.Add(e.From); idSet.Add(e.To);
            if (!obj.ContainsKey(e.From)) obj[e.From] = new List<(string, string)>();
            obj[e.From].Add((e.To, e.EdgeType ?? "prerequisite"));
        }
        var csr = Build(edges, idSet);
        foreach (var id in idSet)
        {
            if (!csr.TryIndex(id, out var idx)) return false;
            var got = csr.Neighbors(idx).Select(i => csr.IndexToId[i]).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var exp = (obj.GetValueOrDefault(id) ?? new List<(string, string)>())
                .Select(x => x.To).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!got.SequenceEqual(exp)) return false;
        }
        return true;
    }
}

/// <summary>知识库文档（§45）。</summary>
public sealed record KbDocument(
    string Id,
    string Title,
    string OwnerUserId,
    string Visibility, // private | consented | course | public
    string CourseCode,
    string[] Tags,
    string Status, // parsing | ready | failed
    int CharCount,
    string? GraphId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Error = null);

/// <summary>用户学习画像（§46）。</summary>
public sealed record UserProfileTag(
    string StudentId,
    string Tag,
    string Domain, // e.g. concept | pace | consent
    double Weight,
    bool OptOut,
    DateTime UpdatedAt);

public sealed record LearningProfile(
    string StudentId,
    IReadOnlyList<UserProfileTag> Tags,
    bool OptOut,
    DateTime UpdatedAt)
{
    public IReadOnlyList<UserProfileTag> VisibleTags(bool teacherSide)
        => OptOut || teacherSide == false
            ? Tags.Where(t => !t.OptOut).Where(t => t.Domain is not ("sensitive" or "semantic" or "crisis")).ToList()
            : Tags.Where(t => !t.OptOut).ToList();
}
