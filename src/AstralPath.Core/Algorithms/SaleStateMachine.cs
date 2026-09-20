using AstralPath.Core.Formula;

namespace AstralPath.Core.Algorithms;

public readonly record struct SaleProbe(double Acc, int Conf);

public sealed record SaleCheckResult(
    bool Saleable,
    int Streak,
    string? Need,
    IReadOnlyList<string> EvidenceAttemptIds);

/// <summary>
/// 销账状态机：cleared ⟺ 连续 2 次 quiz acc≥0.7 ∧ conf≥3。
/// 仅 progress-svc 可写 cleared（C8）。
/// </summary>
public static class SaleStateMachine
{
    public static SaleCheckResult IsSaleable(IReadOnlyList<SaleProbe> history, IReadOnlyList<string>? attemptIds = null)
    {
        var streak = 0;
        var evidence = new List<string>();
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var ok = history[i].Acc >= FormulaWeights.SaleAccThreshold
                     && history[i].Conf >= FormulaWeights.SaleConfThreshold;
            if (!ok) break;
            streak++;
            if (attemptIds != null && i < attemptIds.Count)
                evidence.Insert(0, attemptIds[i]);
        }

        if (streak >= FormulaWeights.SaleStreakRequired)
            return new SaleCheckResult(true, streak, null, evidence);

        var need = $"再完成 {FormulaWeights.SaleStreakRequired - streak} 次达标小测（acc≥0.7 且 conf≥3）";
        return new SaleCheckResult(false, streak, need, evidence);
    }

    public static string Transition(string current, string action)
    {
        return (current, action) switch
        {
            ("open", "plan_active") => "repairing",
            ("repairing", "sale_pass") => "cleared",
            ("repairing", "plan_abandoned") => "open",
            ("open", "admin_drop") => "dropped",
            ("cleared", _) => "cleared",
            _ => current
        };
    }

    public static bool CanWriteCleared(string service)
        => string.Equals(service, "progress-svc", StringComparison.OrdinalIgnoreCase);
}
