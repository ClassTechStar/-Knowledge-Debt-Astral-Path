namespace AstralPath.Shared;

/// <summary>客户端共享演示元数据（Desktop/Mobile 消费 Core 时使用）。</summary>
public static class DemoMeta
{
    public const string StudentAId = "demo-student-a";
    public const string StudentBId = "demo-student-b";
    public const string TeacherId = "demo-teacher";
    public const string GraphVersion = "1";
    public const string FooterDisclaimer = "本系统仅用于教学辅助与学习规划，不构成处分依据。";

    public static string BandLabel(string band) => band switch
    {
        "green" => "掌握良好",
        "yellow" => "需巩固",
        "red" => "优先修复",
        _ => band
    };
}
