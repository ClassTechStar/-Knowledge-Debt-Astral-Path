using AstralPath.Core.Formula;

namespace AstralPath.Core.Planner;

public sealed record PlanItemInput(
    string Id,
    string KpId,
    string KpName,
    string Type, // concept | drill | quiz | review
    int Difficulty,
    int EstMin,
    string Why,
    IReadOnlyList<string> DebtRef,
    string? FromKpName = null,
    string Status = "pending");

public sealed record PlanDayInput(int Day, IReadOnlyList<PlanItemInput> Items);

public sealed record ConstraintViolation(string Code, int? Day, string Message, IReadOnlyDictionary<string, object?> Detail);

public sealed record ConstraintCheckResult(bool ConstraintsChecked, IReadOnlyList<ConstraintViolation> Violations);

/// <summary>K1–K5 约束检查器（BASELINE）。</summary>
public static class PlannerConstraintChecker
{
    public static ConstraintCheckResult Check(IReadOnlyList<PlanDayInput> days, int dayBudgetMin, IReadOnlyList<(string FromKp, string ToKp)> topDebts)
    {
        var violations = new List<ConstraintViolation>();

        // K1 daily budget
        foreach (var day in days)
        {
            var minutes = day.Items.Where(i => i.Status != "dropped").Sum(i => i.EstMin);
            if (minutes > dayBudgetMin)
            {
                violations.Add(new ConstraintViolation(
                    "K1", day.Day,
                    $"day={day.Day} minutes={minutes} > budget={dayBudgetMin}",
                    new Dictionary<string, object?> { ["minutes"] = minutes, ["budget"] = dayBudgetMin }));
            }
        }

        // K2: no 3 consecutive days on same kp with difficulty>=4
        var byDay = days.OrderBy(d => d.Day).ToList();
        for (var i = 0; i < byDay.Count - 2; i++)
        {
            var d1 = byDay[i].Items.Where(x => x.Difficulty >= 4).Select(x => x.KpId).ToHashSet();
            var d2 = byDay[i + 1].Items.Where(x => x.Difficulty >= 4).Select(x => x.KpId).ToHashSet();
            var d3 = byDay[i + 2].Items.Where(x => x.Difficulty >= 4).Select(x => x.KpId).ToHashSet();
            foreach (var kp in d1.Intersect(d2).Intersect(d3))
            {
                violations.Add(new ConstraintViolation(
                    "K2", byDay[i + 2].Day,
                    $"kp={kp} 连续 3 天 difficulty≥4",
                    new Dictionary<string, object?> { ["kpId"] = kp }));
            }
        }

        // K3: why non-empty and contains from_kp name
        foreach (var day in days)
        {
            foreach (var item in day.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Why))
                {
                    violations.Add(new ConstraintViolation("K3", day.Day, "why 为空", new Dictionary<string, object?> { ["itemId"] = item.Id }));
                    continue;
                }

                var fromName = item.FromKpName
                               ?? item.DebtRef.FirstOrDefault()
                               ?? item.KpName;
                if (!string.IsNullOrWhiteSpace(fromName) && !item.Why.Contains(fromName, StringComparison.Ordinal))
                {
                    // allow why containing the kp name of the debt source
                    var nameOk = item.DebtRef.Count == 0 || item.Why.Contains(item.KpName, StringComparison.Ordinal)
                                 || (item.FromKpName != null && item.Why.Contains(item.FromKpName, StringComparison.Ordinal));
                    if (!nameOk || !item.Why.Contains(fromName, StringComparison.Ordinal))
                    {
                        violations.Add(new ConstraintViolation(
                            "K3", day.Day,
                            "why 未包含 from_kp 名",
                            new Dictionary<string, object?> { ["itemId"] = item.Id, ["why"] = item.Why }));
                    }
                }
            }
        }

        // K4: within same kp, concept before drill/quiz
        foreach (var day in days)
        {
            var groups = day.Items.GroupBy(x => x.KpId);
            foreach (var g in groups)
            {
                var conceptIdx = g.Select((x, idx) => (x, idx)).Where(t => t.x.Type == "concept").Select(t => t.idx).DefaultIfEmpty(-1).Max();
                var drillIdx = g.Select((x, idx) => (x, idx)).Where(t => t.x.Type is "drill" or "quiz").Select(t => t.idx).DefaultIfEmpty(-1).Min();
                if (conceptIdx >= 0 && drillIdx >= 0 && drillIdx < conceptIdx)
                {
                    violations.Add(new ConstraintViolation(
                        "K4", day.Day,
                        $"kp={g.Key} concept 必须先于 drill/quiz",
                        new Dictionary<string, object?> { ["kpId"] = g.Key }));
                }
            }
        }

        // K5: cover top debts or explicit dropped reason
        var covered = new HashSet<string>();
        var dropped = new HashSet<string>();
        foreach (var day in days)
        {
            foreach (var item in day.Items)
            {
                if (item.DebtRef.Count >= 2)
                {
                    var key = $"{item.DebtRef[0]}->{item.DebtRef[1]}";
                    if (item.Status == "dropped") dropped.Add(key);
                    else covered.Add(key);
                }
            }
        }

        foreach (var debt in topDebts)
        {
            var key = $"{debt.FromKp}->{debt.ToKp}";
            if (!covered.Contains(key) && !dropped.Contains(key))
            {
                violations.Add(new ConstraintViolation(
                    "K5", null,
                    $"债边 {key} 未覆盖且无 dropped 原因",
                    new Dictionary<string, object?> { ["debt"] = key }));
            }
        }

        var ok = violations.Count == 0;
        return new ConstraintCheckResult(ok, ok ? Array.Empty<ConstraintViolation>() : violations);
    }

    public static bool TryGeneratePlan(
        IReadOnlyList<ScannedDebtInput> debts,
        IReadOnlyDictionary<string, string> kpNames,
        int dayBudgetMin,
        int horizonDays,
        out PlanDaySchedule plan,
        out ConstraintCheckResult check)
    {
        var ranked = debts
            .OrderByDescending(d => d.Impact / Math.Max(1, d.EstMin))
            .ToList();

        var days = new List<List<PlanItemInput>>();
        for (var i = 0; i < horizonDays; i++)
            days.Add(new List<PlanItemInput>());

        var dayIdx = 0;
        var dayMinutes = 0;
        var topDebts = ranked.Take(FormulaWeights.DefaultTopN)
            .Select(d => (d.FromKp, d.ToKp))
            .ToList();

        foreach (var debt in ranked)
        {
            var fromName = kpNames.GetValueOrDefault(debt.FromKp, debt.FromKp);
            var toName = kpNames.GetValueOrDefault(debt.ToKp, debt.ToKp);
            var why = $"为还 {fromName} → {toName} 的债";

            // find a day that can fit concept + drill
            var conceptMin = Math.Min(8, Math.Max(5, debt.EstMin));
            var drillMin = Math.Min(10, Math.Max(5, debt.EstMin - conceptMin));
            var quizMin = 6;
            var total = conceptMin + drillMin + quizMin;

            while (dayIdx < days.Count && dayMinutes + conceptMin > dayBudgetMin)
            {
                dayIdx++;
                dayMinutes = 0;
            }
            if (dayIdx >= days.Count) break;

            var day = dayIdx;
            days[day].Add(new PlanItemInput(
                Guid.NewGuid().ToString(), debt.FromKp, fromName, "concept", 2, conceptMin, why,
                new[] { debt.FromKp, debt.ToKp }, fromName));
            dayMinutes += conceptMin;

            while (dayIdx < days.Count && dayMinutes + drillMin > dayBudgetMin)
            {
                dayIdx++;
                dayMinutes = 0;
            }
            if (dayIdx >= days.Count) break;

            if (dayIdx == day)
            {
                days[day].Add(new PlanItemInput(
                    Guid.NewGuid().ToString(), debt.FromKp, fromName, "drill", 3, drillMin, why,
                    new[] { debt.FromKp, debt.ToKp }, fromName));
                dayMinutes += drillMin;
            }
            else
            {
                day = dayIdx;
                dayMinutes = 0;
                days[day].Add(new PlanItemInput(
                    Guid.NewGuid().ToString(), debt.FromKp, fromName, "drill", 3, drillMin, why,
                    new[] { debt.FromKp, debt.ToKp }, fromName));
                dayMinutes += drillMin;
            }

            while (dayIdx < days.Count && dayMinutes + quizMin > dayBudgetMin)
            {
                dayIdx++;
                dayMinutes = 0;
            }
            if (dayIdx >= days.Count) break;

            days[dayIdx].Add(new PlanItemInput(
                Guid.NewGuid().ToString(), debt.ToKp, toName, "quiz", 4, quizMin, why,
                new[] { debt.FromKp, debt.ToKp }, fromName));
            dayMinutes += quizMin;
        }

        var planDays = days
            .Select((items, i) => new PlanDayInput(i + 1, items))
            .Where(d => d.Items.Count > 0)
            .ToList();

        // pad remaining coverage as review days if needed
        check = Check(planDays, dayBudgetMin, topDebts);
        var attempts = 0;
        while (!check.ConstraintsChecked && attempts < 2)
        {
            // drop lowest impact overflow items
            foreach (var d in planDays)
            {
                var over = d.Items.Sum(x => x.EstMin) - dayBudgetMin;
                if (over <= 0) continue;
                var removable = d.Items.OrderByDescending(x => x.Difficulty).ToList();
                var removedMin = 0;
                var keep = new List<PlanItemInput>();
                foreach (var item in removable)
                {
                    if (removedMin < over && item.Type == "drill")
                    {
                        removedMin += item.EstMin;
                        continue;
                    }
                    keep.Add(item);
                }
                if (keep.Count != d.Items.Count)
                {
                    planDays[planDays.IndexOf(d)] = d with { Items = keep };
                }
            }
            check = Check(planDays, dayBudgetMin, topDebts);
            attempts++;
        }

        plan = new PlanDaySchedule(planDays, dayBudgetMin);
        return check.ConstraintsChecked;
    }
}

public sealed record ScannedDebtInput(string FromKp, string ToKp, double Impact, int EstMin);

public sealed record PlanDaySchedule(IReadOnlyList<PlanDayInput> Days, int DayBudgetMin);
