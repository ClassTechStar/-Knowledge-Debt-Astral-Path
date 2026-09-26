using AstralPath.Core.Algorithms;
using AstralPath.Core.Filtering;
using AstralPath.Core.Formatting;
using AstralPath.Core.Planner;
using AstralPath.Core.Formula;
using AstralPath.Core.Resources;
using Xunit;

namespace AstralPath.Mobile.Tests;

/// <summary>score-v2 / impact-v2 / sale-v2 金样与性质测试（容差 1e-6）。</summary>
public class GoldenScoreTests
{
    [Fact]
    public void GS01_Empirical_JeffreysPrior_IsHalfWhenNoData()
        => Assert.Equal(0.5, MasteryCalculator.Empirical(0, 0), 6);

    [Fact]
    public void GS02_Empirical_ShrinksTowardHalf()
    {
        var one = MasteryCalculator.Empirical(1, 1);   // 1.5/2 = 0.75
        var ten = MasteryCalculator.Empirical(10, 10); // 10.5/11
        Assert.Equal(0.75, one, 6);
        Assert.True(ten > one); // 同样全对，样本大更极端，但仍 <1
        Assert.True(ten < 1.0);
    }

    [Fact]
    public void GS03_Stability_GrowsWithStreakAndReps()
    {
        var a = MasteryCalculator.Stability(0, 1);
        var b = MasteryCalculator.Stability(3, 1);
        var c = MasteryCalculator.Stability(3, 12);
        Assert.True(a < b && b < c);
    }

    [Fact]
    public void GS04_Retention_DecaysWithAge()
    {
        var r0 = MasteryCalculator.Retention(0, 2, 8);
        var r10 = MasteryCalculator.Retention(10, 2, 8);
        var r30 = MasteryCalculator.Retention(30, 2, 8);
        Assert.Equal(1.0, r0, 6);
        Assert.True(r10 < r0);
        Assert.True(r30 < r10);
    }

    [Fact]
    public void GS05_PrereqSupport_UsesWeakestLink()
    {
        // mean=0.75, min=0.5, λ=0.65 → 0.35*0.75 + 0.65*0.5
        var expected = 0.35 * 0.75 + 0.65 * 0.5;
        Assert.Equal(expected, MasteryCalculator.PrereqSupport(new[] { 1.0, 0.5 }), 6);
    }

    [Fact]
    public void GS06_PrereqCeiling_CapsScore()
    {
        Assert.Equal(1.0, MasteryCalculator.PrereqCeiling(Array.Empty<double>()), 6);
        Assert.Equal(0.55 + 0.45 * 0.4, MasteryCalculator.PrereqCeiling(new[] { 0.4, 0.9 }), 6);
    }

    [Fact]
    public void GS07_Score_CappedByWeakestPrereq()
    {
        var strongSelf = MasteryCalculator.ComputeScore(
            new MasteryInput(20, 18, 5, 0, 4, new[] { 0.2, 0.9 }));
        Assert.InRange(strongSelf, 0.0, 0.55 + 0.45 * 0.2 + FormulaConstants.GoldenTolerance);
    }

    [Fact]
    public void GS08_Score_DomainUnitInterval()
    {
        var s = MasteryCalculator.ComputeScore(new MasteryInput(3, 0, 1, 400, -2, new[] { -1.0, 2.0 }));
        Assert.InRange(s, 0.0, 1.0);
    }

    [Fact]
    public void GS09_FormatScore_F6()
        => Assert.Equal("0.500000", NumberFormatter.FormatScore(0.5));

    [Fact]
    public void GS10_V1Compat_RawMapped()
    {
        var s = MasteryCalculator.ComputeScore(80, 0, 0, Array.Empty<double>());
        Assert.InRange(s, 0.6, 0.9); // 合理区间，不再是 v1 的硬 0.8
    }
}

public class GoldenImpactTests
{
    [Fact]
    public void GI01_CrossFactors()
    {
        Assert.Equal(1.0, DebtScanner.CrossFactor(EdgeType.Prerequisite), 6);
        Assert.Equal(1.15, DebtScanner.CrossFactor(EdgeType.TransferGap), 6);
    }

    [Fact]
    public void GI02_ReadinessGap_SmoothNoCliff()
    {
        // v1 在 0.60 两侧是 0/1 悬崖；v2 平滑
        Assert.True(DebtScanner.Readiness(0.55) > 0.45 && DebtScanner.Readiness(0.55) < 0.55);
        Assert.True(DebtScanner.Gap(0.50) > 0.45 && DebtScanner.Gap(0.50) < 0.55);
    }

    [Fact]
    public void GI03_Impact_MonotoneInGapAndCascade()
    {
        var low = DebtScanner.ComputeImpact(new DebtInput(0.8, 0.5, 1, EdgeType.Prerequisite, 3, 0));
        var highGap = DebtScanner.ComputeImpact(new DebtInput(0.8, 0.2, 1, EdgeType.Prerequisite, 3, 0));
        var highCascade = DebtScanner.ComputeImpact(new DebtInput(0.8, 0.5, 1, EdgeType.Prerequisite, 3, 20));
        var highFreq = DebtScanner.ComputeImpact(new DebtInput(0.8, 0.5, 1, EdgeType.Prerequisite, 30, 0));
        Assert.True(highGap > low);
        Assert.True(highCascade > low);
        Assert.True(highFreq > low);
    }

    [Fact]
    public void GI04_Impact_TransferGapBoost()
    {
        var p = DebtScanner.ComputeImpact(new DebtInput(0.8, 0.3, 1.2, EdgeType.Prerequisite, 5, 4));
        var t = DebtScanner.ComputeImpact(new DebtInput(0.8, 0.3, 1.2, EdgeType.TransferGap, 5, 4));
        Assert.Equal(p * 1.15, t, 6);
    }

    [Fact]
    public void GI05_NoFreq_ZeroImpact()
        => Assert.Equal(0.0, DebtScanner.ComputeImpact(new DebtInput(0.9, 0.1, 1, EdgeType.Prerequisite, 0)), 6);

    [Fact]
    public void GI06_WeakParent_NotBlamed()
    {
        // parent 自己都不会，不应记为「债」
        var impact = DebtScanner.ComputeImpact(new DebtInput(0.1, 0.1, 1, EdgeType.Prerequisite, 8));
        Assert.True(impact < FormulaConstants.ImpactHitThreshold);
    }
}

public class GoldenSaleTests
{
    [Fact]
    public void GC01_WeightedBar()
    {
        Assert.True(SaleStateMachine.MeetsBar(new SaleAttempt(true, 0.8, 4)));
        Assert.False(SaleStateMachine.MeetsBar(new SaleAttempt(true, 0.5, 5))); // acc 太低
        Assert.False(SaleStateMachine.MeetsBar(new SaleAttempt(true, 0.9, 2))); // conf 太低
    }

    [Fact]
    public void GC02_TwoBarsNotEnough_UntilMinAttempts()
    {
        var s = SaleStateMachine.Create();
        s = SaleStateMachine.Transition(s, new SaleAttempt(true, 0.9, 5));
        s = SaleStateMachine.Transition(s, new SaleAttempt(true, 0.9, 5));
        Assert.Equal(SaleStatus.Repairing, s.Status); // attempts=2 < 3
        s = SaleStateMachine.Transition(s, new SaleAttempt(true, 0.9, 5));
        Assert.Equal(SaleStatus.Cleared, s.Status);
    }

    [Fact]
    public void GC03_FailResetsStreak()
    {
        var s = SaleStateMachine.Create();
        s = SaleStateMachine.Transition(s, new SaleAttempt(true, 0.9, 5));
        s = SaleStateMachine.Transition(s, new SaleAttempt(false, 0.0, 1));
        Assert.Equal(0, s.Streak);
    }

    [Fact]
    public void GC04_ClearedTerminal()
    {
        var s = new SaleState(SaleStatus.Cleared, 2, 3);
        Assert.Equal(SaleStatus.Cleared, SaleStateMachine.Transition(s, new SaleAttempt(false, 0, 1)).Status);
    }
}

public class QuestionSchedulerTests
{
    [Fact]
    public void QS01_PrefersWeakHighDebtOverStrongLowDebt()
    {
        var weak = new QuestionCandidate("q1", "A", 0.3, 0.2, 0.5, 3);
        var strong = new QuestionCandidate("q2", "B", 0.3, 0.9, 0.1, 3);
        Assert.Equal("q1", QuestionScheduler.Pick(new[] { strong, weak })!.QuestionId);
    }

    [Fact]
    public void QS02_PrefersMatchingDifficulty()
    {
        var easy = new QuestionCandidate("e", "A", 0.1, 0.5, 0.4, 1);
        var fit = new QuestionCandidate("f", "A", 0.65, 0.5, 0.4, 1); // target≈0.65
        Assert.Equal("f", QuestionScheduler.Pick(new[] { easy, fit })!.QuestionId);
    }
}

public class ConstraintCheckerTests
{
    [Fact]
    public void GK01_K1_DayBudget()
    {
        var items = new PlanItemInput[]
        {
            new("1", "A", "A", "drill", 2, 25, "巩固「A」", Array.Empty<string>()),
            new("2", "B", "B", "drill", 2, 20, "巩固「B」", Array.Empty<string>())
        };
        var r = PlannerConstraintChecker.Check(new[] { new PlanDayInput(1, items) }, 40, Array.Empty<(string, string)>());
        Assert.Contains(r.Violations, v => v.Code == "K1");
    }

    [Fact]
    public void GK05_K5_Coverage()
    {
        var items = new[] { new PlanItemInput("1", "X", "X", "drill", 2, 10, "巩固「X」", Array.Empty<string>()) };
        var r = PlannerConstraintChecker.Check(new[] { new PlanDayInput(1, items) }, 40, new[] { ("A", "B") });
        Assert.Contains(r.Violations, v => v.Code == "K5");
    }

    [Fact]
    public void GK06_BuilderSatisfiesK1K4()
    {
        // 主版 Build 只产扁平条目；生成 + 自修复 + 按天分组的入口是 TryGeneratePlan
        var debts = new[]
        {
            new ScannedDebtInput("A", "B", 2.0, 10),
            new ScannedDebtInput("C", "D", 1.0, 10)
        };
        var ok = PlannerConstraintChecker.TryGeneratePlan(
            debts, new Dictionary<string, string>(), 40, 14, out var plan, out var check);
        Assert.True(ok, "生成器自修复后应通过 K1–K5");
        Assert.DoesNotContain(check.Violations, v => v.Code == "K1");
        Assert.DoesNotContain(check.Violations, v => v.Code == "K3");
        Assert.DoesNotContain(check.Violations, v => v.Code == "K4");
    }
}

public class GraphTopologyTests
{
    [Fact]
    public void GT01_TopoOrder()
    {
        var order = GraphTopology.TopologicalOrder(new[] { "A", "B", "C" }, new[] { ("A", "B"), ("B", "C") });
        Assert.Equal(new[] { "A", "B", "C" }, order);
    }

    [Fact]
    public void GT02_CycleThrows()
        => Assert.Throws<InvalidOperationException>(() =>
            GraphTopology.TopologicalOrder(new[] { "A", "B" }, new[] { ("A", "B"), ("B", "A") }));
}

public class GoldenWalkthroughTests
{
    [Fact]
    public void Walkthrough_DiagnosePlanPracticeSale()
    {
        // 1) 弱前置 + 弱目标 + 多错题 → 真债
        var input = new DebtInput(0.75, 0.25, 1.2, EdgeType.Prerequisite, 6, 12);
        var impact = DebtScanner.ComputeImpact(input);
        Assert.True(DebtScanner.IsHit(input));
        Assert.True(impact > FormulaConstants.ImpactHitThreshold);

        // 2) 计划覆盖并满足 K1/K3/K4（主版入口：TryGeneratePlan 生成 + 自修复）
        var planOk = PlannerConstraintChecker.TryGeneratePlan(
            new[] { new ScannedDebtInput("N1", "N2", impact, 10) },
            new Dictionary<string, string>(), 40, 14, out var plan, out var check);
        Assert.True(planOk);
        Assert.DoesNotContain(check.Violations, v => v.Code == "K1");

        // 3) 三次作答（两次达标）→ 销账
        var sale = SaleStateMachine.Create();
        sale = SaleStateMachine.Transition(sale, new SaleAttempt(true, 0.8, 4));
        sale = SaleStateMachine.Transition(sale, new SaleAttempt(true, 0.9, 5));
        Assert.Equal(SaleStatus.Repairing, sale.Status);
        sale = SaleStateMachine.Transition(sale, new SaleAttempt(true, 0.7, 3));
        Assert.Equal(SaleStatus.Cleared, sale.Status);

        // 4) 掌握度随正确率上升
        var low = MasteryCalculator.ComputeScore(new MasteryInput(10, 2, 2, 0, 0, new[] { 0.5 }));
        var high = MasteryCalculator.ComputeScore(new MasteryInput(10, 9, 5, 0, 3, new[] { 0.5 }));
        Assert.True(high > low);
    }
}

public class CrossClientConsistencyTests
{
    [Fact]
    public void CC01_FormulaVersions_Bumped()
    {
        Assert.Equal("score-v2", FormulaConstants.ScoreVersion);
        Assert.Equal("impact-v2", FormulaConstants.ImpactVersion);
    }

    [Fact]
    public void CC02_NumberFormatter_F6()
        => Assert.Equal("0.123457", NumberFormatter.FormatScore(0.1234567));

    [Fact]
    public void CC03_SaleStatus_Frozen()
        => Assert.Equal(3, Enum.GetValues<SaleStatus>().Length);

    [Fact]
    public void CC04_ErrorCatalog_Intact()
    {
        foreach (var code in ErrorCatalog.Codes)
            Assert.True(ErrorCatalog.TryGetMessage(code, out _), code);
    }
}
