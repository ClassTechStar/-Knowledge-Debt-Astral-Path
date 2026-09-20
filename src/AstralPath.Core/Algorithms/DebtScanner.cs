using AstralPath.Core.Formula;

namespace AstralPath.Core.Algorithms;

public readonly record struct ImpactInput(
    double ScoreP,
    double ScoreC,
    int Freq,
    int DaysSinceLastError,
    double Weight);

public readonly record struct ImpactResult(bool Detected, double Impact, double Recency);

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

/// <summary>
/// BASELINE impact：freq × (50 − score_c) × recency × weight
/// 触发：score_p &lt; 40 ∧ score_c &lt; 50 ∧ freq &gt; 0
/// </summary>
public static class DebtScanner
{
    public static double Recency(int daysSinceLastError)
        => Math.Round(1.0 / (1.0 + daysSinceLastError / 7.0), 6, MidpointRounding.AwayFromZero);

    public static ImpactResult ComputeImpact(ImpactInput input)
    {
        var recency = Recency(input.DaysSinceLastError);
        var detected = input.ScoreP < FormulaWeights.DebtScorePMax
                       && input.ScoreC < FormulaWeights.DebtScoreCMax
                       && input.Freq > 0;
        if (!detected)
            return new ImpactResult(false, 0, recency);

        var impact = input.Freq * (50.0 - input.ScoreC) * recency * input.Weight;
        return new ImpactResult(true, Math.Round(impact, 6, MidpointRounding.AwayFromZero), recency);
    }

    public static IReadOnlyList<ScannedDebtEdge> Scan(
        IEnumerable<(string FromKp, string ToKp, string FromName, string ToName, double ScoreP, double ScoreC, int Freq, int Days, double Weight)> edges,
        int topN = FormulaWeights.DefaultTopN)
    {
        var results = new List<ScannedDebtEdge>();
        foreach (var e in edges)
        {
            var impact = ComputeImpact(new ImpactInput(e.ScoreP, e.ScoreC, e.Freq, e.Days, e.Weight));
            if (!impact.Detected) continue;
            results.Add(new ScannedDebtEdge(
                e.FromKp, e.ToKp, e.FromName, e.ToName,
                e.ScoreP, e.ScoreC, e.Freq, e.Days,
                impact.Recency, e.Weight, impact.Impact));
        }

        return results
            .OrderByDescending(x => x.Impact)
            .ThenBy(x => x.FromKp, StringComparer.Ordinal)
            .ThenBy(x => x.ToKp, StringComparer.Ordinal)
            .Take(topN)
            .ToList();
    }
}
