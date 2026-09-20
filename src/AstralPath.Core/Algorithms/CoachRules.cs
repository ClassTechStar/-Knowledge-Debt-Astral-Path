namespace AstralPath.Core.Algorithms;

public sealed record CoachDecision(
    string Action, // continue | skip_easy | downgrade | insert_microcard | lighten
    string? MicroCardTopic,
    string Message,
    bool ShowMessage);

/// <summary>教练自适应规则（BASELINE）。</summary>
public static class CoachRules
{
    public static CoachDecision Decide(double recentAcc, int selfConf, int consecutiveMissDays, string kpName)
    {
        if (consecutiveMissDays >= 2)
        {
            return new CoachDecision(
                "lighten", null,
                "最近两天任务偏多，今天可以适当减负，完成最关键的一小步就好。",
                true);
        }

        if (recentAcc >= 0.9 && selfConf >= 4)
        {
            return new CoachDecision(
                "skip_easy", null,
                $"你在{kpName}上掌握得不错，可以跳过同知识点的简单题。",
                true);
        }

        if (recentAcc < 0.5)
        {
            return new CoachDecision(
                "downgrade", kpName,
                $"这道{kpName}相关题可以先降低一档难度，并插入一张概念微卡。",
                true);
        }

        return new CoachDecision("continue", null, string.Empty, false);
    }
}
