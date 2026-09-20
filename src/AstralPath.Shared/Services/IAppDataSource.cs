using AstralPath.Contracts;

namespace AstralPath.Shared.Services;

/// <summary>
/// 客户端数据源抽象（方案 §11.2 / §16）。
///
/// 两个实现：
/// <list type="bullet">
///   <item><see cref="OfflineDemoDataSource"/> —— 进程内，基于 <c>AstralPathStore</c> + Core 纯函数，
///         零外部依赖即可跑通演示与 Headless 测试（默认）。</item>
///   <item><see cref="ApiAppDataSource"/> —— 走 HTTP 契约访问 <c>AstralPath.Api</c>，
///         与 §4 接口目录一致，用于真机联调。</item>
/// </list>
/// 视图模型只依赖本接口，因此两种模式对 UI 完全透明。
/// </summary>
public interface IAppDataSource
{
    /// <summary>数据源模式标签，用于顶栏显示（如「离线演示」/「API」）。</summary>
    string ModeLabel { get; }

    // ── 学生与图 ───────────────────────────────────────────────
    IReadOnlyList<StudentSummary> Students { get; }

    GraphViewDto GetGraphView(string studentId);

    DiagnoseResponse Diagnose(string studentId, int topN);

    IReadOnlyList<MasteryRow> GetMastery(string studentId);

    // ── 计划 / 今日 / 练习 ─────────────────────────────────────
    PlanDto CreatePlan(string studentId, int dayBudgetMin = 35, int horizonDays = 14);

    PlanDto? GetPlan(string studentId);

    TodayResponse GetToday(string studentId, int? day = null);

    AttemptDto SubmitAttempt(CreateAttemptRequest request);

    SaleCheckResponse SaleCheck(string studentId, string fromKp, string toKp);

    // ── What-if ───────────────────────────────────────────────
    WhatIfResponse WhatIf(WhatIfRequest request);

    // ── 教师端与 consent（fail-closed） ─────────────────────────
    HotspotsResponse GetHotspots(string teacherId);

    ConsentDto? GetConsent(string studentId, string teacherId, string purpose);

    ConsentDto Grant(string studentId, string teacherId, string purpose);

    ConsentDto Revoke(string studentId, string teacherId, string purpose);

    // ── §44 受约束智能体 ───────────────────────────────────────
    AgentTurnResult AgentTurn(string userId, string role, string utterance, string? sessionId);

    IReadOnlyList<AgentToolRow> AgentTools(string role);

    // ── §45 知识库 ────────────────────────────────────────────
    IReadOnlyList<KbDocRow> ListKbDocuments();

    IReadOnlyList<KbHitRow> KbSearch(string userId, string role, string query);

    KbDocRow CreateKbDocument(string title, string ownerUserId, string visibility, string text);

    // ── §46 用户画像 ──────────────────────────────────────────
    ProfileOverviewRow? GetProfile(string studentId, bool teacherSide);

    ProfileRadarRow? GetProfileRadar(string studentId, bool teacherSide);

    ProfileTagRow[] GetProfileTags(string studentId, bool teacherSide);

    ProfileTimelineRow[] GetProfileTimeline(string studentId, bool teacherSide);

    ProfileOverviewRow? SetProfileOptOut(string studentId, bool optOut);
}

// ── 客户端侧强类型行记录（把模块的匿名返回规范化，便于 XAML 编译绑定） ──

public sealed record StudentSummary(string StudentId, string DisplayName, string DemoGroup, int OpenDebtCount, double AvgScore);

public sealed record MasteryRow(string KpId, string Name, double RecentAcc, double Sev, double SelfConf, double Score, string Band);

public sealed record AgentToolRow(string IntentId, string Description, string RoleMask, string[] RequiredSlots, int AnchorCount);

public sealed record AgentTurnResult(
    string SessionId, string IntentId, double Score, string Decision, string Text,
    string Trace, string[] MissingSlots, string Phase, bool Crisis);

public sealed record KbDocRow(string DocumentId, string Title, string Visibility, string Status, int CurrentVersion, string[] Tags, string OwnerUserId);

public sealed record KbHitRow(string DocumentId, string ChunkId, string Title, string Snippet, double Score, string Visibility);

public sealed record ProfileOverviewRow(string StudentId, bool OptOut, int SampleCount, string Summary);

public sealed record ProfileAxis(string Axis, double Value);

public sealed record ProfileRadarRow(string StudentId, bool Suppressed, ProfileAxis[] Axes);

public sealed record ProfileTagRow(string Tag, string Domain, double Weight, bool OptOut);

public sealed record ProfileTimelineRow(string Date, int Attempts, double Accuracy);
