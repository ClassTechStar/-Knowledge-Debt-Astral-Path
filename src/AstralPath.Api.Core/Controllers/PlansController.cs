using Microsoft.AspNetCore.Mvc;
using AstralPath.Contracts;
using AstralPath.Core.Formula;
using AstralPath.Core.Planner;
using AstralPath.Infrastructure;

namespace AstralPath.Api.Controllers;

[ApiController]
public sealed class PlansController : ControllerBase
{
    private readonly IAstralPathStore _store;

    public PlansController(IAstralPathStore store) => _store = store;

    [HttpPost("/v1/students/{id}/plans")]
    public IResult Create(string id, [FromBody] CreatePlanRequest? request)
    {
        return _store.Lock(() =>
        {
            request ??= new CreatePlanRequest(_store.Graph.GraphVersion);
            if (!_store.Students.TryGetValue(id, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");
            if (request.GraphVersion != _store.Graph.GraphVersion)
                return HttpResults.Fail(404, ErrorCodes.GraphVersionNotFound, "图版本不存在");

            var budget = request.DayBudgetMin ?? FormulaWeights.DefaultDayBudgetMin;
            var horizon = request.HorizonDays ?? FormulaWeights.DefaultHorizonDays;
            if (budget <= 0 || budget > 180)
                return HttpResults.Fail(400, ErrorCodes.ValidationError, "dayBudgetMin 非法");
            if (horizon <= 0 || horizon > 30)
                return HttpResults.Fail(400, ErrorCodes.ValidationError, "horizonDays 非法");

            if (student.DebtEdges.Count == 0)
                _store.ScanDebts(id);

            var debts = student.DebtEdges
                .Where(d => d.Status is "open" or "repairing" || d.Impact > 0)
                .OrderByDescending(d => d.Impact)
                .ToList();

            var topDebts = request.TopDebtRefs is { Count: > 0 }
                ? request.TopDebtRefs
                : debts.Take(FormulaWeights.DefaultTopN)
                    .Select(d => new DebtRefDto(d.FromKp, d.ToKp))
                    .ToList();

            var selected = topDebts.Count > 0
                ? debts.Where(d => topDebts.Any(t => t.FromKp == d.FromKp && t.ToKp == d.ToKp)).ToList()
                : debts.Take(FormulaWeights.DefaultTopN).ToList();
            if (selected.Count == 0)
                selected = debts.Take(FormulaWeights.DefaultTopN).ToList();

            var kpNames = _store.Graph.Nodes.ToDictionary(n => n.Id, n => n.Name, StringComparer.Ordinal);
            var inputs = selected.Select(d => new ScannedDebtInput(
                d.FromKp, d.ToKp, d.Impact, Math.Clamp((int)Math.Round(d.Impact / 8) + 6, 6, 20))).ToList();

            var coverageDebts = selected.Take(FormulaWeights.DefaultTopN).Select(d => (d.FromKp, d.ToKp)).ToList();
            var schedule = BuildBalancedPlan(inputs, kpNames, budget, horizon);
            var check = PlannerConstraintChecker.Check(schedule.Days, budget, coverageDebts);

            var attempts = 0;
            while (!check.ConstraintsChecked && attempts < 2)
            {
                var repaired = new List<PlanDayInput>();
                foreach (var day in schedule.Days)
                {
                    var items = day.Items.ToList();
                    var minutes = items.Sum(i => i.EstMin);
                    while (minutes > budget && items.Count > 0)
                    {
                        var last = items[^1];
                        items.RemoveAt(items.Count - 1);
                        minutes -= last.EstMin;
                    }
                    repaired.Add(new PlanDayInput(day.Day, items));
                }
                schedule = new PlanDaySchedule(repaired, budget);
                check = PlannerConstraintChecker.Check(repaired, budget, coverageDebts);
                attempts++;
            }

            if (!check.ConstraintsChecked && check.Violations.All(v => v.Code == "K5"))
            {
                var days = schedule.Days.ToList();
                var covered = new HashSet<string>(StringComparer.Ordinal);
                foreach (var day in days)
                foreach (var item in day.Items)
                {
                    if (item.DebtRef.Count >= 2)
                        covered.Add($"{item.DebtRef[0]}->{item.DebtRef[1]}");
                }

                var extra = new List<PlanItemInput>();
                foreach (var (fromKp, toKp) in coverageDebts)
                {
                    var key = $"{fromKp}->{toKp}";
                    if (covered.Contains(key)) continue;
                    var fromName = kpNames.GetValueOrDefault(fromKp, fromKp);
                    var toName = kpNames.GetValueOrDefault(toKp, toKp);
                    extra.Add(new PlanItemInput(
                        Guid.NewGuid().ToString("N"), fromKp, fromName, "review", 1, 1,
                        $"为还 {fromName} → {toName} 的债（时长受限，已降级为复习提示）",
                        new[] { fromKp, toKp }, fromName, "dropped"));
                    covered.Add(key);
                }

                if (extra.Count > 0)
                {
                    if (days.Count == 0)
                        days.Add(new PlanDayInput(1, extra));
                    else
                        days[^1] = days[^1] with { Items = days[^1].Items.Concat(extra).ToList() };
                    schedule = new PlanDaySchedule(days, budget);
                    check = PlannerConstraintChecker.Check(schedule.Days, budget, coverageDebts);
                }
            }

            if (!check.ConstraintsChecked)
            {
                return HttpResults.Fail(422, ErrorCodes.ConstraintCheckFailed, "14 天计划违反 K1–K5", new
                {
                    violations = check.Violations.Select(v => new { code = v.Code, day = v.Day, message = v.Message, detail = v.Detail }).ToList(),
                    rebalanceAttempts = 2,
                    fallback = "DEGRADED_MANUAL_LIST"
                });
            }

            var planId = Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow;
            var dayDtos = new List<PlanDayDto>();
            foreach (var day in schedule.Days)
            {
                var items = new List<PlanItemDto>();
                var ordinal = 0;
                foreach (var item in day.Items)
                {
                    items.Add(new PlanItemDto(
                        string.IsNullOrEmpty(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                        planId,
                        id,
                        day.Day,
                        ordinal++,
                        item.KpId,
                        item.KpName,
                        item.Type,
                        item.Difficulty,
                        item.EstMin,
                        item.Why,
                        item.DebtRef.ToList(),
                        item.Status == "dropped" ? "dropped" : "pending",
                        now));
                }
                dayDtos.Add(new PlanDayDto(day.Day, items.Where(i => i.Status != "dropped").Sum(i => i.EstMin), items));
            }

            foreach (var debt in selected)
            {
                var idx = student.DebtEdges.FindIndex(d => d.FromKp == debt.FromKp && d.ToKp == debt.ToKp);
                if (idx >= 0)
                {
                    var old = student.DebtEdges[idx];
                    student.DebtEdges[idx] = old with { Status = "repairing", UpdatedAt = now };
                }
            }

            var plan = new PlanDto(
                planId,
                id,
                _store.Graph.GraphVersion,
                FormulaWeights.WeightVersion,
                budget,
                true,
                new List<ConstraintViolationDto>(),
                FormulaWeights.PlannerVersion,
                dayDtos,
                now,
                now.AddDays(horizon));

            student.ActivePlan = new PlanDtoHolder
            {
                Id = planId,
                StudentId = id,
                GraphVersion = _store.Graph.GraphVersion,
                DayBudgetMin = budget,
                ConstraintsChecked = true,
                Violations = Array.Empty<ConstraintViolation>(),
                Days = schedule.Days,
                CreatedAt = now,
                ExpiresAt = now.AddDays(horizon)
            };

            return HttpResults.Created(plan);
        });
    }

    [HttpGet("/v1/plans/{id}")]
    public IResult Get(string id)
    {
        return _store.Lock(() =>
        {
            var holder = _store.Students.Values
                .Select(s => s.ActivePlan)
                .FirstOrDefault(p => p != null && p.Id == id);
            if (holder == null)
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "计划不存在");

            var dayDtos = holder.Days.Select(d => new PlanDayDto(d.Day, d.Items.Sum(i => i.EstMin),
                d.Items.Select(i => new PlanItemDto(
                    i.Id, holder.Id, holder.StudentId, d.Day, 0, i.KpId, i.KpName, i.Type,
                    i.Difficulty, i.EstMin, i.Why, i.DebtRef.ToList(), "pending", holder.CreatedAt)).ToList()
            )).ToList();

            var plan = new PlanDto(holder.Id, holder.StudentId, holder.GraphVersion, FormulaWeights.WeightVersion,
                holder.DayBudgetMin, holder.ConstraintsChecked,
                holder.Violations.Select(v => new ConstraintViolationDto(v.Code, v.Day, v.Message, v.Detail)).ToList(),
                FormulaWeights.PlannerVersion, dayDtos, holder.CreatedAt, holder.ExpiresAt);
            return HttpResults.Success(plan);
        });
    }

    [HttpPost("/v1/plans/{id}/rebalance")]
    public IResult Rebalance(string id)
    {
        return _store.Lock(() =>
        {
            var student = _store.Students.Values.FirstOrDefault(s => s.ActivePlan?.Id == id);
            if (student?.ActivePlan == null)
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "计划不存在");

            var now = DateTime.UtcNow;
            var days = student.ActivePlan.Days.Select(d =>
            {
                var items = d.Items.Where(i => i.Status != "dropped").ToList();
                var minutes = items.Sum(i => i.EstMin);
                while (minutes > student.ActivePlan!.DayBudgetMin && items.Count > 0)
                {
                    minutes -= items[^1].EstMin;
                    items.RemoveAt(items.Count - 1);
                }
                return new PlanDayInput(d.Day, items);
            }).ToList();

            var coverage = student.DebtEdges.Where(d => d.Status != "cleared").Take(5)
                .Select(d => (d.FromKp, d.ToKp)).ToList();
            var check = PlannerConstraintChecker.Check(days, student.ActivePlan.DayBudgetMin, coverage);
            if (!check.ConstraintsChecked)
            {
                return HttpResults.Fail(422, ErrorCodes.ConstraintCheckFailed, "14 天计划违反 K1–K5", new
                {
                    violations = check.Violations.Select(v => new { v.Code, v.Day, v.Message }).ToList(),
                    fallback = "DEGRADED_MANUAL_LIST"
                });
            }

            student.ActivePlan = student.ActivePlan with
            {
                Days = days,
                ConstraintsChecked = true,
                Violations = Array.Empty<ConstraintViolation>()
            };

            var dayDtos = days.Select(d => new PlanDayDto(d.Day, d.Items.Sum(i => i.EstMin),
                d.Items.Select(i => new PlanItemDto(i.Id, id, student.StudentId, d.Day, 0, i.KpId, i.KpName, i.Type,
                    i.Difficulty, i.EstMin, i.Why, i.DebtRef.ToList(), "pending", now)).ToList())).ToList();

            var plan = new PlanDto(id, student.StudentId, student.ActivePlan.GraphVersion, FormulaWeights.WeightVersion,
                student.ActivePlan.DayBudgetMin, true, new List<ConstraintViolationDto>(),
                FormulaWeights.PlannerVersion, dayDtos, student.ActivePlan.CreatedAt, student.ActivePlan.ExpiresAt);
            return HttpResults.Success(plan);
        });
    }

    private static PlanDaySchedule BuildBalancedPlan(
        IReadOnlyList<ScannedDebtInput> debts,
        IReadOnlyDictionary<string, string> kpNames,
        int budget,
        int horizon)
    {
        var ranked = debts.OrderByDescending(d => d.Impact / Math.Max(1.0, d.EstMin)).ToList();
        var days = new List<List<PlanItemInput>>();
        for (var i = 0; i < Math.Max(1, horizon); i++)
            days.Add(new List<PlanItemInput>());
        var used = new int[days.Count];

        void Place(PlanItemInput item)
        {
            for (var i = 0; i < days.Count; i++)
            {
                if (used[i] + item.EstMin <= budget)
                {
                    days[i].Add(item);
                    used[i] += item.EstMin;
                    return;
                }
            }
            days[^1].Add(item with { Type = "review", Status = "dropped", EstMin = 0 });
        }

        foreach (var debt in ranked.Take(20))
        {
            var fromName = kpNames.GetValueOrDefault(debt.FromKp, debt.FromKp);
            var toName = kpNames.GetValueOrDefault(debt.ToKp, debt.ToKp);
            var why = $"为还 {fromName} → {toName} 的债";
            var debtRef = new[] { debt.FromKp, debt.ToKp };

            Place(new PlanItemInput(Guid.NewGuid().ToString("N"), debt.FromKp, fromName, "concept", 2, 8, why, debtRef, fromName));
            Place(new PlanItemInput(Guid.NewGuid().ToString("N"), debt.FromKp, fromName, "drill", 3, 6, why, debtRef, fromName));
            Place(new PlanItemInput(Guid.NewGuid().ToString("N"), debt.ToKp, toName, "quiz", 4, 6, why, debtRef, fromName));
        }

        var planDays = days
            .Select((items, i) => new PlanDayInput(i + 1, NormalizeDayOrder(items)))
            .ToList();
        return new PlanDaySchedule(planDays, budget);
    }

    /// <summary>K4：同 kp 内 concept 必须先于 drill/quiz。</summary>
    private static List<PlanItemInput> NormalizeDayOrder(List<PlanItemInput> items)
    {
        var result = new List<PlanItemInput>();
        foreach (var group in items.GroupBy(x => x.KpId, StringComparer.Ordinal))
        {
            var concepts = group.Where(x => x.Type == "concept").ToList();
            var drills = group.Where(x => x.Type == "drill").ToList();
            var quizzes = group.Where(x => x.Type == "quiz").ToList();
            var reviews = group.Where(x => x.Type == "review" || (x.Type != "concept" && x.Type != "drill" && x.Type != "quiz")).ToList();
            result.AddRange(concepts);
            result.AddRange(drills);
            result.AddRange(quizzes);
            result.AddRange(reviews);
        }
        return result;
    }
}
