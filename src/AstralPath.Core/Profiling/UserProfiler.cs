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
/// 画像 v3：从作答行为推断学习风格与主题画像。
/// 纯函数 / 可选时间衰减 / 增量 EMA；禁止生成敏感域标签（sensitive/crisis/semantic）。
///
/// 相对 v2 的关键修复：
/// ① Volatility 只看「正误翻转率」，但翻转率的期望本来就随正确率变化
///    （acc=0.5 时随机序列期望翻转率就是 0.5）→ v2 会把 acc≈0.5 的正常学生误判为"波动大"。
///    v3 新增 ExcessVolatility = 实际翻转率 / 2·acc·(1−acc)，>1 才是真的比随机更抖。
/// ② Brier 只有单一标量，无法区分「整体校准好」与「高低两端互相抵消」。
///    v3 新增按信心分桶的校准曲线 + ECE（期望校准误差）。
/// ③ Pace 用绝对中位耗时，难题本来就慢 → 慢 ≠ 差。
///    v3 新增 PaceAdjusted：按难度归一后的耗时。
/// ④ Persistence = 每组平均次数，与总题数强相关（做题多就"更坚持"）。
///    v3 改用 RepeatRate = (总数−去重数)/总数，是真正的重复率。
/// ⑤ Topics 的兴趣用 n/total 且无时间衰减；难度因子实际取值域只有 [0.75,1.0]，形同虚设。
///    v3 加指数时间衰减，并把难度因子改成真正有区分度的目标难度曲线。
/// ⑥ TeacherView 的 k 语义错了：k-匿名应是「该标签覆盖的学生数 ≥k」，
///    而 v2 判断的是「标签个数 ≥k」。保留旧重载（改名为最小标签数），新增真 k-匿名重载。
/// ⑦ PrivacyNoise 用同一个伪随机数决定符号和幅度 → 分布严重偏离 Laplace；
///    且哈希基于 value，相同值永远加相同噪声，**不满足差分隐私**。
///    v3 改为标准 Laplace 逆变换，噪声由 seed 驱动（不再由 value 驱动）。
/// </summary>
public static class UserProfiler
{
    public static readonly string[] BannedDomains = { "sensitive", "crisis", "semantic" };

    // ── 1. 信心校准 ─────────────────────────────────────
    /// <summary>Brier 分数（越小越准）：(conf/5 − 1{对})²。契约保持不变。</summary>
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

    /// <summary>校准偏差：E[conf/5] − E[正确率]。&gt;0 过度自信。契约保持不变。</summary>
    public static double CalibrationBias(IEnumerable<AttemptSignal> signals)
    {
        var list = signals.ToList();
        if (list.Count == 0) return 0;
        var p = list.Average(s => Math.Clamp(s.SelfConf / 5.0, 0, 1));
        var y = list.Average(s => s.Correct ? 1.0 : 0.0);
        return p - y;
    }

    /// <summary>按信心档位（1–5）分桶的校准情况。</summary>
    public sealed record CalibrationBucket(
        int ConfLevel,
        int Count,
        double MeanConf,
        double Accuracy,
        double Gap);   // MeanConf − Accuracy，>0 该档过度自信

    /// <summary>
    /// 校准曲线。整体 Brier 会把"高信心答对"和"低信心答错"平均成好看的数字，
    /// 分桶才能看出到底哪一档失准。
    /// </summary>
    public static IReadOnlyList<CalibrationBucket> CalibrationCurve(IEnumerable<AttemptSignal> signals)
    {
        var list = signals.ToList();
        if (list.Count == 0) return Array.Empty<CalibrationBucket>();
        return list.GroupBy(s => Math.Clamp(s.SelfConf, 1, 5))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var meanConf = g.Average(s => Math.Clamp(s.SelfConf / 5.0, 0, 1));
                var acc = g.Average(s => s.Correct ? 1.0 : 0.0);
                return new CalibrationBucket(g.Key, g.Count(),
                    Math.Round(meanConf, 6), Math.Round(acc, 6), Math.Round(meanConf - acc, 6));
            })
            .ToList();
    }

    /// <summary>
    /// ECE（期望校准误差）= Σ (n_b/N)·|conf_b − acc_b|。
    /// 比单一 Brier 更能定位"哪一档不准"，也是校准类指标的通行做法。
    /// </summary>
    public static double Ece(IEnumerable<AttemptSignal> signals)
    {
        var curve = CalibrationCurve(signals);
        if (curve.Count == 0) return 0;
        var total = curve.Sum(b => b.Count);
        return Math.Round(curve.Sum(b => (double)b.Count / total * Math.Abs(b.Gap)), 6);
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
        double Volatility,        // 原始翻转率（契约保留）
        double Pace,              // 绝对速度（契约保留）
        double Persistence,       // 每组平均次数（契约保留）
        // ↓ v3 新增
        double ExcessVolatility,  // 相对随机基线的超额波动，>1 才是真抖
        double RepeatRate,        // (总数−去重数)/总数，真重复率
        double PaceAdjusted,      // 难度归一后的速度
        double Ece,               // 期望校准误差
        int StreakWrong);         // 末尾连续答错数

    /// <param name="halfLifeDays">时间衰减半衰期；null 表示不衰减（默认，保持 v2 行为）。</param>
    /// <param name="now">"现在"的时间基准，便于测试。</param>
    public static BehaviorFeatures Extract(
        IEnumerable<AttemptSignal> signals,
        double? halfLifeDays = null,
        DateTime? now = null)
    {
        var list = signals.OrderBy(s => s.At).ToList();
        if (list.Count == 0)
            return new BehaviorFeatures(0, 0, 0, 0.25, 0, 0, 0, 0, 0.5, 0, 0, 0, 0.5, 0, 0);

        // 时间权重：越新的作答证据越强
        var w = Weights(list, halfLifeDays, now);
        var wSum = w.Sum();
        double Wm(Func<AttemptSignal, double> f)
            => wSum <= 0 ? list.Average(f) : list.Select((s, i) => w[i] * f(s)).Sum() / wSum;

        var acc = Wm(s => s.Correct ? 1.0 : 0.0);
        var meanConf = Wm(s => Math.Clamp(s.SelfConf / 5.0, 0, 1));

        // 中位耗时（难度归一）
        var adj = list.Select(s => s.LatencyMs / (1.0 + 0.25 * (Math.Clamp(s.Difficulty, 1, 5) - 1))).OrderBy(x => x).ToList();
        var medAdj = Median(adj);
        var lats = list.Select(s => (double)s.LatencyMs).OrderBy(x => x).ToList();
        var med = Median(lats);

        var hintRate = Wm(s => Math.Min(s.HintsUsed, 3) / 3.0);
        var flips = 0;
        for (var i = 1; i < list.Count; i++)
            if (list[i].Correct != list[i - 1].Correct) flips++;
        var vol = list.Count <= 1 ? 0 : (double)flips / (list.Count - 1);

        // ★ v3：随机序列的期望翻转率 = 2·acc·(1−acc)。不归一化的话，
        //   acc≈0.5 的学生天然 flip≈0.5，会被 v2 误判成"波动大"。
        var expectedFlip = Math.Max(2 * acc * (1 - acc), 0.05);
        var excess = list.Count <= 1 ? 0 : vol / expectedFlip;

        var pace = Math.Exp(-med / 20000.0);
        var paceAdj = Math.Exp(-medAdj / 20000.0);
        var distinct = list.Select(s => s.KpId).Distinct(StringComparer.Ordinal).Count();
        var repeatRate = list.Count <= 1 ? 0 : (double)(list.Count - distinct) / list.Count;

        var streakWrong = 0;
        for (var i = list.Count - 1; i >= 0 && !list[i].Correct; i--) streakWrong++;

        return new BehaviorFeatures(
            list.Count, Math.Round(acc, 6), Math.Round(meanConf, 6),
            Math.Round(Brier(list), 6), Math.Round(CalibrationBias(list), 6),
            med, Math.Round(hintRate, 6), Math.Round(vol, 6),
            Math.Round(pace, 6), Math.Round(list.GroupBy(s => s.KpId).Average(g => g.Count()), 6),
            Math.Round(excess, 6), Math.Round(repeatRate, 6), Math.Round(paceAdj, 6),
            Ece(list), streakWrong);
    }

    private static double Median(IReadOnlyList<double> sorted)
        => sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    private static double[] Weights(IReadOnlyList<AttemptSignal> list, double? halfLifeDays, DateTime? now)
    {
        var n = list.Count;
        var w = new double[n];
        if (halfLifeDays is null || halfLifeDays <= 0)
        {
            Array.Fill(w, 1.0);
            return w;
        }
        var t0 = now ?? (list.Count > 0 ? list[^1].At : DateTime.UtcNow);
        var lambda = Math.Log(2.0) / halfLifeDays.Value;
        for (var i = 0; i < n; i++)
            w[i] = Math.Exp(-lambda * Math.Max(0, (t0 - list[i].At).TotalDays));
        return w;
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

        var accFirst = Math.Clamp(0.55 * f.Accuracy + 0.45 * f.PaceAdjusted, 0, 1);
        // 校准：仍以 Brier 为主（金样 UP04 依赖此口径），ECE 作为独立指标另出
        var calibrated = Math.Clamp(1.0 - Math.Min(f.Brier / 0.5, 1.0), 0, 1);
        // ★ v3：稳态用超额波动，不再把 acc≈0.5 的正常学生误判为不稳
        var steady = Math.Clamp(1.0 - Math.Min(f.ExcessVolatility, 1.0), 0, 1);
        var independent = Math.Clamp(1.0 - f.HintRate, 0, 1);
        // ★ v3：重复率取代"每组平均次数"（后者与总题数强相关）
        var deep = Math.Clamp(0.5 * DiffTol() + 0.5 * Math.Min(f.RepeatRate / 0.6, 1.0), 0, 1);
        return new StyleVector(
            Math.Round(accFirst, 6), Math.Round(calibrated, 6),
            Math.Round(steady, 6), Math.Round(independent, 6), Math.Round(deep, 6));
    }

    // ── 4. 主题画像（掌握 × 兴趣 × 难度）────────────────
    public sealed record TopicProfile(string KpId, double Mastery, double Interest, double DiffTolerance, double Weight);

    /// <summary>
    /// v3：兴趣带指数时间衰减（三个月前猛练的知识点不该今天还排第一）；
    /// 难度因子改成真正有区分度的目标难度曲线（v2 的因子取值域只有 [0.75,1.0]，等于没起作用）。
    /// </summary>
    public static IReadOnlyList<TopicProfile> Topics(
        IEnumerable<AttemptSignal> signals,
        IReadOnlyDictionary<string, double> masteryByKp,
        double? halfLifeDays = 14,
        DateTime? now = null)
    {
        var list = signals.ToList();
        if (list.Count == 0) return Array.Empty<TopicProfile>();

        var w = Weights(list, halfLifeDays, now);
        var wSum = w.Sum();
        if (wSum <= 0) { Array.Fill(w, 1.0); wSum = list.Count; }

        var byKp = new Dictionary<string, (double W, double Diff, int N)>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (!byKp.TryGetValue(s.KpId, out var cur)) cur = (0, 0, 0);
            byKp[s.KpId] = (cur.W + w[i], cur.Diff + w[i] * Math.Clamp(s.Difficulty, 1, 5), cur.N + 1);
        }

        return byKp.Select(kv =>
            {
                var (wsum, diffw, n) = kv.Value;
                var interest = wsum / wSum;
                var diff = Math.Clamp(diffw / Math.Max(wsum, 1e-9) / 5.0, 0, 1);
                var mastery = Math.Clamp(masteryByKp.GetValueOrDefault(kv.Key, 0.5), 0, 1);
                var gap = 1.0 - mastery;
                // 目标难度：太简单(0)与太难(1)都不如"最近发展区"中的 0.4–0.7
                var target = 1.0 - Math.Min(1.0, Math.Abs(diff - 0.55) / 0.45);  // ∈[0,1]，峰值在 0.55
                var weight = gap * (0.4 + 0.6 * Math.Min(interest * 2, 1.0)) * (0.35 + 0.65 * target);
                return new TopicProfile(kv.Key, mastery, Math.Round(interest, 6), Math.Round(diff, 6), Math.Round(weight, 6));
            })
            .OrderByDescending(t => t.Weight)
            .ThenBy(t => t.KpId, StringComparer.Ordinal)
            .ToList();
    }

    // ── 5. 动态 EMA（画像漂移）──────────────────────────
    /// <summary>style 指标指数平滑，α 为新证据权重。契约保持不变。</summary>
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

    /// <summary>
    /// 时间感知的 EMA：两次观测间隔越久，新证据权重越高（上限 0.9）。
    /// v2 的 α 固定，隔了一周和隔了一分钟用同一个权重，不合理。
    /// </summary>
    public static StyleVector SmoothAdaptive(StyleVector old, StyleVector neu, TimeSpan elapsed, double halfLifeDays = 7, double alphaMin = 0.1, double alphaMax = 0.9)
    {
        var days = Math.Max(0, elapsed.TotalDays);
        var alpha = alphaMin + (alphaMax - alphaMin) * (1 - Math.Exp(-Math.Log(2.0) * days / Math.Max(halfLifeDays, 0.01)));
        return Smooth(old, neu, alpha);
    }

    // ── 6. 隐私：k-匿名与教师侧脱敏 ─────────────────────
    public sealed record TagOut(string Tag, string Domain, double Weight);

    /// <summary>
    /// 教师侧可见标签：禁列域剔除 + 「最少标签数」门槛。
    /// ⚠️ 这里的 k 是**标签个数**，不是 k-匿名的 k（v2 把它叫 k-匿名是概念误用）。
    /// 真正的 k-匿名请用 <see cref="TeacherViewAnonymized"/>。
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

    /// <summary>
    /// 真·k-匿名：逐个标签检查「该标签覆盖的学生数是否 ≥k」，不足则**抑制该标签**，
    /// 而不是像 v2 那样一次性把所有标签都藏起来。
    /// </summary>
    public static (IReadOnlyList<TagOut> Visible, IReadOnlyList<string> SuppressedTags) TeacherViewAnonymized(
        IEnumerable<TagOut> tags,
        IReadOnlyDictionary<string, int> cohortCountByTag,
        int k = 3)
    {
        var visible = new List<TagOut>();
        var suppressed = new List<string>();
        foreach (var t in tags)
        {
            if (BannedDomains.Contains(t.Domain, StringComparer.OrdinalIgnoreCase))
            { suppressed.Add(t.Tag); continue; }              // 禁列域永不外发
            var n = cohortCountByTag.GetValueOrDefault(t.Tag, 0);
            if (n < k) { suppressed.Add(t.Tag); continue; }   // 样本不足 k → 抑制，不显示人数
            visible.Add(t);
        }
        return (visible.OrderByDescending(t => t.Weight).ToList(), suppressed);
    }

    /// <summary>
    /// ε-差分隐私 Laplace 噪声 v3。
    /// v2 的三个毛病：
    ///   ① 同一个伪随机数既决定符号又决定幅度 → 分布严重偏离 Laplace
    ///   ② 哈希基于 value → 相同值永远加相同噪声，攻击者可查表反推，**不满足 DP**
    ///   ③ 没有值域裁剪，可能越界
    /// v3：标准 Laplace 逆变换，噪声由 seed 驱动；相同 (seed,value) 仍可复现。
    /// 真实部署请为每个请求传入随机 seed，否则固定噪声不具备 DP 语义。
    /// </summary>
    public static double PrivacyNoise(
        double value,
        double epsilon = 1.0,
        double sensitivity = 1.0,
        long seed = 0,
        double? min = null,
        double? max = null)
    {
        if (double.IsInfinity(epsilon) || epsilon <= 0) return value;
        var b = sensitivity / epsilon;
        var u = Uniform(seed);                       // 与 value 无关，避免可反推
        var x = -b * Math.Sign(u - 0.5) * Math.Log(1 - 2 * Math.Abs(u - 0.5));
        var r = value + x;
        if (min.HasValue) r = Math.Max(min.Value, r);
        if (max.HasValue) r = Math.Min(max.Value, r);
        return r;
    }

    private static double Uniform(long seed)
    {
        // splitmix64：比 v2 的 sin-hash 分布好得多
        ulong x = (ulong)seed + 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        // 避开 0 与 0.5 两个奇点
        var u = (x >> 11) * (1.0 / 9007199254740992.0);
        if (u < 1e-12) u = 1e-12;
        if (u > 1 - 1e-12) u = 1 - 1e-12;
        if (Math.Abs(u - 0.5) < 1e-12) u = 0.5 + 1e-12;
        return u;
    }

    // ── 7. 状态软提示（非处分）──────────────────────────
    /// <summary>
    /// v3：过度自信改用「校准偏差」判据（v2 用绝对信心 >0.6，会把"确实会且信心高"的好学生误判）；
    /// 并加连续答错的兜底提示。
    /// </summary>
    public static string SoftNudge(BehaviorFeatures f)
    {
        if (f.Attempts >= 5 && f.Accuracy < 0.35 && f.CalibBias > 0.25)
            return "最近正确率偏低但信心偏高，建议降低节奏、先做基础题。";
        if (f.Attempts >= 5 && f.ExcessVolatility > 1.3)
            return "正误波动较大，建议穿插复习稳定基础。";
        if (f.Attempts >= 5 && f.HintRate > 0.5)
            return "提示使用较多，可先尝试独立作答再看提示。";
        if (f.StreakWrong >= 3)
            return "连续几题没通过，可以先回到先修知识点过一遍。";
        return "保持当前节奏。";
    }

    // ── 8. 一次性装配 ───────────────────────────────────
    public sealed record Profile(
        BehaviorFeatures Features,
        StyleVector Style,
        IReadOnlyList<TopicProfile> Topics,
        IReadOnlyList<CalibrationBucket> Calibration,
        string Nudge);

    public static Profile Build(
        IEnumerable<AttemptSignal> signals,
        IReadOnlyDictionary<string, double>? masteryByKp = null,
        double? halfLifeDays = null)
    {
        var list = signals.ToList();
        var f = Extract(list, halfLifeDays);
        var st = Style(f, list);
        var tp = Topics(list, masteryByKp ?? new Dictionary<string, double>(StringComparer.Ordinal));
        return new Profile(f, st, tp, CalibrationCurve(list), SoftNudge(f));
    }

    // ── 9. 禁列域守卫 ───────────────────────────────────
    public static bool IsBannedDomain(string domain)
        => BannedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
}
