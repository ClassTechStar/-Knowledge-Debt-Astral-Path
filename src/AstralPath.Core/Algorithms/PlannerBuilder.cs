using AstralPath.Core.Planner;

namespace AstralPath.Core.Algorithms;

/// <summary>
/// 计划生成 v2：拓扑约束 + 间隔重复（0/2/6 天三遍）+ 影响优先。
/// 输出满足 PlannerConstraintChecker（K1–K5）。
/// </summary>
public static class PlannerBuilder
{
    public sealed record DebtGoal(string FromKp, string ToKp, double Impact, string? FromName = null, string? ToName = null);

    /// <summary>layer：core ≤25 分，challenge ≤15 分；同一 KP 间隔 ≥1 天。</summary>
    public static IReadOnlyList<PlanItemInput> Build(
        IReadOnlyList<DebtGoal> debts,
        int horizonDays = 14,
        int dayBudget = 40)
    {
        var items = new List<PlanItemInput>();
        var id = 1;
        var dayLoad = new int[horizonDays + 2];
        var lastDay = new Dictionary<string, int>(StringComparer.Ordinal);

        bool Fits(int day, int minutes)
            => day >= 1 && day <= horizonDays && dayLoad[day] + minutes <= dayBudget;

        void Place(string kp, string name, string layer, int minutes, int preferDay, string? debtFrom, string? debtTo)
        {
            var cap = layer == "core" ? 25 : 15;
            minutes = Math.Min(minutes, cap);
            for (var d = preferDay; d <= horizonDays; d++)
            {
                if (lastDay.TryGetValue(kp, out var prev) && d - prev < 1) continue;
                if (!Fits(d, minutes)) continue;
                var debtRef = debtFrom is null || debtTo is null
                    ? Array.Empty<string>()
                    : new[] { debtFrom, debtTo };
                items.Add(new PlanItemInput(
                    id.ToString(), kp, name, layer == "core" ? "drill" : "challenge",
                    layer == "core" ? 2 : 3, minutes,
                    $"巩固「{name}」", debtRef, debtFrom, "pending"));
                dayLoad[d] += minutes;
                lastDay[kp] = d;
                id++;
                return;
            }
        }

        foreach (var goal in debts.OrderByDescending(x => x.Impact))
        {
            var fromName = goal.FromName ?? goal.FromKp;
            var toName = goal.ToName ?? goal.ToKp;
            var offsets = new[] { 0, 2, 6 };
            for (var p = 0; p < offsets.Length; p++)
            {
                var day = 1 + offsets[p];
                // 先修先练（K2）
                Place(goal.FromKp, fromName, "core", p == 0 ? 20 : 12, day, goal.FromKp, goal.ToKp);
                Place(goal.ToKp, toName, p == 0 ? "challenge" : "core", p == 0 ? 15 : 10, day + 1, goal.FromKp, goal.ToKp);
            }
        }
        return items;
    }
}
