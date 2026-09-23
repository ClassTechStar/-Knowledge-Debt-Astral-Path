using AstralPath.Core.Formula;

namespace AstralPath.Core.Algorithms;

/// <summary>掌握度输入：替代 v1 的 raw/age/streak 三元组。</summary>
public sealed record MasteryInput(
    int Attempts,
    int Successes,
    double MeanSelfConf,
    double AgeDays,
    int Streak,
    IReadOnlyList<double> PrereqScores);

/// <summary>
/// score-v2：经验（Beta 收缩）× 遗忘保持 × 先修支撑，再受最弱先修封顶。
/// score ∈ [0,1]，端上纯函数。
/// </summary>
public static class MasteryCalculator
{
    public static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    /// <summary>Sigmoid 用于平滑门限，避免 v1 的悬崖。</summary>
    public static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    /// <summary>Beta 后验均值（Jeffreys 先验），小样本自动向 0.5 收缩。</summary>
    public static double Empirical(int attempts, int successes)
    {
        var s = Math.Clamp(successes, 0, Math.Max(attempts, 0));
        var f = Math.Max(attempts, 0) - s;
        return (s + FormulaConstants.PriorSuccess)
               / (s + f + FormulaConstants.PriorSuccess + FormulaConstants.PriorFail);
    }

    /// <summary>
    /// 记忆稳定性（天）：连对与复习次数都会拉长保持。
    /// S = S0 × (1 + α×streak) × (1 + β×ln(1+attempts))
    /// </summary>
    public static double Stability(int streak, int attempts)
    {
        var st = Math.Max(streak, 0);
        var n = Math.Max(attempts, 1);
        return FormulaConstants.Stability0
               * (1.0 + FormulaConstants.StabilityAlpha * st)
               * (1.0 + FormulaConstants.StabilityBeta * Math.Log(1.0 + n));
    }

    /// <summary>Ebbinghaus 保持率 = exp(-age / S)，S 越大遗忘越慢。</summary>
    public static double Retention(double ageDays, int streak, int attempts)
    {
        var s = Stability(streak, attempts);
        var age = Math.Max(ageDays, 0);
        return Math.Exp(-age / s);
    }

    /// <summary>先修支撑：(1-λ)·mean + λ·min，既看整体也卡最弱链。</summary>
    public static double PrereqSupport(IReadOnlyList<double> prereqScores)
    {
        if (prereqScores is not { Count: > 0 }) return 0.5; // 无先修：中性
        var mean = prereqScores.Average();
        var min = prereqScores.Min();
        var λ = FormulaConstants.PrereqWeakMix;
        return (1 - λ) * mean + λ * min;
    }

    /// <summary>先修封顶：score ≤ Floor + (1-Floor)×min(prereq)。</summary>
    public static double PrereqCeiling(IReadOnlyList<double> prereqScores)
    {
        if (prereqScores is not { Count: > 0 }) return 1.0;
        return FormulaConstants.PrereqFloor + (1.0 - FormulaConstants.PrereqFloor) * prereqScores.Min();
    }

    public static double ComputeScore(MasteryInput input)
    {
        var emp = Empirical(input.Attempts, input.Successes);
        // 信心只作弱加成：低信心不抬高、高信心微升
        var confNorm = Math.Clamp(input.MeanSelfConf / FormulaConstants.ConfScale, 0, 1);
        var know = 0.85 * emp + 0.15 * confNorm;

        var ret = Retention(input.AgeDays, input.Streak, input.Attempts);
        var pre = PrereqSupport(input.PrereqScores);

        var raw = FormulaConstants.WEmpirical * know
                  + FormulaConstants.WRetention * ret
                  + FormulaConstants.WPrereq * pre;

        return Clamp01(Math.Min(raw, PrereqCeiling(input.PrereqScores)));
    }

    // ── v1 兼容入口（迁移期调用）────────────────────────
    /// <summary>v1 签名：raw/100 作经验代理，streak 作稳定性代理。</summary>
    public static double ComputeScore(double raw, double ageDays, int streak, IReadOnlyList<double> prereqScores)
    {
        var p = Math.Clamp(raw / 100.0, 0, 1);
        // 将 raw 逆映射为伪计数：10 次尝试的等价证据
        var attempts = 10;
        var successes = (int)Math.Round(p * attempts);
        var conf = 1 + 4 * p; // 与 raw 同向
        return ComputeScore(new MasteryInput(attempts, successes, conf, ageDays, streak, prereqScores));
    }
}
