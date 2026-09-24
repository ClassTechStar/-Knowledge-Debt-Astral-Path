using AstralPath.Contracts;
using AstralPath.Core.Algorithms;
using AstralPath.Core.Formula;
using AstralPath.Core.Models;
using AstralPath.Core.Narrative;
using AstralPath.Core.Planner;
using AstralPath.Infrastructure;

namespace AstralPath.Shared.Services;

/// <summary>
/// 离线演示数据源：在**进程内**复现 API 契约的读路径，零外部依赖。
///
/// 与 API 的一致性策略：
/// <list type="bullet">
///   <item>公式类（score / impact / K1–K5 / 销账）**直接调用 Core 纯函数**，与 API 同源，
///         因此「手算 = API = 端上显示」在离线模式下同样成立（容差 1e-6）。</item>
///   <item>计划生成复用 <see cref="PlannerConstraintChecker.TryGeneratePlan"/>（Core 内的权威实现），
///         不另写一份规划器。</item>
///   <item>仅「今日任务组装」与「教师热点聚合」的编排顺序按 <c>AstralPath.Api</c> 控制器对齐，
///         字段来源逐项对应（见各方法注释）。</item>
/// </list>
/// 该实现同时用于 Headless UI 测试，保证测试与演示走同一条代码路径。
/// </summary>
public sealed partial class OfflineDemoDataSource : IAppDataSource
{
    private readonly AstralPathStore _store;
    private readonly AstralPathModules _modules = new();
    private readonly object _gate = new();

    public OfflineDemoDataSource(string? graphPackDirectory = null)
    {
        _store = new AstralPathStore(graphPackDirectory ?? AstralPathStore.FindGraphPack());
    }

    public string ModeLabel => "离线演示";

    // ── 学生 ───────────────────────────────────────────────────

    public IReadOnlyList<StudentSummary> Students => _store.Lock(() =>
        _store.Students.Values
            .OrderBy(s => s.DemoGroup, StringComparer.Ordinal)
            .ThenBy(s => s.StudentId, StringComparer.Ordinal)
            .Select(s => new StudentSummary(
                s.StudentId,
                s.DisplayName,
                s.DemoGroup,
                s.DebtEdges.Count(d => d.Status != "cleared"),
                s.Mastery.Count == 0 ? 0 : Math.Round(s.Mastery.Values.Average(m => m.Score), 6)))
            .ToList());

    // ── 图与诊断（对齐 StudentsController.GraphView / Diagnose） ──

    public GraphViewDto GetGraphView(string studentId) => _store.Lock(() =>
    {
        var student = RequireStudent(studentId);

        var debts = student.DebtEdges
            .Where(d => d.Status != "cleared")
            .OrderByDescending(d => d.Impact)
            .ToList();

        var involved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in debts)
        {
            involved.Add(d.FromKp);
            involved.Add(d.ToKp);
            foreach (var n in _store.Graph.OutNeighbors(d.FromKp)) involved.Add(n);
            foreach (var e in _store.Graph.EdgesOfNode(d.FromKp).SelectMany(x => new[] { x.From, x.To })) involved.Add(e);
        }
        if (involved.Count == 0)
        {
            foreach (var n in _store.Graph.Nodes.Take(20)) involved.Add(n.Id);
        }

        var nodes = new List<GraphViewNodeDto>();
        foreach (var kpId in involved.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!_store.Graph.TryGetNode(kpId, out var node)) continue;
            student.Mastery.TryGetValue(kpId, out var mastery);
            var score = mastery?.Score ?? 0;
            nodes.Add(new GraphViewNodeDto(node.Id, node.Name, node.Course, score, ScoreCalculator.BandOf(score)));
        }

        var edges = debts.Select(d => new GraphViewEdgeDto(
            d.FromKp, d.ToKp, d.Impact, d.Status, d.Weight,
            d.FromKpName, d.ToKpName, d.ScoreFrom, d.ScoreTo, d.Freq)).ToList();

        return new GraphViewDto(studentId, _store.Graph.GraphVersion, FormulaWeights.WeightVersion,
            nodes, edges, edges.Count, DateTime.UtcNow);
    });

    public DiagnoseResponse Diagnose(string studentId, int topN) => _store.Lock(() =>
    {
        RequireStudent(studentId);
        topN = Math.Clamp(topN, 1, 20);
        var scanned = _store.ScanDebts(studentId, topN);

        var topDebts = scanned.Select(s => new DebtTopItem(
            s.FromKp, s.ToKp, s.FromKpName, s.ToKpName,
            s.ScoreFrom, s.ScoreTo, s.Freq, s.Impact, "open")).ToList();

        var narratives = new List<NarrativeDto>();
        foreach (var debt in topDebts)
        {
            var slots = new NarrativeSlots(debt.FromKpName, debt.ToKpName, debt.Impact, debt.ScoreFrom, debt.ScoreTo);
            var n = NarrativeGuard.Build(slots);
            narratives.Add(new NarrativeDto(
                debt.FromKp, debt.ToKp, n.Story, n.Actions.ToList(), n.Tone,
                new SafetyFlagsDto(n.BannedHit, n.SlotFailed, n.DegradedTemplate), n.ModelVer));
        }

        return new DiagnoseResponse(studentId, _store.Graph.GraphVersion, FormulaWeights.WeightVersion,
            FormulaInfo.Default, topDebts, narratives);
    });

    public IReadOnlyList<MasteryRow> GetMastery(string studentId) => _store.Lock(() =>
    {
        var student = RequireStudent(studentId);
        return student.Mastery.Values
            .OrderBy(m => m.KpId, StringComparer.Ordinal)
            .Select(m => new MasteryRow(
                m.KpId,
                _store.Graph.TryGetNode(m.KpId, out var n) ? n.Name : m.KpId,
                m.RecentAcc, m.Sev, m.SelfConf, m.Score, m.Band))
            .ToList();
    });

    // ── 计划（复用 Core 权威规划器） ─────────────────────────────

    public PlanDto CreatePlan(string studentId, int dayBudgetMin = 35, int horizonDays = 14) => _store.Lock(() =>
    {
        var student = RequireStudent(studentId);
        dayBudgetMin = Math.Clamp(dayBudgetMin, 10, 180);
        horizonDays = Math.Clamp(horizonDays, 1, 28);

        var debts = student.DebtEdges
            .Where(d => d.Status is "open" or "repairing")
            .OrderByDescending(d => d.Impact)
            .ToList();
        if (debts.Count == 0)
        {
            _store.ScanDebts(studentId);
            debts = student.DebtEdges.Where(d => d.Status is "open" or "repairing")
                .OrderByDescending(d => d.Impact).ToList();
        }

        var selected = debts.Take(FormulaWeights.DefaultTopN).ToList();
        var inputs = selected
            .Select(d => new ScannedDebtInput(d.FromKp, d.ToKp, d.Impact,
                Math.Clamp((int)Math.Round(d.Impact / 8) + 6, 6, 20)))
            .ToList();
        var kpNames = _store.Graph.Nodes.ToDictionary(n => n.Id, n => n.Name, StringComparer.Ordinal);

        PlannerConstraintChecker.TryGeneratePlan(inputs, kpNames, dayBudgetMin, horizonDays,
            out var schedule, out var check);

        var now = DateTime.UtcNow;
        var planId = $"plan-{studentId}-{now:yyyyMMddHHmmss}";

        var days = new List<PlanDayDto>();
        foreach (var d in schedule.Days)
        {
            var ordinal = 0;
            var items = d.Items.Select(i => new PlanItemDto(
                i.Id, planId, studentId, d.Day, ordinal++, i.KpId, i.KpName, i.Type,
                i.Difficulty, i.EstMin, i.Why, i.DebtRef.ToList(),
                i.Status == "dropped" ? "dropped" : "pending", now)).ToList();
            days.Add(new PlanDayDto(d.Day, items.Where(x => x.Status != "dropped").Sum(x => x.EstMin), items));
        }

        var violations = check.Violations
            .Select(v => new ConstraintViolationDto(v.Code, v.Day, v.Message, v.Detail))
            .ToList();

        var dto = new PlanDto(planId, studentId, _store.Graph.GraphVersion, FormulaWeights.WeightVersion,
            dayBudgetMin, check.ConstraintsChecked, violations, FormulaWeights.PlannerVersion,
            days, now, now.AddDays(horizonDays));

        student.ActivePlan = new PlanDtoHolder
        {
            Id = planId,
            StudentId = studentId,
            GraphVersion = _store.Graph.GraphVersion,
            DayBudgetMin = dayBudgetMin,
            ConstraintsChecked = check.ConstraintsChecked,
            Violations = check.Violations,
            Days = schedule.Days,
            CreatedAt = now,
            ExpiresAt = now.AddDays(horizonDays)
        };
        student.CurrentDay = 1;

        // 与 PlansController 一致：被选中的债边进入 repairing
        foreach (var s in selected)
        {
            var idx = student.DebtEdges.FindIndex(d => d.FromKp == s.FromKp && d.ToKp == s.ToKp);
            if (idx >= 0) student.DebtEdges[idx] = student.DebtEdges[idx] with { Status = "repairing", UpdatedAt = now };
        }

        return dto;
    });

    public PlanDto? GetPlan(string studentId) => _store.Lock(() =>
    {
        var student = RequireStudent(studentId);
        var holder = student.ActivePlan;
        if (holder is null) return null;

        var days = holder.Days.Select(d =>
        {
            var ordinal = 0;
            var items = d.Items.Select(i => new PlanItemDto(
                i.Id, holder.Id, studentId, d.Day, ordinal++, i.KpId, i.KpName, i.Type,
                i.Difficulty, i.EstMin, i.Why, i.DebtRef.ToList(), i.Status, holder.CreatedAt)).ToList();
            return new PlanDayDto(d.Day, items.Where(x => x.Status != "dropped").Sum(x => x.EstMin), items);
        }).ToList();

        var violations = holder.Violations
            .Select(v => new ConstraintViolationDto(v.Code, v.Day, v.Message, v.Detail))
            .ToList();

        return new PlanDto(holder.Id, studentId, holder.GraphVersion, FormulaWeights.WeightVersion,
            holder.DayBudgetMin, holder.ConstraintsChecked, violations, FormulaWeights.PlannerVersion,
            days, holder.CreatedAt, holder.ExpiresAt);
    });

    // ── 今日任务（对齐 CoachProgressController.Today） ────────────

    public TodayResponse GetToday(string studentId, int? day = null) => _store.Lock(() =>
    {
        var student = RequireStudent(studentId);
        var holder = student.ActivePlan;

        var currentDay = day
            ?? (holder is null ? student.CurrentDay : Math.Clamp(student.CurrentDay, 1, Math.Max(1, holder.Days.Count)));
        currentDay = Math.Clamp(currentDay, 1, 14);
        student.CurrentDay = currentDay;

        var tasks = new List<TodayTaskDto>();
        var planDay = holder?.Days.FirstOrDefault(d => d.Day == currentDay);

        if (planDay is not null)
        {
            var qIndex = 0;
            foreach (var item in planDay.Items.Where(i => i.Status != "dropped").Take(8))
            {
                var q = PickQuestion(item.KpId, qIndex++);
                var conf = student.Mastery.TryGetValue(item.KpId, out var m) ? (int)Math.Round(m.SelfConf) : 3;
                var acc = m?.RecentAcc ?? 0.5;
                var decision = CoachRules.Decide(acc, conf, 0, item.KpName);

                tasks.Add(new TodayTaskDto(
                    Id: $"task-{currentDay}-{item.Id}",
                    KpId: item.KpId,
                    KpName: item.KpName,
                    Type: decision.Action == "skip_easy" ? "review" : item.Type,
                    Difficulty: decision.Action == "downgrade" ? Math.Max(1, item.Difficulty - 1) : item.Difficulty,
                    EstMin: item.EstMin,
                    Why: item.Why,
                    QuestionId: q.Id,
                    Stem: q.Stem,
                    Options: q.Options.ToList(),
                    CorrectIndex: q.CorrectIndex,
                    PlanItemId: item.Id));
            }
        }
        else
        {
            // 无计划：退化为按 impact 降序的债边建议（与控制器第 ④ 分支一致）
            foreach (var debt in student.DebtEdges.Where(d => d.Status != "cleared")
                         .OrderByDescending(d => d.Impact).Take(3))
            {
                var q = PickQuestion(debt.FromKp, 0);
                tasks.Add(new TodayTaskDto(
                    $"task-{currentDay}-{debt.FromKp}", debt.FromKp, debt.FromKpName, "concept", 2, 8,
                    $"为还 {debt.FromKpName} → {debt.ToKpName} 的债",
                    q.Id, q.Stem, q.Options.ToList(), q.CorrectIndex, null));
            }
        }

        var total = tasks.Sum(t => t.EstMin);
        var done = student.Attempts.Count(a => a.PlanItemId is not null && planDay is not null
                                              && planDay.Items.Any(i => i.Id == a.PlanItemId));
        var message = tasks.Count == 0
            ? "今天没有待办任务，可以先做一次诊断看看有没有新的知识债。"
            : $"今天 {tasks.Count} 个任务，预计 {total} 分钟。已完成 {Math.Min(done, tasks.Count)} 个。";

        return new TodayResponse(studentId, currentDay, DateTime.UtcNow.Date, total, message, tasks);
    });

    private AstralPathStore.QuestionBankItem PickQuestion(string kpId, int index)
    {
        var candidates = _store.Questions.Values.Where(q => q.KpId == kpId).ToList();
        if (candidates.Count == 0) candidates = _store.Questions.Values.ToList();
        return candidates[index % Math.Max(1, candidates.Count)];
    }

    /// <summary>与 CoachProgressController 一致：SHA-256 小写十六进制（防剧透 stem_hash）。</summary>
    private static string StemHash(string stem)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stem)))
            .ToLowerInvariant();

    // ── 作答与销账 ─────────────────────────────────────────────

    public AttemptDto SubmitAttempt(CreateAttemptRequest request) => _store.Lock(() =>
    {
        var student = _store.EnsureStudent(request.StudentId);
        var now = DateTime.UtcNow;
        var stem = _store.Questions.TryGetValue(request.QuestionId, out var q) ? q.Stem : request.QuestionId;

        var attempt = new Attempt(
            $"att-{Guid.NewGuid():N}"[..16], request.StudentId, request.PlanItemId, request.KpId,
            request.QuestionId, StemHash(stem), "choice",
            request.Correct, request.SelfConf, request.LatencyMs, request.HintsUsed, now, now);

        student.Attempts.Add(attempt);

        if (student.MasteryInputs.TryGetValue(request.KpId, out var row))
        {
            var newAcc = (row.RecentAcc * 9 + (request.Correct ? 1 : 0)) / 10.0;
            var newSev = Math.Clamp(row.Sev * (request.Correct ? 0.9 : 1.05), 0, 1);
            student.MasteryInputs[request.KpId] = row with { RecentAcc = newAcc, Sev = newSev };
            _store.RecomputeMastery(request.StudentId);
        }

        if (request.PlanItemId is not null && student.ActivePlan is not null)
        {
            var item = student.ActivePlan.Days
                .SelectMany(d => d.Items)
                .FirstOrDefault(i => i.Id == request.PlanItemId);
            if (item is not null && item.DebtRef.Count >= 2)
            {
                var key = $"{item.DebtRef[0]}->{item.DebtRef[1]}";
                if (!student.SaleHistory.TryGetValue(key, out var probes))
                {
                    probes = new List<SaleProbe>();
                    student.SaleHistory[key] = probes;
                }
                probes.Add(new SaleProbe(request.Correct ? 0.85 : 0.4, request.SelfConf));

                var sale = SaleCompat.IsSaleable(probes);
                student.SaleStreak[key] = sale.Streak;
                if (sale.Saleable)
                {
                    var idx = student.DebtEdges.FindIndex(d => d.FromKp == item.DebtRef[0] && d.ToKp == item.DebtRef[1]);
                    if (idx >= 0 && student.DebtEdges[idx].Status != "cleared")
                    {
                        student.DebtEdges[idx] = student.DebtEdges[idx] with { Status = "cleared", SaleStreak = sale.Streak, UpdatedAt = now };
                    }
                }
            }
        }

        return new AttemptDto(attempt.Id, attempt.StudentId, attempt.PlanItemId, attempt.KpId,
            attempt.QuestionId, attempt.StemHash, attempt.Correct, attempt.SelfConf,
            attempt.LatencyMs, attempt.OccurredAt);
    });

    public SaleCheckResponse SaleCheck(string studentId, string fromKp, string toKp) => _store.Lock(() =>
    {
        var student = RequireStudent(studentId);
        var key = $"{fromKp}->{toKp}";

        if (!student.SaleHistory.TryGetValue(key, out var probes) || probes.Count == 0)
        {
            probes = student.Attempts
                .Where(a => a.KpId == fromKp || a.KpId == toKp)
                .OrderBy(a => a.OccurredAt)
                .Select(a => new SaleProbe(a.Correct ? 0.85 : 0.4, a.SelfConf))
                .ToList();
        }

        var sale = SaleCompat.IsSaleable(probes);
        var edge = student.DebtEdges.FirstOrDefault(d => d.FromKp == fromKp && d.ToKp == toKp);
        var impact = edge is null
            ? new ImpactResult(false, 0, 0)
            : DebtScannerCompat.ComputeImpactV1(new ImpactInput(edge.ScoreFrom, edge.ScoreTo, edge.Freq,
                edge.DaysSinceLastError, edge.Weight));

        if (sale.Saleable && edge is not null && edge.Status is "open" or "repairing")
        {
            var idx = student.DebtEdges.IndexOf(edge);
            student.DebtEdges[idx] = edge with { Status = "cleared", SaleStreak = sale.Streak, UpdatedAt = DateTime.UtcNow };
            edge = student.DebtEdges[idx];
        }

        var status = edge?.Status ?? "open";
        return new SaleCheckResponse(status == "cleared", status == "cleared" ? null : sale.Need,
            sale.Streak, Math.Round(impact.Impact, 6), status);
    });

    // ── What-if（对齐 TeacherConsentGraphController 的 what-if 分支） ──

    public WhatIfResponse WhatIf(WhatIfRequest request) => _store.Lock(() =>
    {
        var student = RequireStudent(request.StudentId);
        var edge = student.DebtEdges.FirstOrDefault(d => d.FromKp == request.FromKp && d.ToKp == request.ToKp);

        var scoreFrom = request.OverrideScoreFrom
            ?? edge?.ScoreFrom
            ?? (student.Mastery.TryGetValue(request.FromKp, out var mf) ? mf.Score : 0);
        var scoreTo = request.OverrideScoreTo
            ?? edge?.ScoreTo
            ?? (student.Mastery.TryGetValue(request.ToKp, out var mt) ? mt.Score : 0);
        var freq = request.OverrideFreq ?? edge?.Freq ?? 3;
        var days = request.OverrideDays ?? edge?.DaysSinceLastError ?? 0;
        var weight = edge?.Weight ?? 1.0;

        var impact = DebtScannerCompat.ComputeImpactV1(new ImpactInput(scoreFrom, scoreTo, freq, days, weight));
        return new WhatIfResponse(
            Math.Round(scoreFrom, 6), Math.Round(scoreTo, 6), freq, days,
            impact.Impact, impact.Detected, impact.Recency);
    });

    // ── 教师端与 consent（fail-closed + k-匿名） ─────────────────

    private static string ConsentKey(string studentId, string teacherId, string purpose)
        => $"{studentId}:{teacherId}:{purpose}";

    public HotspotsResponse GetHotspots(string teacherId) => _store.Lock(() =>
    {
        var authorized = _store.Consents.Values
            .Where(c => c.TeacherId == teacherId && c.State == "granted" && c.AllowTeacher)
            .Select(c => c.StudentId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (authorized.Count == 0)
        {
            return new HotspotsResponse(teacherId, "default", 0, true, new List<HotspotDto>(),
                "无有效授权，教师端不可见任何班级热点（默认不可见 / fail-closed）");
        }

        _store.RebuildTeacherCache(teacherId);
        if (!_store.TeacherCache.TryGetValue(teacherId, out var cached))
        {
            cached = new();
        }

        var hotspots = cached.Select(h => new HotspotDto(
            h.FromKp, h.ToKp, h.FromKpName, h.ToKpName, h.StudentCount, h.AvgImpact)).ToList();

        // k-匿名：桶过小且热点过多时抑制（方案 §19.3）
        var suppressed = hotspots.Count > 0
                         && hotspots.Min(h => h.StudentCount) < 3
                         && hotspots.Count > 5;

        var emptyReason = hotspots.Count == 0 ? "已授权学生暂无开放债边" : string.Empty;
        return new HotspotsResponse(teacherId, "default", authorized.Count, suppressed, hotspots, emptyReason);
    });

    public ConsentDto? GetConsent(string studentId, string teacherId, string purpose)
    {
        var key = ConsentKey(studentId, teacherId, purpose);
        return _store.Lock(() => _store.Consents.TryGetValue(key, out var c) ? ToDto(c) : null);
    }

    public ConsentDto Grant(string studentId, string teacherId, string purpose) => _store.Lock(() =>
    {
        var now = DateTime.UtcNow;
        var key = ConsentKey(studentId, teacherId, purpose);
        var auditId = $"aud-{Guid.NewGuid():N}"[..16];

        var consent = new Consent(studentId, teacherId, "granted", true, now, null, purpose, auditId, now);
        _store.Consents[key] = consent;
        _store.ConsentAudits.Add(new ConsentAudit(auditId, studentId, studentId, "student", "grant",
            purpose, now, $"corr-{Guid.NewGuid():N}"[..16]));
        _store.RebuildTeacherCache(teacherId);
        return ToDto(consent);
    });

    public ConsentDto Revoke(string studentId, string teacherId, string purpose) => _store.Lock(() =>
    {
        var now = DateTime.UtcNow;
        var key = ConsentKey(studentId, teacherId, purpose);
        var old = _store.Consents.TryGetValue(key, out var c)
            ? c
            : new Consent(studentId, teacherId, "none", false, null, null, purpose, "none", now);

        var auditId = $"aud-{Guid.NewGuid():N}"[..16];
        var revoked = old with { State = "revoked", AllowTeacher = false, RevokedAt = now, UpdatedAt = now, AuditId = auditId };
        _store.Consents[key] = revoked;
        _store.ConsentAudits.Add(new ConsentAudit(auditId, studentId, studentId, "student", "revoke",
            purpose, now, $"corr-{Guid.NewGuid():N}"[..16]));

        // 撤销即 purge 教师缓存（方案 §6.4）
        _store.PurgeTeacherCacheForStudent(studentId);
        return ToDto(revoked);
    });

    private static ConsentDto ToDto(Consent c) => new(
        c.StudentId, c.TeacherId, c.State, c.AllowTeacher, c.GrantedAt, c.RevokedAt, c.Purpose, c.AuditId, c.UpdatedAt);

    private StudentState RequireStudent(string studentId)
        => _store.Students.TryGetValue(studentId, out var s)
            ? s
            : throw new InvalidOperationException($"学生不存在: {studentId}");
}
