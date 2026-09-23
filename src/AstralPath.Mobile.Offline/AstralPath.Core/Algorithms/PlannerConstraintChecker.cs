namespace AstralPath.Core.Algorithms;

/// <summary>计划约束 K1–K5 纯函数检查。</summary>
public enum ConstraintCode
{
    K1, // 每日总时长 ≤ 预算
    K2, // 先修任务早于依赖任务
    K3, // core ≤25 / challenge ≤15
    K4, // 同一 KP 两次任务间隔 ≥ 1 天
    K5  // 所有 open 债边被覆盖或显式跳过
}

public sealed record PlanItem(int Id, int Day, string KpId, string Layer, int Minutes, string Status);

public sealed record PlanConstraintResult(bool ConstraintsChecked, IReadOnlyList<ConstraintCode> Violations);

public static class PlannerConstraintChecker
{
    public const int DefaultDayBudgetMin = 40;
    public const int CoreMinutesMax = 25;
    public const int ChallengeMinutesMax = 15;

    public static PlanConstraintResult Check(
        IReadOnlyList<PlanItem> items,
        int dayBudgetMin,
        IReadOnlyList<(string From, string To)> openDebtRefs,
        IReadOnlySet<string>? explicitlySkipped = null)
    {
        var violations = new List<ConstraintCode>();

        // K1
        foreach (var g in items.GroupBy(i => i.Day))
            if (g.Sum(i => i.Minutes) > dayBudgetMin)
                violations.Add(ConstraintCode.K1);

        // K2：先修任务日 ≤ 依赖任务日（同一计划中 from 的任务日不晚于 to 的任务日）
        foreach (var (from, to) in openDebtRefs)
        {
            var fromDays = items.Where(i => i.KpId == from).Select(i => i.Day).ToList();
            var toDays = items.Where(i => i.KpId == to).Select(i => i.Day).ToList();
            if (fromDays.Count > 0 && toDays.Count > 0 && fromDays.Min() > toDays.Min())
                violations.Add(ConstraintCode.K2);
        }

        // K3
        foreach (var i in items)
        {
            if (string.Equals(i.Layer, "core", StringComparison.OrdinalIgnoreCase) && i.Minutes > CoreMinutesMax)
                violations.Add(ConstraintCode.K3);
            if (string.Equals(i.Layer, "challenge", StringComparison.OrdinalIgnoreCase) && i.Minutes > ChallengeMinutesMax)
                violations.Add(ConstraintCode.K3);
        }

        // K4：同一 KP 两次任务间隔 ≥ 1 天（同日即间隔 0，违规）
        foreach (var g in items.GroupBy(i => i.KpId))
        {
            var days = g.Select(i => i.Day).OrderBy(d => d).ToList();
            for (var k = 1; k < days.Count; k++)
                if (days[k] - days[k - 1] < 1)
                    violations.Add(ConstraintCode.K4);
        }

        // K5
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in items)
        {
            // 任务覆盖其 KP；债边覆盖 = 任一端 KP 有任务
            foreach (var (from, to) in openDebtRefs)
                if (i.KpId == from || i.KpId == to)
                    covered.Add($"{from}->{to}");
        }
        if (explicitlySkipped is not null)
            foreach (var s in explicitlySkipped) covered.Add(s);

        foreach (var (from, to) in openDebtRefs)
        {
            var key = $"{from}->{to}";
            if (!covered.Contains(key))
                violations.Add(ConstraintCode.K5);
        }

        return new PlanConstraintResult(violations.Count == 0, violations.Distinct().ToList());
    }
}
