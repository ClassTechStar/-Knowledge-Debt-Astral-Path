using AstralPath.Core.Formula;

namespace AstralPath.Core.Algorithms;

public readonly record struct ScoreInput(double RecentAcc, double Sev, double SelfConf)
{
    public double SevNorm => 1.0 - Math.Min(Sev, 1.0);
}

public readonly record struct ScoreResult(double Score, double SevNorm)
{
    public string Band => Score switch
    {
        >= 70 => "green",
        >= 40 => "yellow",
        _ => "red"
    };
}

/// <summary>
/// BASELINE score：100 × (0.6×recent_acc + 0.3×sev_norm + 0.1×(self_conf/5))
/// 禁止 LLM 改分；金样容差 1e-6。
/// </summary>
public static class ScoreCalculator
{
    public static ScoreResult Compute(ScoreInput input)
    {
        var sevNorm = input.SevNorm;
        var raw = 100.0 * (
            FormulaWeights.ScoreWAcc * input.RecentAcc +
            FormulaWeights.ScoreWSev * sevNorm +
            FormulaWeights.ScoreWConf * (input.SelfConf / 5.0));
        return new ScoreResult(Math.Round(raw, 6, MidpointRounding.AwayFromZero), Math.Round(sevNorm, 6, MidpointRounding.AwayFromZero));
    }

    public static double ComputeScore(double recentAcc, double sev, double selfConf)
        => Compute(new ScoreInput(recentAcc, sev, selfConf)).Score;

    public static string BandOf(double score) => score switch
    {
        >= 70 => "green",
        >= 40 => "yellow",
        _ => "red"
    };
}
