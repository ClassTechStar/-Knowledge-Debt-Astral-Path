using System.Text.Json;
using AstralPath.Core.Diagnostics;
using System.Text.RegularExpressions;
using AstralPath.Core.Agent;

namespace AstralPath.Core.Planner;

/// <summary>计划编排输入（四源：教材目录与债边章节 / 画像 / 今日快照 / 债边事实清单）。</summary>
public sealed record PlanOrchestrationInput(
    string BookName,
    IReadOnlyList<string> ChapterTitles,
    string ProfileSummary,
    string TodaySummary,
    IReadOnlyList<PlanDebtFact> DebtFacts,
    IReadOnlyList<string> KpWhitelist,
    string UserNote);

public sealed record PlanDebtFact(string FromKp, string FromName, string ToKp, string ToName, double Impact, int EstMin);

/// <summary>计划草案（LLM 输出：总体策略 + 任务条目 + 执行提示 + 延期说明）。</summary>
public sealed record PlanDraft(string Strategy, IReadOnlyList<PlanDraftItem> Items, string? Tips, string? Deferred);

public sealed record PlanDraftItem(int Day, string KpName, string Type, int Difficulty, int EstMin, string Why, string? FromKpName = null);

/// <summary>编排结果（草案 → 校验修复后计划 + 违规摘要 + 来源）。</summary>
public sealed record PlanOrchestrationResult(
    IReadOnlyList<PlanDayInput> Days,
    bool FromAi,
    bool ConstraintChecked,
    IReadOnlyList<string> Violations,
    string? Strategy);

/// <summary>
/// AI 计划官（P1 迁移，源：Astralpath generatePlan/startGuiPlan T9，L2106-2183）。
/// 流程：四源上下文 → LLM 结构化草案 → 知识点白名单校验（幻觉条目剔除）→
/// 本地 K1-K5 合约全量终审（PlannerConstraintChecker）→ 自动修复（时长压缩/顺延/说明回填/先修顺序）→
/// 修复后仍违规则回退本地规则计划（PlannerConstraintChecker.TryGeneratePlan）。
/// 约束原则：AI 只产出草案叙事，数字与约束由本地公式/规则把关。
/// </summary>
public sealed class AiPlanOrchestrator
{
    private readonly MimoClient _client;

    public AiPlanOrchestrator(MimoClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>系统提示词（结构化 JSON 草案 + 知识点白名单 + 不改写数字约束）。</summary>
    public static string SystemPrompt =>
        "你是「知债」修复计划编排官。根据教材、学习者画像、今日快照与债边清单，编排一份修复计划草案。"
        + "硬性要求：1) 严格输出 JSON：{\"strategy\":\"总体策略，不超过80字\",\"items\":[{\"day\":1,\"kp\":\"知识点名\",\"type\":\"concept|drill|quiz|review\",\"difficulty\":2,\"est_min\":10,\"why\":\"执行理由，须包含先修知识点名\",\"from_kp\":\"先修知识点名或空\"}],\"tips\":\"执行提示\",\"deferred\":\"未排入债边的延期说明\"}；"
        + "2) kp 与 from_kp 必须使用给定白名单中的准确名称，不得发明新名称；"
        + "3) 单日任务总时长不超过给定预算；"
        + "4) 先修任务的 day 不得晚于目标任务；"
        + "5) 不要输出 JSON 以外的任何内容。";

    public static string BuildUserPrompt(PlanOrchestrationInput input, int dayBudgetMin, int horizonDays)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("教材《" + input.BookName + "》，修复周期 " + horizonDays + " 天，单日预算 " + dayBudgetMin + " 分钟。");
        sb.AppendLine("章节目录：" + string.Join("；", input.ChapterTitles.Take(24)));
        sb.AppendLine("学习者画像：" + input.ProfileSummary);
        sb.AppendLine("今日快照：" + input.TodaySummary);
        sb.AppendLine("债边事实清单（from=先修，to=目标，impact=影响分，est=预估分钟）：");
        foreach (var d in input.DebtFacts.Take(16))
            sb.AppendLine("- " + d.FromName + " → " + d.ToName + "（impact=" + d.Impact.ToString("F2") + "，est=" + d.EstMin + "min）");
        sb.AppendLine("知识点白名单：" + string.Join("、", input.KpWhitelist.Take(60)));
        if (!string.IsNullOrWhiteSpace(input.UserNote))
            sb.AppendLine("用户补充要求：" + input.UserNote);
        return sb.ToString();
    }

    /// <summary>草案 JSON 解析（容错通道 JsonLoose）。</summary>
    public static PlanDraft? ParseDraft(string? modelText)
    {
        var root = JsonLoose.Parse(modelText);
        if (root == null) return null;
        var r = root.Value;
        var strategy = r.TryGetProperty("strategy", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        var items = new List<PlanDraftItem>();
        if (r.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray().Take(200))
            {
                if (!it.TryGetProperty("kp", out var kp) || kp.ValueKind != JsonValueKind.String) continue;
                var kpName = kp.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(kpName)) continue;
                var day = it.TryGetProperty("day", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var dv) ? Math.Max(1, dv) : 1;
                var type = it.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "concept" : "concept";
                if (type is not ("concept" or "drill" or "quiz" or "review")) type = "concept";
                var diff = it.TryGetProperty("difficulty", out var df) && df.ValueKind == JsonValueKind.Number && df.TryGetInt32(out var dfv) ? Math.Clamp(dfv, 1, 5) : 2;
                var est = it.TryGetProperty("est_min", out var em) && em.ValueKind == JsonValueKind.Number && em.TryGetInt32(out var emv) ? Math.Clamp(emv, 5, 60) : 10;
                var why = it.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? "" : "";
                var from = it.TryGetProperty("from_kp", out var fk) && fk.ValueKind == JsonValueKind.String ? fk.GetString() : null;
                items.Add(new PlanDraftItem(day, kpName, type, diff, est, why, string.IsNullOrWhiteSpace(from) ? null : from));
            }
        }
        if (items.Count == 0) return null;
        var tips = r.TryGetProperty("tips", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;
        var deferred = r.TryGetProperty("deferred", out var df2) && df2.ValueKind == JsonValueKind.String ? df2.GetString() : null;
        return new PlanDraft(strategy, items, tips, deferred);
    }

    /// <summary>
    /// 编排主流程。模型失败/离线/未配置/草案不合法 → 回退本地规则计划（FromAi=false）。
    /// </summary>
    public async Task<PlanOrchestrationResult> OrchestrateAsync(
        MimoConfig cfg,
        PlanOrchestrationInput input,
        int dayBudgetMin,
        int horizonDays,
        Action<string, bool>? onDelta = null,
        CancellationToken ct = default)
    {
        // ① 本地规则计划兜底（先算好，随时可回退）
        var fallback = BuildLocalPlan(input, dayBudgetMin, horizonDays, out var fallbackChecked, out var fallbackViolations);

        PlanDraft? draft = null;
        try
        {
            var messages = new[]
            {
                new MimoMessage("system", SystemPrompt),
                new MimoMessage("user", BuildUserPrompt(input, dayBudgetMin, horizonDays))
            };
            var result = await _client.CompleteAsync(cfg, messages,
                new MimoOptions(MaxTokens: 3000, Temperature: 0.4, TimeoutMs: 45000, OnDelta: onDelta), ct).ConfigureAwait(false);
            draft = ParseDraft(result.Text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Diag.Recover(ex, "AiPlanOrchestrator");
        }

        if (draft == null)
            return new PlanOrchestrationResult(fallback, FromAi: false, fallbackChecked, fallbackViolations, Strategy: null);

        // ② 白名单校验：幻觉条目剔除（源 T9：知识点名称必须通过本地白名单校验）
        var whitelist = new HashSet<string>(input.KpWhitelist, StringComparer.Ordinal);
        var nameById = input.DebtFacts
            .SelectMany(d => new[] { (d.FromKp, d.FromName), (d.ToKp, d.ToName) })
            .GroupBy(x => x.Item2, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Item1, StringComparer.Ordinal);
        var kept = new List<PlanDraftItem>();
        var droppedCount = 0;
        foreach (var item in draft.Items)
        {
            if (whitelist.Count > 0 && !whitelist.Contains(item.KpName)) { droppedCount++; continue; }
            kept.Add(item);
        }
        if (kept.Count == 0)
            return new PlanOrchestrationResult(fallback, FromAi: false, fallbackChecked, fallbackViolations, draft.Strategy);

        // ③ 草案 → PlanDayInput（K3：why 须含先修名；缺失自动回填）
        var topDebts = input.DebtFacts.Take(5).Select(d => (d.FromKp, d.ToKp)).ToList();
        var days = Materialize(kept, nameById, input.DebtFacts);

        // ④ K1-K5 合约终审 + 自动修复（时长压缩/顺延/说明回填/先修顺序）
        days = AutoFix(days, dayBudgetMin, nameById);
        var check = PlannerConstraintChecker.Check(days, dayBudgetMin, topDebts);
        days = AutoFix(days, dayBudgetMin, nameById);
        check = PlannerConstraintChecker.Check(days, dayBudgetMin, topDebts);

        if (!check.ConstraintsChecked)
        {
            // 修复后仍违规：回退本地规则计划（源 T9：自动回退为本地规则生成的计划）
            return new PlanOrchestrationResult(fallback, FromAi: false, fallbackChecked,
                fallbackViolations.Concat(check.Violations.Select(v => v.Code + ":" + v.Message)).ToList(),
                draft.Strategy);
        }

        return new PlanOrchestrationResult(days, FromAi: true, ConstraintChecked: true,
            check.Violations.Select(v => v.Code + ":" + v.Message).ToList(), draft.Strategy);
    }

    private static List<PlanDayInput> Materialize(List<PlanDraftItem> items, Dictionary<string, string> nameById, IReadOnlyList<PlanDebtFact> debts)
    {
        var byDay = items.GroupBy(i => i.Day).OrderBy(g => g.Key).ToList();
        var days = new List<PlanDayInput>();
        foreach (var g in byDay)
        {
            var list = new List<PlanItemInput>();
            foreach (var item in g)
            {
                nameById.TryGetValue(item.KpName, out var kpId);
                var debtRef = item.FromKpName != null && nameById.TryGetValue(item.FromKpName, out var fromId)
                    ? new[] { fromId, kpId ?? item.KpName }
                    : Array.Empty<string>();
                var why = item.Why;
                // K3 自动回填：why 缺先修名则补入（源 T9：说明回填）
                if (!string.IsNullOrWhiteSpace(item.FromKpName) && !why.Contains(item.FromKpName, StringComparison.Ordinal))
                    why = "为巩固「" + item.FromKpName + "」再推进本点；" + why;
                if (string.IsNullOrWhiteSpace(why))
                    why = item.FromKpName != null
                        ? "从「" + item.FromKpName + "」先垫一步，再检验「" + item.KpName + "」"
                        : "巩固「" + item.KpName + "」（按教材顺序）";
                list.Add(new PlanItemInput(
                    Guid.NewGuid().ToString("N"), kpId ?? item.KpName, item.KpName,
                    item.Type, item.Difficulty, item.EstMin, why, debtRef, item.FromKpName));
            }
            days.Add(new PlanDayInput(g.Key, list));
        }
        return days;
    }

    /// <summary>自动修复：K1 时长压缩（压 drill 超额）、K3 说明回填、K2/K4 由顺序保证（源 T9：时长压缩/任务顺延/说明回填/先修顺序调整）。</summary>
    private static List<PlanDayInput> AutoFix(List<PlanDayInput> days, int budget, Dictionary<string, string> nameById)
    {
        var fixedDays = new List<PlanDayInput>();
        foreach (var day in days)
        {
            var items = day.Items.ToList();
            // K3 说明回填
            for (var i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (string.IsNullOrWhiteSpace(it.Why))
                {
                    it = it with { Why = it.FromKpName != null
                        ? "从「" + it.FromKpName + "」先垫一步，再检验「" + it.KpName + "」"
                        : "巩固「" + it.KpName + "」（按教材顺序）" };
                }
                else if (it.FromKpName != null && !it.Why.Contains(it.FromKpName, StringComparison.Ordinal))
                {
                    it = it with { Why = "为巩固「" + it.FromKpName + "」再推进本点；" + it.Why };
                }
                items[i] = it;
            }
            // K1 时长压缩：超额时先压 drill/quiz 时长（每条下限 5 分钟），再 drop 最低难度项
            var total = items.Where(x => x.Status != "dropped").Sum(x => x.EstMin);
            if (total > budget)
            {
                var over = total - budget;
                for (var i = 0; i < items.Count && over > 0; i++)
                {
                    if (items[i].Type is "drill" or "quiz" && items[i].EstMin > 5)
                    {
                        var cut = Math.Min(over, items[i].EstMin - 5);
                        items[i] = items[i] with { EstMin = items[i].EstMin - cut };
                        over -= cut;
                    }
                }
                // 仍超额：drop 最低难度项
                if (over > 0)
                {
                    foreach (var it in items.Where(x => x.Status != "dropped").OrderBy(x => x.Difficulty).ThenByDescending(x => x.EstMin).ToList())
                    {
                        if (over <= 0) break;
                        if (it.Type == "concept") continue;
                        var idx = items.IndexOf(it);
                        items[idx] = it with { Status = "dropped" };
                        over -= it.EstMin;
                    }
                }
            }
            // K4 顺序：同 kp 的 concept 排在 drill/quiz 之前
            var ordered = items
                .OrderBy(x => x.Type == "concept" ? 0 : 1)
                .ThenBy(x => x.KpId, StringComparer.Ordinal)
                .ToList();
            fixedDays.Add(new PlanDayInput(day.Day, ordered));
        }
        return fixedDays;
    }

    /// <summary>本地规则计划兜底（对接 PlannerConstraintChecker.TryGeneratePlan，源 buildPlan T9 同构）。</summary>
    private static List<PlanDayInput> BuildLocalPlan(PlanOrchestrationInput input, int budget, int horizonDays, out bool checkedOk, out List<string> violations)
    {
        var debts = input.DebtFacts.Select(d => new ScannedDebtInput(d.FromKp, d.ToKp, d.Impact, d.EstMin)).ToList();
        if (debts.Count == 0)
        {
            // 无债边：按白名单顺序排基础巩固（构造上满足 K1-K5）
            var days = new List<PlanDayInput>();
            var kpList = input.KpWhitelist.Take(Math.Min(horizonDays * 2, 12)).ToList();
            var perDay = Math.Max(1, kpList.Count / Math.Max(1, horizonDays));
            var idx = 0;
            for (var d = 1; d <= horizonDays && idx < kpList.Count; d++)
            {
                var items = new List<PlanItemInput>();
                for (var k = 0; k < perDay && idx < kpList.Count; k++, idx++)
                {
                    var name = kpList[idx];
                    items.Add(new PlanItemInput(Guid.NewGuid().ToString("N"), name, name,
                        "concept", 2, 12, "按教材顺序巩固「" + name + "」", Array.Empty<string>(), name));
                }
                if (items.Count > 0) days.Add(new PlanDayInput(d, items));
            }
            checkedOk = true;
            violations = new List<string>();
            return days;
        }
        var kpNames = input.DebtFacts
            .SelectMany(d => new[] { (d.FromKp, d.FromName), (d.ToKp, d.ToName) })
            .GroupBy(x => x.Item1, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.Ordinal);
        PlannerConstraintChecker.TryGeneratePlan(debts, kpNames, budget, horizonDays, out var schedule, out var check);
        checkedOk = check.ConstraintsChecked;
        violations = check.Violations.Select(v => v.Code + ":" + v.Message).ToList();
        return schedule.Days.ToList();
    }
}
