namespace AstralPath.Core.Models;

public enum MasteryBand { Green, Yellow, Red }
public enum DebtStatus { Open, Repairing, Cleared, Dropped }
public enum PlanItemType { Concept, Drill, Quiz, Review }
public enum ConsentState { Granted, Revoked, None }

public sealed record MasteryRecord(
    string StudentId,
    string KpId,
    int GraphVersion,
    double RecentAcc,
    double Sev,
    double SelfConf,
    double Score,
    string Band,
    int AttemptCount,
    DateTime? LastAttemptAt,
    string FormulaVersion,
    DateTime UpdatedAt);

public sealed record DebtEdge(
    string StudentId,
    string FromKp,
    string ToKp,
    string FromKpName,
    string ToKpName,
    int GraphVersion,
    string WeightVer,
    double ScoreFrom,
    double ScoreTo,
    int Freq,
    int DaysSinceLastError,
    double Recency,
    double Weight,
    double Impact,
    string Status,
    int SaleStreak,
    DateTime DetectedAt,
    DateTime UpdatedAt,
    string FormulaVersion);

public sealed record Attempt(
    string Id,
    string StudentId,
    string? PlanItemId,
    string KpId,
    string QuestionId,
    string StemHash,
    string AnswerKind,
    bool Correct,
    int SelfConf,
    long LatencyMs,
    int HintsUsed,
    DateTime OccurredAt,
    DateTime CreatedAt);

public sealed record Consent(
    string StudentId,
    string TeacherId,
    string State,
    bool AllowTeacher,
    DateTime? GrantedAt,
    DateTime? RevokedAt,
    string Purpose,
    string AuditId,
    DateTime UpdatedAt);

public sealed record ConsentAudit(
    string Id,
    string StudentId,
    string ActorId,
    string ActorRole,
    string Action,
    string Purpose,
    DateTime OccurredAt,
    string CorrelationId);
