using System.Text.Json;

namespace AstralPath.Graph;

public sealed record KpNode(string Id, string Name, string Course, string Description = "");

public sealed record KpEdge(
    string From,
    string To,
    string EdgeType, // prerequisite | transfer_gap
    double Weight,
    string Source);

public sealed record GraphPack(
    string PackId,
    string Name,
    string CourseScope,
    int GraphVersion,
    string PublishedAt,
    IReadOnlyList<KpNode> Nodes,
    IReadOnlyList<KpEdge> Edges);

public sealed record GraphValidateResult(
    bool Ok,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<string> Cycles,
    IReadOnlyList<string> Issues);

/// <summary>graph-svc：无环校验 + 版本化（禁 LLM 发明边）。</summary>
public sealed class KnowledgeGraph
{
    private readonly Dictionary<string, KpNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<KpEdge> _edges = new();
    private readonly Dictionary<string, List<string>> _out = new(StringComparer.Ordinal);

    public int GraphVersion { get; }
    public string PackId { get; }

    public KnowledgeGraph(GraphPack pack)
    {
        PackId = pack.PackId;
        GraphVersion = pack.GraphVersion;
        foreach (var n in pack.Nodes)
        {
            _nodes[n.Id] = n;
            _out[n.Id] = new List<string>();
        }
        foreach (var e in pack.Edges)
        {
            if (!_nodes.ContainsKey(e.From) || !_nodes.ContainsKey(e.To))
                continue;
            _edges.Add(e);
            _out[e.From].Add(e.To);
        }
    }

    public IReadOnlyCollection<KpNode> Nodes => _nodes.Values;
    public IReadOnlyList<KpEdge> Edges => _edges;
    public bool TryGetNode(string id, out KpNode node) => _nodes.TryGetValue(id, out node!);
    public IReadOnlyList<string> OutNeighbors(string id) => _out.GetValueOrDefault(id) ?? new List<string>();

    public IReadOnlyList<KpEdge> EdgesOfNode(string kpId)
        => _edges.Where(e => e.From == kpId || e.To == kpId).ToList();

    public GraphValidateResult Validate()
    {
        var issues = new List<string>();
        if (_nodes.Count < 30)
            issues.Add($"节点数 {_nodes.Count} < 30（课程包要求）");
        if (_edges.Count < 40)
            issues.Add($"边数 {_edges.Count} < 40（课程包要求）");

        foreach (var e in _edges)
        {
            if (!_nodes.ContainsKey(e.From))
                issues.Add($"边起点不存在: {e.From}");
            if (!_nodes.ContainsKey(e.To))
                issues.Add($"边终点不存在: {e.To}");
            if (e.Weight <= 0 || e.Weight > 2)
                issues.Add($"边权重非法: {e.From}->{e.To} weight={e.Weight}");
            if (string.IsNullOrWhiteSpace(e.Source))
                issues.Add($"边缺少 source: {e.From}->{e.To}");
        }

        var cycles = FindCycles();
        if (cycles.Count > 0)
            issues.AddRange(cycles.Select(c => $"存在环: {c}"));

        return new GraphValidateResult(cycles.Count == 0 && issues.Count == 0, _nodes.Count, _edges.Count, cycles, issues);
    }

    public IReadOnlyList<string> FindCycles()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0=unseen 1=visiting 2=done
        var stack = new List<string>();
        var cycles = new List<string>();

        foreach (var id in _nodes.Keys)
            state[id] = 0;

        void Dfs(string u)
        {
            state[u] = 1;
            stack.Add(u);
            foreach (var v in _out[u])
            {
                if (!state.ContainsKey(v)) continue;
                if (state[v] == 0) Dfs(v);
                else if (state[v] == 1)
                {
                    var idx = stack.IndexOf(v);
                    var path = idx >= 0 ? string.Join(" -> ", stack.Skip(idx)) + " -> " + v : u + " -> " + v;
                    cycles.Add(path);
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[u] = 2;
        }

        foreach (var id in _nodes.Keys)
        {
            if (state[id] == 0) Dfs(id);
        }

        return cycles.Distinct().ToList();
    }

    public static GraphPack LoadFromDirectory(string directory)
    {
        var nodesPath = Path.Combine(directory, "nodes.json");
        var edgesPath = Path.Combine(directory, "edges.json");
        var metaPath = Path.Combine(directory, "meta.json");

        var nodes = JsonSerializer.Deserialize<List<KpNode>>(File.ReadAllText(nodesPath), JsonOptions) ?? new();
        var edges = JsonSerializer.Deserialize<List<KpEdge>>(File.ReadAllText(edgesPath), JsonOptions) ?? new();
        GraphPack? meta = null;
        if (File.Exists(metaPath))
            meta = JsonSerializer.Deserialize<GraphPack>(File.ReadAllText(metaPath), JsonOptions);

        return meta is null
            ? new GraphPack("accounting-v1", "会计学课程包", "ACCOUNTING", 1, DateTime.UtcNow.ToString("O"), nodes, edges)
            : meta with { Nodes = nodes, Edges = edges };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
