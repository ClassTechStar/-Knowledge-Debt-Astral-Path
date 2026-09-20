using AstralPath.Core.Algorithms;
using AstralPath.Graph;
using Xunit;

namespace AstralPath.Core.Tests;

public class GraphAndRecallTests
{
    private static string PackDir
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "graph-packs", "accounting-v1"),
                Path.Combine(Directory.GetCurrentDirectory(), "graph-packs", "accounting-v1"),
                @"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\graph-packs\accounting-v1"
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(Path.Combine(full, "nodes.json"))) return full;
            }
            throw new DirectoryNotFoundException("graph-packs/accounting-v1 not found");
        }
    }

    [Fact]
    public void AccountingPack_IsAcyclic_AndMeetsMinimums()
    {
        var pack = KnowledgeGraph.LoadFromDirectory(PackDir);
        var graph = new KnowledgeGraph(pack);
        var result = graph.Validate();
        Assert.True(result.Cycles.Count == 0, string.Join(";", result.Cycles));
        Assert.True(result.NodeCount >= 30, $"nodes={result.NodeCount}");
        Assert.True(result.EdgeCount >= 40, $"edges={result.EdgeCount}");
    }

    [Fact]
    public void Recall_OnSeededDebts_IsAtLeast080()
    {
        // 20 ground-truth debt edges for accounting pack Student A profile
        var groundTruth = new List<(string From, string To)>
        {
            ("K02", "K03"), ("K03", "K05"), ("K05", "K06"), ("K02", "K05"),
            ("K03", "K06"), ("K33", "K30"), ("K12", "K06"), ("K17", "K18"),
            ("K03", "K13"), ("K03", "K14"), ("K05", "K08"), ("K05", "K15"),
            ("K05", "K16"), ("K19", "K20"), ("K27", "K29"), ("K28", "K29"),
            ("K02", "K30"), ("K29", "K31"), ("K05", "K19"), ("K04", "K05")
        };

        // Simulate Student A scores on pack
        var pack = KnowledgeGraph.LoadFromDirectory(PackDir);
        var graph = new KnowledgeGraph(pack);
        var scores = new Dictionary<string, (double ScoreP, double ScoreC, int Freq, int Days, double Weight)>(StringComparer.Ordinal);

        var gtSet = groundTruth.ToHashSet();
        var inputs = new List<(string, string, string, string, double, double, int, int, double)>();
        foreach (var edge in graph.Edges)
        {
            var isGt = gtSet.Contains((edge.From, edge.To));
            // ground-truth debt edges are pre-seeded so BASELINE gate is satisfied
            var scoreP = isGt ? 28.0 : 72.0;
            var scoreC = isGt ? 41.0 : 68.0;
            var freq = isGt ? 5 : 0;
            if (!isGt && edge.From is "K02" or "K03" or "K05" && edge.To is "K03" or "K05" or "K06")
            {
                scoreP = 30.0;
                scoreC = 45.0;
                freq = 2; // distractors that may lower precision but should not kill recall
            }
            inputs.Add((
                edge.From, edge.To,
                graph.TryGetNode(edge.From, out var fn) ? fn.Name : edge.From,
                graph.TryGetNode(edge.To, out var tn) ? tn.Name : edge.To,
                scoreP, scoreC, freq, 0, edge.Weight));
        }

        // evaluate recall on full scan (no aggressive topN cut for the metric)
        var scanned = DebtScanner.Scan(inputs, topN: 50);
        var predicted = scanned.Select(s => (s.FromKp, s.ToKp)).ToHashSet();
        var hits = groundTruth.Count(gt => predicted.Contains(gt));
        var recall = hits / (double)groundTruth.Count;

        Assert.True(recall >= 0.80, $"recall={recall:0.000} hits={hits}/{groundTruth.Count} predicted={predicted.Count}");
    }

    [Fact]
    public void DualStudent_DemoExpectations()
    {
        // A has debts, B should not under healthy scores
        var pack = KnowledgeGraph.LoadFromDirectory(PackDir);
        var graph = new KnowledgeGraph(pack);

        var inputsA = graph.Edges.Select(e => (
            e.From, e.To, e.From, e.To,
            e.From is "K02" or "K03" ? 28.0 : 70.0,
            e.To is "K05" or "K06" ? 41.0 : 70.0,
            e.To is "K05" or "K06" ? 6 : 0,
            0, e.Weight)).ToList();

        var inputsB = graph.Edges.Select(e => (
            e.From, e.To, e.From, e.To, 85.0, 80.0, 0, 0, e.Weight)).ToList();

        var scanA = DebtScanner.Scan(inputsA);
        var scanB = DebtScanner.Scan(inputsB);
        Assert.True(scanA.Count >= 3, $"A debts={scanA.Count}");
        Assert.Empty(scanB);
    }
}
