namespace AstralPath.Core.Formula;

/// <summary>BASELINE 权重与阈值（contract-v1.2.0 / team appendix B）。运行时不可变。</summary>
public static class FormulaWeights
{
    public const string ScoreVersion = "score-v1";
    public const string ImpactVersion = "impact-v1";
    public const string PlannerVersion = "planner-v1";
    public const string WeightVersion = "1.0.0";

    public const double ScoreWAcc = 0.6;
    public const double ScoreWSev = 0.3;
    public const double ScoreWConf = 0.1;

    public const double DebtScorePMax = 40.0;
    public const double DebtScoreCMax = 50.0;

    public const int DefaultDayBudgetMin = 35;
    public const int DefaultHorizonDays = 14;
    public const int DefaultTopN = 5;

    public const double SaleAccThreshold = 0.7;
    public const int SaleConfThreshold = 3;
    public const int SaleStreakRequired = 2;

    public const double CoursePriorityDefault = 0.5;
    public const double ExamProximityDefault = 0.3;

    public const double Tolerance = 1e-6;

    public static readonly string[] BannedWords =
    {
        "不适合", "太笨", "比别人差", "没救", "别学了", "处分",
        "废物", "无可救药", "智商"
    };
}
