using AstralPath.Core.Algorithms;
using AstralPath.Core.Formula;
using AstralPath.Core.Narrative;
using AstralPath.Core.Planner;
using AstralPath.Graph;

namespace AstralPath.Desktop.Demo;

/// <summary>
/// 客户端复算自检（方案 §12：「手算 = API = 端上显示」，容差 1e-6）。
///
/// 直接引用 <c>AstralPath.Core</c> 纯函数，验证端上公式与金样一致。
/// 用法：<c>AstralPath.Desktop --demo [图包目录]</c>，成功返回 0，失败返回 1。
/// </summary>
internal static class DemoRunner
{
    public static int Run(string[] args)
    {
        var packDir = args.FirstOrDefault(a => !a.StartsWith('-')) ?? FindPack();
        var pack = KnowledgeGraph.LoadFromDirectory(packDir);
        var graph = new KnowledgeGraph(pack);
        var validate = graph.Validate();

        Console.WriteLine("=== 知债：星穹学途 · 客户端复算演示 ===");
        Console.WriteLine($"图包 {pack.PackId} v{pack.GraphVersion}");
        Console.WriteLine($"节点 {validate.NodeCount} · 边 {validate.EdgeCount} · 无环 {validate.Ok}");
        Console.WriteLine();

        var mastery = new Dictionary<string, ScoreInput>
        {
            ["K02"] = new(0.28, 0.80, 2),
            ["K03"] = new(0.30, 0.80, 2),
            ["K05"] = new(0.35, 0.70, 2),
            ["K06"] = new(0.40, 0.60, 3)
        };

        Console.WriteLine("掌握度（BASELINE score）:");
        foreach (var (kp, input) in mastery)
        {
            var r = ScoreCalculator.Compute(input);
            var name = graph.TryGetNode(kp, out var n) ? n.Name : kp;
            Console.WriteLine($"  {kp} {name}: score={r.Score} band={r.Band}");
        }

        Console.WriteLine();
        Console.WriteLine("债边 impact（端上复算）:");
        var edges = graph.Edges
            .Where(e => mastery.ContainsKey(e.From) && mastery.ContainsKey(e.To))
            .Select(e =>
            {
                var sp = ScoreCalculator.Compute(mastery[e.From]).Score;
                var sc = ScoreCalculator.Compute(mastery[e.To]).Score;
                const int freq = 6;
                var impact = DebtScanner.ComputeImpact(new ImpactInput(sp, sc, freq, 0, e.Weight));
                return (e, sp, sc, freq, impact);
            })
            .Where(x => x.impact.Detected)
            .OrderByDescending(x => x.impact.Impact)
            .ToList();

        foreach (var (e, sp, sc, freq, impact) in edges.Take(5))
        {
            var fn = graph.TryGetNode(e.From, out var a) ? a.Name : e.From;
            var tn = graph.TryGetNode(e.To, out var b) ? b.Name : e.To;
            Console.WriteLine($"  {fn} → {tn}: score {sp}→{sc} freq={freq} impact={impact.Impact}");
            var narrative = NarrativeGuard.Build(new NarrativeSlots(fn, tn, impact.Impact, sp, sc));
            Console.WriteLine($"    叙事: {narrative.Story}");
            Console.WriteLine($"    安全: banned={narrative.BannedHit} slotFailed={narrative.SlotFailed} degraded={narrative.DegradedTemplate}");
        }

        Console.WriteLine();
        Console.WriteLine("金样手算对照:");
        var goldenScore = ScoreCalculator.ComputeScore(0.30, 0.80, 2);
        var goldenImpact = DebtScanner.ComputeImpact(new ImpactInput(28, 41, 6, 0, 1.6));
        Console.WriteLine($"  G-SCORE-1 expect 28.0 actual {goldenScore} match={Math.Abs(goldenScore - 28) < FormulaWeights.Tolerance}");
        Console.WriteLine($"  G-DEBT-1  expect 86.4 actual {goldenImpact.Impact} match={Math.Abs(goldenImpact.Impact - 86.4) < FormulaWeights.Tolerance}");

        Console.WriteLine();
        Console.WriteLine("销账状态机:");
        var sale = SaleStateMachine.IsSaleable(new[] { new SaleProbe(0.8, 4), new SaleProbe(0.75, 3) });
        Console.WriteLine($"  连续两次达标 → saleable={sale.Saleable} streak={sale.Streak}");

        Console.WriteLine();
        Console.WriteLine("K 约束检查:");
        var planCheck = PlannerConstraintChecker.Check(
            new[]
            {
                new PlanDayInput(1, new[]
                {
                    new PlanItemInput("i1", "K03", "借贷记账法", "concept", 2, 8,
                        "为还 会计等式 → 借贷记账法 的债", new[] { "K02", "K03" }, "会计等式")
                })
            },
            35,
            new[] { ("K02", "K03") });
        Console.WriteLine($"  K1–K5 checked={planCheck.ConstraintsChecked} violations={planCheck.Violations.Count}");

        Console.WriteLine();
        Console.WriteLine("本系统仅用于教学辅助与学习规划，不构成处分依据。score/impact/K/销账由确定性公式计算。");
        Console.WriteLine("客户端复算演示完成。");

        return validate.Ok
               && Math.Abs(goldenScore - 28) < FormulaWeights.Tolerance
               && Math.Abs(goldenImpact.Impact - 86.4) < FormulaWeights.Tolerance
            ? 0
            : 1;
    }

    private static string FindPack()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "graph-packs", "accounting-v1"),
            Path.Combine(Directory.GetCurrentDirectory(), "graph-packs", "accounting-v1")
        };

        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(Path.Combine(full, "nodes.json"))) return full;
        }

        throw new DirectoryNotFoundException("graph-packs/accounting-v1 未找到");
    }
}
