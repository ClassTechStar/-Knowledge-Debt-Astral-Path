namespace AstralPath.Core.Graph;

/// <summary>带权知识图（DAG）。from → to 表示 to 依赖 from（先修）。</summary>
public sealed class WeightedKg
{
    public List<KgNode> Nodes { get; } = new();
    public List<KgEdge> Edges { get; } = new();

    public sealed record KgNode(string Id, string Title, string Kind, double Importance = 0);

    public sealed record KgEdge(
        string From,
        string To,
        string EdgeType,
        double Weight,
        double Confidence = 1.0);

    public Dictionary<string, KgNode> NodeMap()
        => Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

    public void AddNode(string id, string title, string kind = "term", double importance = 0)
    {
        if (Nodes.Any(n => n.Id == id)) return;
        Nodes.Add(new KgNode(id, title, kind, importance));
    }

    public void AddEdge(string from, string to, string type = "prerequisite", double weight = 1, double confidence = 1)
    {
        if (from == to) return;
        if (Nodes.All(n => n.Id != from) || Nodes.All(n => n.Id != to)) return;
        if (Edges.Any(e => e.From == from && e.To == to)) return;
        Edges.Add(new KgEdge(from, to, type, Math.Max(weight, 0.01), Math.Max(confidence, 0.01)));
    }

    public IReadOnlyList<(string From, string To)> Pairs()
        => Edges.Select(e => (e.From, e.To)).ToList();
}
