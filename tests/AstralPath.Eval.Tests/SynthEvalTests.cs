using AstralPath.Core.Algorithms;
using AstralPath.Graph;
using Xunit;

namespace AstralPath.Eval.Tests;

public class SynthEvalTests
{
    private static string PackDir =>
        Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "graph-packs", "accounting-v1"))
            ? Path.Combine(Directory.GetCurrentDirectory(), "graph-packs", "accounting-v1")
            : @"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\graph-packs\accounting-v1";

    [Fact]
    public void Synth30_GeneratorProducesHealthyAndDebtProfiles()
    {
        var pack = KnowledgeGraph.LoadFromDirectory(PackDir);
        var graph = new KnowledgeGraph(pack);
        var students = new List<(string Id, bool HasDebt, int DebtCount)>();

        for (var i = 0; i < 30; i++)
        {
            var hasDebt = i % 3 != 2; // 20/30 with debts
            var inputs = graph.Edges.Select(e =>
            {
                double scoreP, scoreC;
                int freq;
                if (hasDebt && e.From is "K02" or "K03" or "K05" && e.To is "K03" or "K05" or "K06")
                {
                    scoreP = 26 + (i % 5);
                    scoreC = 38 + (i % 7);
                    freq = 4 + (i % 4);
                }
                else if (!hasDebt)
                {
                    scoreP = 75; scoreC = 78; freq = 0;
                }
                else
                {
                    scoreP = 62; scoreC = 66; freq = 0;
                }

                return (e.From, e.To, e.From, e.To, scoreP, scoreC, freq, 0, e.Weight);
            }).ToList();

            var scanned = DebtScannerV1.Scan(inputs, 20);
            students.Add(($"synth-{i:D2}", hasDebt, scanned.Count));
        }

        var debtStudents = students.Where(s => s.HasDebt).ToList();
        var cleanStudents = students.Where(s => !s.HasDebt).ToList();
        Assert.Equal(20, debtStudents.Count);
        Assert.Equal(10, cleanStudents.Count);
        Assert.All(debtStudents, s => Assert.True(s.DebtCount >= 1));
        Assert.All(cleanStudents, s => Assert.Equal(0, s.DebtCount));
    }

    [Fact]
    public void Architecture_Guard_ScoreIsPureFunction()
    {
        var s1 = ScoreCalculator.ComputeScore(0.3, 0.8, 2);
        var s2 = ScoreCalculator.ComputeScore(0.3, 0.8, 2);
        Assert.Equal(s1, s2);
        Assert.Equal(28.0, s1, 6);
    }
}
