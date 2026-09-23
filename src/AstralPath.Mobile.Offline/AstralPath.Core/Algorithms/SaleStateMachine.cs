using AstralPath.Core.Formula;

namespace AstralPath.Core.Algorithms;

/// <summary>销账状态。禁止其它模块直接写 cleared。</summary>
public enum SaleStatus
{
    Open,
    Repairing,
    Cleared
}

public sealed record SaleAttempt(bool Correct, double Accuracy, int SelfConf);

public sealed record SaleState(SaleStatus Status, int Streak, int Attempts, int WeightedScorePct = 0);

/// <summary>
/// sale-v2：加权达标（0.7×acc + 0.3×conf/5 ≥ 0.65），
/// 且窗口内至少 3 次作答、近期连续 2 次达标才 cleared。
/// 两题就清账的 v1 行为已废弃。
/// </summary>
public static class SaleStateMachine
{
    public static SaleState Create() => new(SaleStatus.Open, 0, 0, 0);

    public static double Weighted(SaleAttempt attempt)
        => 0.7 * Math.Clamp(attempt.Accuracy, 0, 1)
           + 0.3 * Math.Clamp(attempt.SelfConf / FormulaConstants.ConfScale, 0, 1);

    public static bool MeetsBar(SaleAttempt attempt)
        => Weighted(attempt) >= FormulaConstants.SaleBar
           && attempt.Accuracy >= FormulaConstants.SaleAccMin
           && attempt.SelfConf >= FormulaConstants.SaleConfMin;

    public static SaleState Transition(SaleState state, SaleAttempt attempt)
    {
        if (state.Status == SaleStatus.Cleared) return state;

        var attempts = state.Attempts + 1;
        var streak = MeetsBar(attempt) ? state.Streak + 1 : 0;
        var pct = (int)Math.Round(Weighted(attempt) * 100);
        var status = state.Status;

        if (streak >= FormulaConstants.SaleStreakRequired
            && attempts >= FormulaConstants.SaleMinAttempts)
        {
            status = SaleStatus.Cleared;
        }
        else if (streak > 0 || attempts > 0)
        {
            status = SaleStatus.Repairing;
        }

        return new SaleState(status, streak, attempts, pct);
    }
}
