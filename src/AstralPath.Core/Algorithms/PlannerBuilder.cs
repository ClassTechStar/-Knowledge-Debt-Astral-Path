using AstralPath.Core.Formula;

namespace AstralPath.Core.Algorithms;

/// <summary>
/// 计划生成 v2：拓扑约束 + 间隔重复（0/2/6 天三遍）+ 影响优先背包。
/// 输出仍满足 K1–K5，供 PlannerConstraintChecker 校验。
/// </summary>
public static class PlannerBuilder
{
    public sealed record DebtGoal(string FromKp, string ToKp, double Impact);

    /// <summary>
    /// 1) 按 impact 降序取债边；
    /// 2) 先修 From 必须出现在 To 之前（K2）；
    /// 3) 同一 KP 按 SpacingOffsets 在 horizon 内打三遍（K4 间隔≥1）；
    /// 4) 每日预算内优先高 impact 的 core，再 challenge。
    /// </summary>
    public static IReadOnlyList<PlanItem> Build(
        IReadOnlyList<DebtGoal> debts,
        IReadOnlyDictionary<string, string> titles,
        int horizonDays = FormulaConstants.DefaultHorizonDays,
        int dayBudget = PlannerConstraintChecker.DefaultDayBudgetMin)
    {
        var items = new List<PlanItem>();
        var id = 1;
        var dayLoad = new int[horizonDays + 2];
        var lastDay = new Dictionary<string, int>(StringComparer.Ordinal);

        bool Fits(int day, int minutes)
            => day >= 1 && day <= horizonDays && dayLoad[day] + minutes <= dayBudget;

        void Place(string kp, string layer, int minutes, int preferDay)
        {
            var cap = layer == "core" ? PlannerConstraintChecker.CoreMinutesMax : PlannerConstraintChecker.ChallengeMinutesMax;
            minutes = Math.Min(minutes, cap);
            // 在 preferDay 附近找最近合法日（满足 K4 ≥1 天间隔）
            for (var d = preferDay; d <= horizonDays; d++)
            {
                if (lastDay.TryGetValue(kp, out var prev) && d - prev < 1) continue;
                if (!Fits(d, minutes)) continue;
                items.Add(new PlanItem(id++, d, kp, layer, minutes, "todo"));
                dayLoad[d] += minutes;
                lastDay[kp] = d;
                return;
            }
        }

        var ordered = debts.OrderByDescending(x => x.Impact).ToList();
        foreach (var goal in ordered)
        {
            // 三遍间隔：先修 / 目标 按 From→To 拓扑
            var passes = FormulaConstants.SpacingOffsets;
            for (var p = 0; p < passes.Length; p++)
            {
                var day = 1 + passes[p];
                var layerFrom = p == 0 ? "core" : "core";
                var layerTo = p == 0 ? "challenge" : (p == 1 ? "core" : "challenge");
                var minFrom = p == 0 ? 20 : 12;
                var minTo = p == 0 ? 15 : 10;
                Place(goal.FromKp, layerFrom, minFrom, day);
                Place(goal.ToKp, layerTo, minTo, day + 1);
            }
        }

        return items;
    }
}
