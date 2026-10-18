namespace AstralPath.Core.Agent;

public sealed record AgentIntent(
    int Bit,
    string IntentId,
    string RoleMask,
    string[] Anchors,
    string[] RequiredSlots,
    string Description);

public sealed record AgentRouteResult(
    string IntentId,
    double Score,
    string Decision, // execute | clarify | fallback | crisis.handoff
    string[] MissingSlots,
    string Trace);

/// <summary>打分明细（调试与教师侧可解释性用）。</summary>
public sealed record IntentScore(string IntentId, double Score, string Anchor);
