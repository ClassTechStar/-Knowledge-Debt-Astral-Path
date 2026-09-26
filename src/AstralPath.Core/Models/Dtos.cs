namespace AstralPath.Core.Models;

/// <summary>三端共享 DTO / 状态枚举（禁止手机端特化）。</summary>
public sealed record KpNode(string KpId, string Title, string Course, int Difficulty);

public sealed record KpEdge(string FromKp, string ToKp, string EdgeType, double Weight);

public sealed record MasteryRow(string KpId, double Raw, double AgeDays, int Streak, double Score);

public sealed record DebtEdgeRow(string FromKp, string ToKp, double Impact, string Status, int Streak);

public sealed record QuestionRow(
    string Id,
    string KpId,
    string Stem,
    string AnswerKind,
    string CorrectPayload,
    int Difficulty);

public sealed record AttemptRow(
    string Id,
    string KpId,
    bool Correct,
    int SelfConf,
    DateTime CreatedAt);
