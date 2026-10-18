namespace AstralPath.Core.Agent;

/// <summary>结构化叙事装配（§47 OC-6）：模板 + 槽位，天生避开禁词。</summary>
public static class NarrativeAssembler
{
    public static string BuildDebtStory(string fromKp, string toKp, double scoreFrom, double scoreTo, double impact)
    {
        return $"你在{toKp}相关练习上多次出错，回溯显示{fromKp}掌握度为 {scoreFrom:0.##}，" +
               $"当前知识点掌握度为 {scoreTo:0.##}，影响分 {impact:0.##}。建议先巩固{fromKp}，再回到{toKp}。";
    }

    public static string BuildPlanCoach(string book, string focus, int minutes)
        => $"今天先围绕《{book}》掌握「{focus}」，合计约 {minutes} 分钟。";

    public static string Sanitize(string text, PatternSet banned)
    {
        if (!banned.ContainsAny(text)) return text;
        return "这段内容暂时无法展示，请查看教材原文或联系老师。";
    }
}
