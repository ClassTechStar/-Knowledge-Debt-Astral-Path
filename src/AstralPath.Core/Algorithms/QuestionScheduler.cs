using AstralPath.Core.Formula;

namespace AstralPath.Core.Algorithms;

/// <summary>选题优先级：债边缺口 × 影响 × 时效 × 难度匹配（替代取模轮换）。</summary>
public sealed record QuestionCandidate(
    string QuestionId,
    string KpId,
    double Difficulty,   // 0–1
    double Score,        // 当前掌握 0–1
    double DebtImpact,
    double AgeDays);

public static class QuestionScheduler
{
    /// <summary>目标难度 = 能力 + 0.15（最近发展区）。</summary>
    public static double TargetDifficulty(double score)
        => MasteryCalculator.Clamp01(score + 0.15);

    /// <summary>难度匹配：|d - target| 越小越好，高斯衰减。</summary>
    public static double DifficultyFit(double questionDiff, double score)
    {
        var t = TargetDifficulty(score);
        var d = questionDiff - t;
        return Math.Exp(-4.0 * d * d);
    }

    /// <summary>优先级 = (1-score)^1.2 × (impact+ε) × 难度匹配 × (1+age/14)。</summary>
    public static double Priority(QuestionCandidate q)
    {
        var gap = Math.Pow(1.0 - MasteryCalculator.Clamp01(q.Score), 1.2);
        var impact = q.DebtImpact + 0.05;
        var fit = DifficultyFit(q.Difficulty, q.Score);
        var urgency = 1.0 + Math.Max(q.AgeDays, 0) / 14.0;
        return gap * impact * fit * urgency;
    }

    /// <summary>从未作答池中选优先级最高的一题。</summary>
    public static QuestionCandidate? Pick(IReadOnlyList<QuestionCandidate> pool)
    {
        if (pool.Count == 0) return null;
        return pool.OrderByDescending(Priority).First();
    }
}
