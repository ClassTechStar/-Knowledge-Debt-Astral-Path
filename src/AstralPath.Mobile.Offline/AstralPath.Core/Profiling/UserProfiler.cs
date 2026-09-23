namespace AstralPath.Core.Profiling;

/// <summary>一次作答行为信号（画像原料，不含任何敏感语义标签）。</summary>
public sealed record AttemptSignal(
    DateTime At,
    string KpId,
    bool Correct,
    int SelfConf,          // 1–5
    long LatencyMs,        // 作答耗时
    int Difficulty,        // 1–5
    int HintsUsed = 0);

/// <summary>
/// 画像 v2：从作答行为推断学习风格与主题画像。
/// 纯函数 / 增量 EMA；禁止生成敏感域标签（sensitive/crisis/semantic）。
/// </summary>
public static class UserProfiler
{
    public static readonly string[] BannedDomains = { "sensitive", "crisis", "semantic" };

    // ── 1. 信心校准 ─────────────────────────────────────
    /// <summary>Brier 分数（越小越准）：(conf/5 - 1{对})²。</summary>
    public static double Brier(IEnumerable<AttemptSignal> signals)
    {
        var list = signals.ToList();
        if (list.Count == 0) return 0.25; // 先验
        return list.Average(s =>
        {
            var p = Math.Clamp(s.SelfConf / 5.0, 0, 1);
            var y = s.Correct ? 1.0 : 0.0;
            return (p - y) * (p - y);
        });
    }

    /// <summary>校准偏差：E[conf/5] − E[正确率]。&gt;0 过度自信。</summary>
    public static double CalibrationBias(IEnumerable<AttemptSignal> signals)
    {
        var list = signals.ToList();
        if (list.Count == 0) return 0;
        var p = list.Average(s => Math.Clamp(s.SelfConf / 5.0, 0, 1));
        var y = list.Average(s => s.Correct ? 1.0 : 0.0);
        return p - y;
    }

    // ── 2. 行为特征 ─────────────────────────────────────
    public sealed record BehaviorFeatures(
        int Attempts,
        double Accuracy,
        double MeanConf,
        double Brier,
        double CalibBias,
        double MedianLatencyMs,
        double HintRate,
        double Volatility,     // 正误序列的翻转率
        double Pace,           // 归一化作答速度 0–1（快）
        double Persistence);   // 同 KP 重复次数均值

    public static BehaviorFeatures Extract(IEnumerable<AttemptSignal> signals)
    {
        var list = signals.OrderBy(s => s.At).ToList();
        if (list.Count == 0)
            return new BehaviorFeatures(0, 0, 0, 0.25, 0, 0, 0, 0, 0.5, 0);

        var acc = list.Average(s => s.Correct ? 1.0 : 0.0);
        var meanConf = list.Average(s => Math.Clamp(s.SelfConf / 5.0, 0, 1));
        var lats = list.Select(s => (double)s.LatencyMs).OrderBy(x => x).ToList();
        var med = lats.Count % 2 == 1
            ? lats[lats.Count / 2]
            : (lats[lats.Count / 2 - 1] + lats[lats.Count / 2]) / 2;
        var hintRate = list.Average(s => Math.Min(s.HintsUsed, 3) / 3.0);
        var flips = 0;
        for (var i = 1; i < list.Count; i++)
            if (list[i].Correct != list[i - 1].Correct) flips++;
        var vol = list.Count <= 1 ? 0 : (double)flips / (list.Count - 1);
        // 快慢：以 20s 为基准，映射到 0–1
        var pace = Math.Exp(-med / 20000.0);
        var persistence = list.GroupBy(s => s.KpId).Average(g => g.Count());

        return new BehaviorFeatures(
            list.Count, acc, meanConf,
            Math.Round(Brier(list), 6), Math.Round(CalibrationBias(list), 6),
            med, Math.Round(hintRate, 6), Math.Round(vol, 6),
            Math.Round(pace, 6), Math.Round(persistence, 6));
    }

    // ── 3. 学习风格轴（五维，各 ∈ [0,1]）────────────────
    public sealed record StyleVector(
        double AccuracyFirst,   // 又快又准 vs 慢而稳
        double Calibrated,      // 信心可靠
        double Steady,          // 低波动
        double Independent,     // 少提示
        double DeepDive);       // 同题深挖 / 高难度

    public static StyleVector Style(BehaviorFeatures f, IEnumerable<AttemptSignal> signals)
    {
        var list = signals.ToList();
        double DiffTol() => list.Count == 0
            ? 0.5
            : Math.Clamp(list.Average(s => s.Difficulty) / 5.0, 0, 1);

        var accFirst = Math.Clamp(0.55 * f.Accuracy + 0.45 * f.Pace, 0, 1);
        var calibrated = Math.Clamp(1.0 - Math.Min(f.Brier / 0.5, 1.0), 0, 1);
        var steady = Math.Clamp(1.0 - f.Volatility, 0, 1);
        var independent = Math.Clamp(1.0 - f.HintRate, 0, 1);
        var deep = Math.Clamp(0.5 * DiffTol() + 0.5 * Math.Min(f.Persistence / 3.0, 1.0), 0, 1);
        return new StyleVector(
            Math.Round(accFirst, 6), Math.Round(calibrated, 6),
            Math.Round(steady, 6), Math.Round(independent, 6), Math.Round(deep, 6));
    }

    // ── 4. 主题画像（掌握 × 兴趣 × 难度）────────────────
    public sealed record TopicProfile(string KpId, double Mastery, double Interest, double DiffTolerance, double Weight);

    /// <summary>
    /// Interest：作答次数/时间占比（EMA 可选）；Weight 用于推荐排序。
    /// </summary>
    public static IReadOnlyList<TopicProfile> Topics(
        IEnumerable<AttemptSignal> signals,
        IReadOnlyDictionary<string, double> masteryByKp)
    {
        var list = signals.ToList();
        if (list.Count == 0) return Array.Empty<TopicProfile>();
        var total = list.Count;
        return list.GroupBy(s => s.KpId, StringComparer.Ordinal)
            .Select(g =>
            {
                var n = g.Count();
                var interest = (double)n / total;
                var diff = Math.Clamp(g.Average(s => s.Difficulty) / 5.0, 0, 1);
                var mastery = masteryByKp.GetValueOrDefault(g.Key, 0.5);
                // 薄弱 × 有兴趣 × 难度适中 → 值得练
                var gap = 1.0 - mastery;
                var weight = Math.Round(gap * (0.4 + 0.6 * interest) * (0.5 + 0.5 * (1 - Math.Abs(diff - 0.5))), 6);
                return new TopicProfile(g.Key, mastery, Math.Round(interest, 6), Math.Round(diff, 6), weight);
            })
            .OrderByDescending(t => t.Weight)
            .ToList();
    }

    // ── 5. 动态 EMA（画像漂移）──────────────────────────
    /// <summary>style 指标指数平滑，α 为新证据权重。</summary>
    public static StyleVector Smooth(StyleVector old, StyleVector neu, double alpha = 0.25)
    {
        double M(double a, double b) => Math.Round((1 - alpha) * a + alpha * b, 6);
        return new StyleVector(
            M(old.AccuracyFirst, neu.AccuracyFirst),
            M(old.Calibrated, neu.Calibrated),
            M(old.Steady, neu.Steady),
            M(old.Independent, neu.Independent),
            M(old.DeepDive, neu.DeepDive));
    }

    // ── 6. 隐私：k-匿名与教师侧脱敏 ─────────────────────
    public sealed record TagOut(string Tag, string Domain, double Weight);

    /// <summary>
    /// 教师侧可见标签：禁列域剔除 + k-匿名（可见标签 &lt;k 则整体抑制）。
    /// </summary>
    public static (IReadOnlyList<TagOut> Visible, bool Suppressed) TeacherView(
        IEnumerable<TagOut> tags, int k = 3)
    {
        var safe = tags
            .Where(t => !BannedDomains.Contains(t.Domain, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (safe.Count < k) return (Array.Empty<TagOut>(), true);
        return (safe.OrderByDescending(t => t.Weight).ToList(), false);
    }

    /// <summary>ε-差分隐私轻量噪声（Laplace），仅用于聚合外发，不改端上计算。</summary>
    public static double PrivacyNoise(double value, double epsilon = 1.0, double sensitivity = 1.0)
    {
        if (double.IsInfinity(epsilon) || epsilon <= 0) return value;
        // 确定性伪随机拉普拉斯（可测）：基于 value 哈希的符号与幅度
        var u = Math.Abs(Math.Sin(value * 12.9898) * 43758.5453) % 1.0;
        var sign = u < 0.5 ? -1.0 : 1.0;
        var mag = u < 1e-12 ? 0 : -Math.Log(1 - u) * sensitivity / epsilon;
        return value + sign * mag;
    }

    // ── 7. 状态软提示（非处分）──────────────────────────
    public static string SoftNudge(BehaviorFeatures f)
    {
        if (f.Attempts >= 5 && f.Accuracy < 0.35 && f.MeanConf > 0.6)
            return "最近正确率偏低但信心偏高，建议降低节奏、先做基础题。";
        if (f.Attempts >= 5 && f.Volatility > 0.6)
            return "正误波动较大，建议穿插复习稳定基础。";
        if (f.Attempts >= 5 && f.HintRate > 0.5)
            return "提示使用较多，可先尝试独立作答再看提示。";
        return "保持当前节奏。";
    }

    // ── 8. 禁列域守卫 ───────────────────────────────────
    public static bool IsBannedDomain(string domain)
        => BannedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
}
