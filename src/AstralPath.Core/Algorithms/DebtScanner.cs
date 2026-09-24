using AstralPath.Core.Formula;

namespace AstralPath.Core.Algorithms;

public enum EdgeType
{
    Prerequisite,
    TransferGap
}

/// <summary>债边上下文：替代 v1 的二值命中。</summary>
public sealed record DebtInput(
    double ScoreFrom,
    double ScoreTo,
    double Weight,
    EdgeType EdgeType,
    int Freq,
    int DownstreamCount = 0);

/// <summary>
/// impact-v2：平滑门限 × 最弱缺口 × 级联 × 错题体量。
/// 消除 v1 的 score≥0.60/≤0.45 悬崖，加入下游阻塞放大。
/// </summary>
public static class DebtScanner
{
    public static double CrossFactor(EdgeType edgeType)
        => edgeType == EdgeType.TransferGap
            ? FormulaConstants.TransferGapMultiplier
            : 1.0;

    /// <summary>parent「足够强才可归责」：Sigmoid((sp - 0.55)×8)。</summary>
    public static double Readiness(double scoreFrom)
        => MasteryCalculator.Sigmoid((scoreFrom - FormulaConstants.ParentScoreGate) * FormulaConstants.GateSteepness);

    /// <summary>child 缺口：Sigmoid((0.50 - sc)×8)。</summary>
    public static double Gap(double scoreTo)
        => MasteryCalculator.Sigmoid((FormulaConstants.ChildScoreGate - scoreTo) * FormulaConstants.GateSteepness);

    /// <summary>级联放大：下游被堵住的知识点越多，债越致命。</summary>
    public static double Cascade(int downstreamCount)
        => 1.0 + FormulaConstants.CascadeAlpha * Math.Log(1.0 + Math.Max(downstreamCount, 0));

    /// <summary>错题体量：反复掉坑比偶发失误更重。</summary>
    public static double Volume(int freq)
        => 1.0 + FormulaConstants.VolumeBeta * Math.Log(1.0 + Math.Max(freq, 0));

    public static double ComputeImpact(DebtInput input)
    {
        if (input.Freq <= 0) return 0.0;
        var impact = FormulaConstants.ImpactBase
                     * Readiness(input.ScoreFrom)
                     * Gap(input.ScoreTo)
                     * Math.Max(input.Weight, 0)
                     * CrossFactor(input.EdgeType)
                     * Cascade(input.DownstreamCount)
                     * Volume(input.Freq);
        return impact;
    }

    /// <summary>红边：平滑分 + 体量阈值，替代硬门限 IsHit。</summary>
    public static bool IsHit(DebtInput input)
        => input.Freq > 0 && ComputeImpact(input) >= FormulaConstants.ImpactHitThreshold;

    // ── v1 兼容 ─────────────────────────────────────────
    public static bool IsHit(double scoreFrom, double scoreTo, int freq)
        => IsHit(new DebtInput(scoreFrom, scoreTo, 1.0, EdgeType.Prerequisite, freq));

    public static double ComputeImpact(double scoreFrom, double scoreTo, double weight, EdgeType edgeType, int freq)
        => ComputeImpact(new DebtInput(scoreFrom, scoreTo, weight, edgeType, freq));
}

/// <summary>v1 兼容输入（BASELINE impact）。</summary>
public readonly record struct ImpactInput(
    double ScoreP,
    double ScoreC,
    int Freq,
    int DaysSinceLastError,
    double Weight);

public readonly record struct ImpactResult(bool Detected, double Impact, double Recency);

public static class DebtScannerCompat
{
    public static ImpactResult ComputeImpactV1(ImpactInput input)
    {
        var rec = 1.0 / (1.0 + input.DaysSinceLastError / 7.0); // v1 金样口径
        // v1 BASELINE 门限（score 为 0–100）：sp<40 ∧ sc<50 ∧ freq>0
        var detected = input.ScoreP < 40.0 && input.ScoreC < 50.0 && input.Freq > 0;
        var impact = detected ? input.Freq * (50.0 - input.ScoreC) * rec * input.Weight : 0.0;
        return new ImpactResult(detected, impact, rec);
    }

    public static IReadOnlyList<(string FromKp, string ToKp, double Impact)> Scan(
        IEnumerable<(string FromKp, string ToKp, string FromName, string ToName,
            double ScoreP, double ScoreC, int Freq, int Days, double Weight)> edges,
        int topN = 5)
    {
        var list = new List<(string, string, double)>();
        foreach (var e in edges)
        {
            var r = ComputeImpactV1(new ImpactInput(e.ScoreP, e.ScoreC, e.Freq, e.Days, e.Weight));
            if (r.Detected) list.Add((e.FromKp, e.ToKp, r.Impact));
        }
        return list.OrderByDescending(x => x.Item3).Take(topN).ToList();
    }
}

/// <summary>v1 扫描结果（AstralPathStore 消费）。</summary>
public sealed record ScannedDebtEdge(
    string FromKp,
    string ToKp,
    string FromKpName,
    string ToKpName,
    double ScoreFrom,
    double ScoreTo,
    int Freq,
    int DaysSinceLastError,
    double Recency,
    double Weight,
    double Impact,
    string Status = "open");

public static partial class DebtScannerV1
{
    public static IReadOnlyList<ScannedDebtEdge> Scan(
        IEnumerable<(string FromKp, string ToKp, string FromName, string ToName,
            double ScoreP, double ScoreC, int Freq, int Days, double Weight)> edges,
        int topN = 5)
    {
        var list = new List<ScannedDebtEdge>();
        foreach (var e in edges)
        {
            var r = DebtScannerCompat.ComputeImpactV1(new ImpactInput(e.ScoreP, e.ScoreC, e.Freq, e.Days, e.Weight));
            if (!r.Detected) continue;
            list.Add(new ScannedDebtEdge(e.FromKp, e.ToKp, e.FromName, e.ToName,
                e.ScoreP, e.ScoreC, e.Freq, e.Days, r.Recency, e.Weight, r.Impact));
        }
        return list.OrderByDescending(x => x.Impact).Take(topN).ToList();
    }
}
