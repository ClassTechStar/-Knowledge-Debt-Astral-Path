namespace AstralPath.Core.Resources;

/// <summary>错误码 → 用户可读文案（三端共用，手机端禁止自创文案）。</summary>
public static class ErrorCatalog
{
    public const string ConsentRequired = "error.consent_required";
    public const string GraphMissing = "error.graph_missing";
    public const string QuestionMissing = "error.question_missing";
    public const string PlanInvalid = "error.plan_invalid";
    public const string SaleBlocked = "error.sale_blocked";
    public const string BannedContent = "error.banned_content";
    public const string StorageFailed = "error.storage_failed";
    public const string ExportFailed = "error.export_failed";
    public const string Unknown = "error.unknown";

    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        [ConsentRequired] = "未获得授权",
        [GraphMissing] = "图包未加载",
        [QuestionMissing] = "题目不存在",
        [PlanInvalid] = "计划未通过 K1–K5 约束",
        [SaleBlocked] = "销账条件未满足",
        [BannedContent] = "内容已按规范降级展示",
        [StorageFailed] = "本地存储失败",
        [ExportFailed] = "导出失败",
        [Unknown] = "发生未知错误"
    };

    public static IReadOnlyCollection<string> Codes => Messages.Keys;

    public static string GetMessage(string code)
        => Messages.TryGetValue(code, out var msg) ? msg : Messages[Unknown];

    public static bool TryGetMessage(string code, out string message)
    {
        if (Messages.TryGetValue(code, out var m)) { message = m; return true; }
        message = Messages[Unknown];
        return false;
    }
}
