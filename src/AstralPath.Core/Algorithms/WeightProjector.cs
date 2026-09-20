using AstralPath.Core.Formula;

namespace AstralPath.Core.Algorithms;

/// <summary>W_debt 投影：0.45·norm(impact)+0.20·recency+0.15·course_priority+0.10·exam_proximity+0.10·repair_cost_norm</summary>
public static class WeightProjector
{
    public static double Project(
        double impact,
        double maxImpact,
        double recency,
        double coursePriority = FormulaWeights.CoursePriorityDefault,
        double examProximity = FormulaWeights.ExamProximityDefault,
        double estMin = 1,
        double maxEstMin = 1)
    {
        var normImpact = maxImpact <= 0 ? 0 : impact / maxImpact;
        var repairCost = maxEstMin <= 0 ? 0 : estMin / maxEstMin;
        var w = 0.45 * normImpact
                + 0.20 * recency
                + 0.15 * coursePriority
                + 0.10 * examProximity
                + 0.10 * repairCost;
        return Math.Round(w, 6, MidpointRounding.AwayFromZero);
    }
}
