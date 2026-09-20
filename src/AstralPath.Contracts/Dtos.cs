namespace AstralPath.Contracts;

public static class ErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string AuthRequired = "AUTH_REQUIRED";
    public const string Forbidden = "FORBIDDEN";
    public const string ConsentRequired = "CONSENT_REQUIRED";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string GraphVersionNotFound = "GRAPH_VERSION_NOT_FOUND";
    public const string StateConflict = "STATE_CONFLICT";
    public const string SalePrecondition = "SALE_PRECONDITION";
    public const string ConstraintCheckFailed = "CONSTRAINT_CHECK_FAILED";
    public const string GraphWouldCycle = "GRAPH_WOULD_CYCLE";
    public const string IngestHealthcheckFailed = "INGEST_HEALTHCHECK_FAILED";
    public const string NarrativeSlotInvalid = "NARRATIVE_SLOT_INVALID";
    public const string ModelUnavailable = "MODEL_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";
    /// <summary>能力已登记契约但演示版尚未实现（HTTP 501），用于诚实标注延后项。</summary>
    public const string NotImplemented = "NOT_IMPLEMENTED";
}

public sealed record ApiSuccess<T>(T Data, object Meta, string TraceId);
public sealed record ApiError(string Code, string Message, object Details);
public sealed record ApiFailure(object? Data, ApiError Error, string TraceId);

public sealed record IngestScoreRowDto(string KpId, double RecentAcc, double Sev, double SelfConf);
public sealed record IngestScoresRequest(int GraphVersion, List<IngestScoreRowDto> Rows, string Source, string? IdempotencyKey = null);

public sealed record GraphViewNodeDto(string Id, string Name, string Course, double Score, string Band);
public sealed record GraphViewEdgeDto(string From, string To, double Impact, string Status, double Weight, string FromName, string ToName, double ScoreFrom, double ScoreTo, int Freq);
public sealed record GraphViewDto(
    string StudentId,
    int GraphVer,
    string WeightVer,
    List<GraphViewNodeDto> Nodes,
    List<GraphViewEdgeDto> Edges,
    int TopDebtCount,
    DateTime GeneratedAt);

public sealed record DebtTopItem(
    string FromKp,
    string ToKp,
    string FromKpName,
    string ToKpName,
    double ScoreFrom,
    double ScoreTo,
    int Freq,
    double Impact,
    string Status);

public sealed record NarrativeDto(
    string FromKp,
    string ToKp,
    string Story,
    List<string> Actions,
    string Tone,
    SafetyFlagsDto SafetyFlags,
    string? ModelVer);

public sealed record SafetyFlagsDto(bool BannedHit, bool SlotFailed, bool DegradedTemplate);

public sealed record DiagnoseResponse(
    string StudentId,
    int GraphVer,
    string WeightVer,
    FormulaInfo Formula,
    List<DebtTopItem> TopDebts,
    List<NarrativeDto> Narrative);

public sealed record FormulaInfo(string Score, string Impact)
{
    public static FormulaInfo Default { get; } = new(
        "100*(0.6*acc+0.3*sev_norm+0.1*conf/5)",
        "freq*(50-score_c)*recency*weight");
}

public sealed record PlanItemDto(
    string Id,
    string PlanId,
    string StudentId,
    int Day,
    int Ordinal,
    string KpId,
    string KpName,
    string Type,
    int Difficulty,
    int EstMin,
    string Why,
    List<string> DebtRef,
    string Status,
    DateTime CreatedAt);

public sealed record PlanDayDto(int Day, int Minutes, List<PlanItemDto> Items);

public sealed record ConstraintViolationDto(string Code, int? Day, string Message, object Detail);

public sealed record PlanDto(
    string Id,
    string StudentId,
    int GraphVersion,
    string WeightVer,
    int DayBudgetMin,
    bool ConstraintsChecked,
    List<ConstraintViolationDto> ConstraintViolations,
    string PlannerVersion,
    List<PlanDayDto> Days,
    DateTime CreatedAt,
    DateTime ExpiresAt);

public sealed record CreatePlanRequest(int GraphVersion, List<DebtRefDto>? TopDebtRefs = null, int? DayBudgetMin = null, int? HorizonDays = null, string? IdempotencyKey = null);
public sealed record DebtRefDto(string FromKp, string ToKp);

public sealed record TodayTaskDto(
    string Id,
    string KpId,
    string KpName,
    string Type,
    int Difficulty,
    int EstMin,
    string Why,
    string QuestionId,
    string Stem,
    List<string> Options,
    int CorrectIndex,
    string? PlanItemId);

public sealed record TodayResponse(
    string StudentId,
    int Day,
    DateTime Date,
    int TotalMinutes,
    string CoachMessage,
    List<TodayTaskDto> Tasks);

public sealed record CreateAttemptRequest(
    string StudentId,
    string? PlanItemId,
    string KpId,
    string QuestionId,
    bool Correct,
    int SelfConf,
    long LatencyMs = 0,
    int HintsUsed = 0,
    string? IdempotencyKey = null);

public sealed record AttemptDto(
    string Id,
    string StudentId,
    string? PlanItemId,
    string KpId,
    string QuestionId,
    string StemHash,
    bool Correct,
    int SelfConf,
    long LatencyMs,
    DateTime OccurredAt);

public sealed record SaleCheckRequest(string StudentId, string FromKp, string ToKp, int? GraphVersion = null);

public sealed record SaleCheckResponse(
    bool Cleared,
    string? Reason,
    int Streak,
    double CurrentImpact,
    string Status);

public sealed record HotspotDto(string FromKp, string ToKp, string FromKpName, string ToKpName, int StudentCount, double AvgImpact);

public sealed record HotspotsResponse(
    string TeacherId,
    string ClassId,
    int AuthorizedCount,
    bool Suppressed,
    List<HotspotDto> Hotspots,
    string EmptyReason);

public sealed record ConsentGrantRequest(string TeacherId, string Purpose, string? ActorId = null);
public sealed record ConsentRevokeRequest(string TeacherId, string Purpose, string? ActorId = null);

public sealed record ConsentDto(
    string StudentId,
    string TeacherId,
    string State,
    bool AllowTeacher,
    DateTime? GrantedAt,
    DateTime? RevokedAt,
    string Purpose,
    string AuditId,
    DateTime UpdatedAt);

public sealed record GraphValidateResultDto(bool Ok, int NodeCount, int EdgeCount, List<string> Cycles, List<string> Issues);
public sealed record GraphVersionDto(string PackId, int GraphVersion, string PublishedAt, int NodeCount, int EdgeCount, bool Ok);

public sealed record WhatIfRequest(string StudentId, string FromKp, string ToKp, double? OverrideScoreFrom, double? OverrideScoreTo, int? OverrideFreq, int? OverrideDays);
public sealed record WhatIfResponse(double ScoreFrom, double ScoreTo, int Freq, int Days, double Impact, bool Detected, double Recency);

// ── §44 受约束智能体（agent-orchestrator）────────────────────────────────
public sealed record AgentTurnRequest(string UserId, string Role, string Utterance, string? SessionId = null);
public sealed record AgentToolRequest(string Role);

// ── §45 知识库（kb-svc / kb-search-svc）──────────────────────────────────
public sealed record KbCreateDocument(
    string Title, string OwnerUserId, string? Visibility = null,
    string? CourseCode = null, string? Text = null, string[]? Tags = null);
public sealed record KbPatchDocument(
    string? Title = null, string? Visibility = null, string? CourseCode = null, string[]? Tags = null);
public sealed record KbSetTags(string[] Tags);
public sealed record KbCreateVersion(string? Text = null, string? Note = null, string? ActorId = null);
public sealed record KbPublishRequest(string? Version = null, string? ActorId = null);
public sealed record KbRollbackRequest(string Version, string? ActorId = null);
public sealed record KbSearchRequest(string Query, string? UserId = null, string? Role = null, int? TopK = null);

// ── §46 用户画像（profile-svc）───────────────────────────────────────────
public sealed record ProfileTagUpsert(string Tag, string Domain, double Weight = 1.0, bool OptOut = false, string? ActorId = null);

// ── §45 知识库：分片直传 / 片段 / 内部导入 ─────────────────────────────────
public sealed record KbUploadTicketRequest(
    string Title, string OwnerUserId, string? Visibility = null, string? CourseCode = null, int PartCount = 1);
public sealed record KbUploadCommitRequest(
    IReadOnlyList<string> Parts, string? OwnerUserId = null, string? Role = null);
public sealed record KbImportItem(
    string? Title = null, string? Visibility = null, string? CourseCode = null, string? Text = null);
public sealed record KbImportRequest(string? OwnerUserId = null, IReadOnlyList<KbImportItem>? Items = null);
