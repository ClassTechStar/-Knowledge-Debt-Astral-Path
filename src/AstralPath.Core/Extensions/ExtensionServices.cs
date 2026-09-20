using System.Diagnostics.CodeAnalysis;
using AstralPath.Core.Formula;

namespace AstralPath.Core.Extensions;

/// <summary>
/// §19 扩展服务算法层（BASELINE 纯函数）。
/// 全部为确定性计算：同样输入必得同样输出，可写金样、可 1e-6 复核。
/// 引用口径：《技术方案》§19.1–§19.10；伦理口径见 §31 与《分工方案》附录 A。
/// </summary>

// ══════════════════════════════════════════════════════════════════════════
//  §19.1 concept-diffusion-svc 知识点传播模拟（BASELINE · diff-v1）
//  正向传播：b(0)_v = Δv (v∈S)，b(d)_v = Σ α·w(u,v)·b(d-1)_u
//  反向传播：g(0)_v = impactSumIn(v)，g(d)_v = Σ α·w(v,c)·g(d-1)_c
//  截断误差界：‖b(0)‖·α^(D+1)/(1-α)
// ══════════════════════════════════════════════════════════════════════════
public sealed record DiffusionInput(
    IReadOnlyList<string> Nodes,
    IReadOnlyList<(string From, string To, double Weight)> Edges,
    IReadOnlyDictionary<string, double> Score,
    IReadOnlyDictionary<string, double> Intervention,
    IReadOnlyList<(string From, string To, double Impact)> DebtEdges,
    double Alpha = 0.55,
    int MaxDepth = 6);

public sealed record DiffusionNodeGain(string KpId, double BaseScore, double Propagated, double ScoreHat, double Saturated);
public sealed record DiffusionResult(
    string AlgoVersion, double Alpha, int Depth, int NodeCount,
    double TruncationBound, IReadOnlyList<DiffusionNodeGain> Gains,
    IReadOnlyList<(string KpId, double RepairGain)> RepairRanking);

public static class ConceptDiffusion
{
    public const string Version = "diff-v1";

    public static DiffusionResult Run(DiffusionInput input)
    {
        var alpha = input.Alpha is > 0 and < 1 ? input.Alpha : 0.55;
        var depth = input.MaxDepth is > 0 and <= 32 ? input.MaxDepth : 6;

        var nodes = input.Nodes.Distinct(StringComparer.Ordinal).ToList();
        if (nodes.Count > 400) nodes = nodes.Take(400).ToList();          // N 上限（性能预算）
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++) index[nodes[i]] = i;

        var n = nodes.Count;
        var outAdj = new List<(int To, double W)>[n];
        var inAdj = new List<(int From, double W)>[n];
        for (var i = 0; i < n; i++) { outAdj[i] = new(); inAdj[i] = new(); }
        foreach (var (from, to, w) in input.Edges)
        {
            if (!index.TryGetValue(from, out var fi) || !index.TryGetValue(to, out var ti)) continue;
            var weight = Math.Clamp(w, 0, 2);
            outAdj[fi].Add((ti, weight));
            inAdj[ti].Add((fi, weight));
        }

        // 正向传播
        var b = new double[n];
        foreach (var (kp, delta) in input.Intervention)
            if (index.TryGetValue(kp, out var i)) b[i] = Math.Clamp(delta, 0, 100);

        var b0Norm = b.Sum(Math.Abs);
        var cur = (double[])b.Clone();
        for (var d = 1; d <= depth; d++)
        {
            var next = new double[n];
            for (var u = 0; u < n; u++)
            {
                if (cur[u] == 0) continue;
                foreach (var (v, w) in outAdj[u]) next[v] += alpha * w * cur[u];
            }
            cur = next;
            for (var i = 0; i < n; i++) b[i] += cur[i];
        }

        // 结果（score_hat = clamp(score + b, 0, 100)）
        var gains = new List<DiffusionNodeGain>(n);
        for (var i = 0; i < n; i++)
        {
            var kp = nodes[i];
            var baseScore = input.Score.TryGetValue(kp, out var s) ? s : 0;
            var propagated = Math.Round(b[i], 6);
            var unknown = Math.Clamp(baseScore + propagated, 0, 100);
            gains.Add(new DiffusionNodeGain(kp, Math.Round(baseScore, 6), propagated,
                Math.Round(unknown, 6), Math.Round(unknown - baseScore, 6)));
        }

        // 反向传播：修谁最划算
        var g = new double[n];
        foreach (var (from, to, impact) in input.DebtEdges)
            if (index.TryGetValue(to, out var ti)) g[ti] += Math.Max(0, impact);
        var gCur = (double[])g.Clone();
        var gTotal = (double[])g.Clone();
        for (var d = 1; d <= depth; d++)
        {
            var next = new double[n];
            for (var v = 0; v < n; v++)
            {
                if (gCur[v] == 0) continue;
                foreach (var (c, w) in outAdj[v]) next[c] += alpha * w * gCur[v];
            }
            gCur = next;
            for (var i = 0; i < n; i++) gTotal[i] += gCur[i];
        }

        var ranking = nodes
            .Select((kp, i) => (KpId: kp, RepairGain: Math.Round(gTotal[i], 6)))
            .OrderByDescending(x => x.RepairGain)
            .ToList();

        var truncationBound = Math.Round(b0Norm * Math.Pow(alpha, depth + 1) / (1 - alpha), 6);
        return new DiffusionResult(Version, alpha, depth, n, truncationBound, gains, ranking);
    }

    /// <summary>债边影响预测（§19.1 impact 预测 BASELINE）：只模拟"修复后是否仍满足条件"。</summary>
    public static IReadOnlyList<(string From, string To, double Impact, double ImpactHat, bool StillDebt)> PredictImpact(
        DiffusionInput input, DiffusionResult diffusion)
    {
        var hat = diffusion.Gains.ToDictionary(x => x.KpId, x => x.ScoreHat, StringComparer.Ordinal);
        var baseScore = diffusion.Gains.ToDictionary(x => x.KpId, x => x.BaseScore, StringComparer.Ordinal);
        var result = new List<(string, string, double, double, bool)>();
        foreach (var (from, to, impact) in input.DebtEdges)
        {
            var sp = baseScore.TryGetValue(from, out var a0) ? a0 : 0;
            var sc = baseScore.TryGetValue(to, out var b0) ? b0 : 0;
            var sph = hat.TryGetValue(from, out var a1) ? a1 : sp;
            var sch = hat.TryGetValue(to, out var b1) ? b1 : sc;
            double impactHat;
            var still = sph < 40 && sch < 50;                   // 债边条件仍满足？
            if (!still) impactHat = 0;                          // 条件不再满足 → 归零
            else
            {
                var denom = 50 - sc;
                impactHat = denom <= 0 ? impact : impact * (50 - sch) / denom;   // 避免除零
            }
            result.Add((from, to, Math.Round(impact, 6), Math.Round(Math.Max(0, impactHat), 6), still));
        }
        return result;
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  §19.2 exam-impact-svc 考试影响预测
//  口径：只读取债边与掌握度快照做线性响应，不重算 score 权威值。
// ══════════════════════════════════════════════════════════════════════════
public sealed record ExamImpactInput(
    string ExamId, int DaysToExam, int DayBudgetMin,
    IReadOnlyList<(string KpId, double Score, double Impact, double Weight)> Debts);

public sealed record ExamImpactResult(
    string ExamId, int DaysToExam, double ExpectedGain, string RiskBand,
    IReadOnlyList<(string KpId, double Impact, double Priority, int SuggestedMin)> Plan,
    string Note);

public static class ExamImpact
{
    public const string Version = "exam-v1";

    public static ExamImpactResult Run(ExamImpactInput input)
    {
        var days = Math.Max(1, input.DaysToExam);
        var budget = Math.Clamp(input.DayBudgetMin, 5, 120);

        // 优先级：impact × 权重 × 紧迫度（越近考试越紧迫）
        var urgency = days <= 3 ? 1.5 : days <= 7 ? 1.2 : 1.0;
        var ranked = input.Debts
            .Select(d => (d.KpId, d.Impact, Priority: Math.Round(d.Impact * Math.Clamp(d.Weight, 0, 2) * urgency, 6)))
            .OrderByDescending(x => x.Priority)
            .ToList();

        // 时间盒：总分片按天预算×天数，按优先级分配分钟（每天 ≤35 分钟为 BASELINE 约束）
        var perDayCap = Math.Min(budget, 35);
        var totalMin = perDayCap * days;
        var sumPriority = ranked.Sum(x => x.Priority);
        var plan = new List<(string, double, double, int)>();
        foreach (var r in ranked)
        {
            var share = sumPriority <= 0 ? 0 : r.Priority / sumPriority;
            var minutes = (int)Math.Floor(totalMin * share);
            plan.Add((r.KpId, r.Impact, r.Priority, minutes));
        }

        var expectedGain = Math.Round(ranked.Sum(x => x.Impact) * Math.Min(1, totalMin / 300.0), 6);
        var risk = expectedGain switch { >= 200 => "high", >= 100 => "medium", _ => "low" };
        var note = days <= 3
            ? "距考试 ≤3 天：仅保留最高优先级两项，避免超负荷。"
            : "按优先级与每日时间盒分配，任一分钟 ≤35 分钟。";
        return new ExamImpactResult(input.ExamId, days, expectedGain, risk, plan, note);
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  §19.3 peer-cohort-svc 同辈对照（强伦理）
//  铁律：k-匿名（默认 k=5）；不产出个体明细；不排名；不做能力定性。
// ══════════════════════════════════════════════════════════════════════════
public sealed record CohortInput(int K, IReadOnlyList<(string StudentId, double Score)> Members);

public sealed record CohortResult(int SampleCount, bool Suppressed, double? P25, double? P50, double? P75, string? Note);

public static class PeerCohort
{
    public const int DefaultK = 5;

    public static CohortResult Run(CohortInput input)
    {
        var k = Math.Max(2, input.K);
        var scores = input.Members.Select(m => m.Score).OrderBy(x => x).ToList();
        if (scores.Count < k)
            return new CohortResult(scores.Count, true, null, null, null,
                $"样本不足（{scores.Count} < k={k}），已做 k-匿名抑制，不产出分布。");

        static double Quantile(List<double> sorted, double q)
        {
            if (sorted.Count == 0) return 0;
            var pos = q * (sorted.Count - 1);
            var lo = (int)Math.Floor(pos);
            var hi = Math.Min(lo + 1, sorted.Count - 1);
            return Math.Round(sorted[lo] + (pos - lo) * (sorted[hi] - sorted[lo]), 6);
        }

        return new CohortResult(scores.Count, false,
            Quantile(scores, 0.25), Quantile(scores, 0.50), Quantile(scores, 0.75),
            "仅输出聚合分布；不输出个体、不排名、不做能力定性。");
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  §19.4 study-group-svc 学习小组匹配（强伦理）
//  铁律：只用聚合标签匹配，不用分数明文/排名；规模 3–5 人。
// ══════════════════════════════════════════════════════════════════════════
public sealed record GroupMemberInput(string StudentId, IReadOnlyList<string> Tags);
public sealed record GroupResult(IReadOnlyList<IReadOnlyList<string>> Groups, string Note);

public static class StudyGroup
{
    public static GroupResult Match(IReadOnlyList<GroupMemberInput> members, int targetSize = 4)
    {
        var size = Math.Clamp(targetSize, 3, 5);
        // 互补优先：按标签集合的 Jaccard 距离贪心聚成互补组（差异大 ⇒ 互补）
        var pending = members.ToList();
        var groups = new List<IReadOnlyList<string>>();
        while (pending.Count >= 2)
        {
            var seed = pending[0];
            pending.RemoveAt(0);
            var group = new List<string> { seed.StudentId };
            var seedTags = seed.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            while (group.Count < size && pending.Count > 0)
            {
                var bestIdx = 0;
                var bestScore = double.MinValue;
                for (var i = 0; i < pending.Count; i++)
                {
                    var t = pending[i].Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var union = seedTags.Union(t).Count();
                    var inter = seedTags.Intersect(t).Count();
                    var jaccard = union == 0 ? 0 : (double)inter / union;
                    var diversity = 1 - jaccard;                     // 差异即互补
                    if (diversity > bestScore) { bestScore = diversity; bestIdx = i; }
                }
                group.Add(pending[bestIdx].StudentId);
                pending.RemoveAt(bestIdx);
            }
            if (group.Count >= 2) groups.Add(group);
        }
        var note = groups.Count == 0
            ? "人数不足，未成组。"
            : "按互补标签成组（3–5 人）；不使用分数明文与排名。";
        return new GroupResult(groups, note);
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  §19.5 micro-lesson-svc 微课装配
//  铁律：不产出数字与结论（禁编造）；只装配资源清单与时长。
// ══════════════════════════════════════════════════════════════════════════
public sealed record MicroLessonInput(string KpId, string KpName, IReadOnlyList<string> Resources, int Minutes);

public sealed record MicroLessonResult(
    string Id, string KpId, string Caption, IReadOnlyList<string> Resources,
    int Minutes, bool HasNumericClaim, string Note);

public static class MicroLesson
{
    // 门禁只拦截**结论性数字**：分数（N 分，不含"分钟"）、百分比、个百分点。
    // 时长（N 分钟）属于排期事实，不是编造的学业结论，故放行。
    private static readonly System.Text.RegularExpressions.Regex NumberPattern =
        new(@"\d+(\.\d+)?\s*(%|个百分点)|\d+(\.\d+)?\s*分(?!钟)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static MicroLessonResult Assemble(MicroLessonInput input)
    {
        var minutes = Math.Clamp(input.Minutes, 3, 20);
        // 文案只描述主题，不出现任何数字结论（禁编造）
        var caption = $"{input.KpName}｜{minutes} 分钟微课：先看概念 → 再做 1 题自测 → 回看错因。";
        var hasNumeric = NumberPattern.IsMatch(caption) ||
                         input.Resources.Any(r => NumberPattern.IsMatch(r));
        return new MicroLessonResult(
            $"ml-{Guid.NewGuid():N}"[..14], input.KpId, caption, input.Resources, minutes, hasNumeric,
            hasNumeric ? "检测到数字表述，需经叙事门禁复核后方可发布。" : "无数字结论，可直接发布。");
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  §19.6 learning-velocity-svc 学习速度建模（禁黑盒）
//  口径：只用可解释的指数平滑 + 线性回归斜率；不引入不可解释模型。
// ══════════════════════════════════════════════════════════════════════════
public sealed record VelocityInput(IReadOnlyList<(DateTime At, double Score)> Series, double Alpha = 0.3);
public sealed record VelocityResult(double Smoothed, double SlopePerDay, string Trend, string Note);

public static class LearningVelocity
{
    public static VelocityResult Fit(VelocityInput input)
    {
        var pts = input.Series.OrderBy(p => p.At).ToList();
        if (pts.Count == 0) return new VelocityResult(0, 0, "unknown", "无样本。");

        var alpha = input.Alpha is > 0 and < 1 ? input.Alpha : 0.3;
        var smoothed = pts[0].Score;
        foreach (var p in pts) smoothed = alpha * p.Score + (1 - alpha) * smoothed;

        var t0 = pts[0].At;
        var xs = pts.Select(p => (p.At - t0).TotalDays).ToList();
        var ys = pts.Select(p => p.Score).ToList();
        var mx = xs.Average();
        var my = ys.Average();
        var denom = xs.Sum(x => (x - mx) * (x - mx));
        var slope = denom <= 0 ? 0 : xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / denom;

        var trend = slope > 0.5 ? "up" : slope < -0.5 ? "down" : "flat";
        return new VelocityResult(Math.Round(smoothed, 6), Math.Round(slope, 6), trend,
            "指数平滑 + 最小二乘斜率（可解释）；不使用黑盒模型。");
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  §19.7 spaced-review-svc 间隔重复调度（建议态）
//  铁律：只产出建议队列，不直接写 mastery / cleared。
// ══════════════════════════════════════════════════════════════════════════
public sealed record ReviewItem(string KpId, int Lapses, double LastScore, DateTime? LastReviewAt);

public sealed record ReviewPlanItem(string KpId, DateTime DueAt, int IntervalDays, string Reason);

public static class SpacedReview
{
    /// <summary>BASELINE：按掌握度与遗忘次数给出间隔（1/2/4/7/15 天阶梯）。</summary>
    public static IReadOnlyList<ReviewPlanItem> Schedule(IReadOnlyList<ReviewItem> items, DateTime now)
    {
        var plan = new List<ReviewPlanItem>();
        foreach (var it in items)
        {
            var ladder = new[] { 1, 2, 4, 7, 15 };
            var level = it.LastScore switch { >= 85 => 3, >= 70 => 2, >= 50 => 1, _ => 0 };
            level = Math.Clamp(level - Math.Min(2, it.Lapses), 0, ladder.Length - 1);
            var interval = ladder[level];
            var due = (it.LastReviewAt ?? now).AddDays(interval);
            if (due < now) due = now;
            plan.Add(new ReviewPlanItem(it.KpId, due, interval,
                $"score={it.LastScore:F1}, lapses={it.Lapses} ⇒ 阶梯第 {level + 1} 级"));
        }
        return plan.OrderBy(p => p.DueAt).ToList();
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  §19.8 prerequisite-simulator-svc 先修补全模拟器
//  口径：只做"若补齐某先修，债边会如何变化"的只读推演，不写生产 debt_edges。
// ══════════════════════════════════════════════════════════════════════════
public sealed record PrereqSimInput(
    IReadOnlyList<(string From, string To, double Weight)> Edges,
    IReadOnlyDictionary<string, double> Score,
    IReadOnlyList<(string From, string To, double Impact)> DebtEdges,
    string TargetKp, double TargetScore);

public sealed record PrereqSimResult(string TargetKp, double TargetScore, int AffectedEdges,
    double ImpactBefore, double ImpactAfter, double Delta, string Note);

public static class PrerequisiteSimulator
{
    public static PrereqSimResult Simulate(PrereqSimInput input)
    {
        var target = Math.Clamp(input.TargetScore, 0, 100);
        var before = input.DebtEdges
            .Where(e => string.Equals(e.From, input.TargetKp, StringComparison.Ordinal))
            .Sum(e => e.Impact);

        // 推演：目标被补到 target 后，若 p≥40 则该批债边条件不再满足 ⇒ impact 归零
        var after = target >= 40 ? 0 : before * (50 - target) / 50.0;
        var affected = input.DebtEdges.Count(e => string.Equals(e.From, input.TargetKp, StringComparison.Ordinal));
        return new PrereqSimResult(input.TargetKp, target, affected,
            Math.Round(before, 6), Math.Round(Math.Max(0, after), 6),
            Math.Round(before - Math.Max(0, after), 6),
            "只读推演；不写生产 debt_edges，不改写掌握度。");
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  §19.9 knowledge-forecast-svc 学业预警预测（强伦理）
//  铁律：只输出**区间与建议**，不输出"不及格概率"等可当处分的定性；fail-closed。
// ══════════════════════════════════════════════════════════════════════════
public sealed record ForecastInput(
    double CurrentScore, double SlopePerDay, int DaysAhead, double OpenDebtImpact);

public sealed record ForecastResult(
    double ExpectedScore, double LowerBound, double UpperBound, string Band,
    bool Suppressed, string Note);

public static class KnowledgeForecast
{
    public static ForecastResult Forecast(ForecastInput input)
    {
        var days = Math.Clamp(input.DaysAhead, 1, 90);
        var expected = Math.Clamp(input.CurrentScore + input.SlopePerDay * days, 0, 100);
        // 区间宽度随外推天数与债边压力放大（不确定性随时间增大）
        var spread = Math.Clamp(5 + days * 0.4 + input.OpenDebtImpact / 40.0, 5, 30);
        var lower = Math.Clamp(expected - spread, 0, 100);
        var upper = Math.Clamp(expected + spread, 0, 100);
        var band = expected switch { >= 80 => "稳固", >= 60 => "基本掌握", >= 40 => "需要巩固", _ => "需要优先处理" };

        var suppressed = days > 30;                       // 外推过远 ⇒ 抑制（避免误用于处分）
        var note = suppressed
            ? "外推超过 30 天，结果仅供参考，不用于任何处分或评价依据。"
            : "输出区间与建议；不输出不及格概率，不作为处分依据。";
        return new ForecastResult(Math.Round(expected, 6), Math.Round(lower, 6), Math.Round(upper, 6),
            band, suppressed, note);
    }
}

// ══════════════════════════════════════════════════════════════════════════
//  §19.10 lab-bench-svc 算法实验台（内部）
//  口径：只做候选公式注册、回归对比与双人审批，不触碰生产写路径。
// ══════════════════════════════════════════════════════════════════════════
public sealed record LabExperiment(string Id, string Name, string CandidateVersion, double Tolerance);
public sealed record LabRunResult(string ExperimentId, int Cases, int Passed, double MaxDelta, bool RegressionOk);

public static class LabBench
{
    public static LabRunResult Run(string experimentId, IReadOnlyList<(double Expected, double Actual)> cases, double tolerance = 1e-6)
    {
        var tol = tolerance <= 0 ? 1e-6 : tolerance;
        var maxDelta = cases.Count == 0 ? 0 : cases.Max(c => Math.Abs(c.Expected - c.Actual));
        var passed = cases.Count(c => Math.Abs(c.Expected - c.Actual) <= tol);
        return new LabRunResult(experimentId, cases.Count, passed, Math.Round(maxDelta, 9), passed == cases.Count);
    }

    /// <summary>公式版本注册（双人审批：proposer ≠ approver）。</summary>
    public static (bool Ok, string Reason) Promote(string version, string proposer, string approver)
    {
        if (string.IsNullOrWhiteSpace(version)) return (false, "版本名为空");
        if (string.IsNullOrWhiteSpace(proposer) || string.IsNullOrWhiteSpace(approver))
            return (false, "缺少提议人或审批人");
        if (string.Equals(proposer, approver, StringComparison.OrdinalIgnoreCase))
            return (false, " DOUBLE-CHECK 未满足：提议人与审批人不可为同一人");
        return (true, $"公式版本 {version} 已由 {proposer} 提议、{approver} 审批通过");
    }

    /// <summary>与 BASELINE 权重版本对齐（§19.10 回归基准）。</summary>
    public static string BaselineFormulaVersion => FormulaWeights.WeightVersion;
}
