namespace AstralPath.Core.Formula;

/// <summary>
/// v2 公式常量：在 v1 门限模型上消除悬崖效应、补上级联/遗忘/最弱先修。
/// 数字仍全部端上计算；LLM 不得改写。
/// </summary>
public static class FormulaConstants
{
    // ── 版本 ────────────────────────────────────────────
    public const string ScoreVersion  = "score-v2";
    public const string ImpactVersion = "impact-v2";

    // ── 掌握度 score-v2 ─────────────────────────────────
    /// <summary>经验项权重（对题正误 + 信心的收缩估计）</summary>
    public const double WEmpirical   = 0.50;
    /// <summary>记忆保持项权重（Ebbinghaus 遗忘）</summary>
    public const double WRetention   = 0.30;
    /// <summary>先修支撑项权重（最弱链 + 均值）</summary>
    public const double WPrereq      = 0.20;
    /// <summary>先修上限：score ≤ 0.55 + 0.45 × min(prereq)</summary>
    public const double PrereqFloor  = 0.55;
    /// <summary>经验项先验伪计数（Jeffreys）</summary>
    public const double PriorSuccess = 0.5;
    public const double PriorFail    = 0.5;
    /// <summary>初始记忆稳定性（天）</summary>
    public const double Stability0   = 2.5;
    /// <summary>连对稳定性乘子：S = S0 × (1+α×streak) × (1+β×ln(1+reps))</summary>
    public const double StabilityAlpha = 0.45;
    public const double StabilityBeta  = 0.25;
    /// <summary>最弱先修混合：soft = (1-λ)×mean + λ×min</summary>
    public const double PrereqWeakMix = 0.65;
    /// <summary>信心归一化分母（1–5 → 0–1）</summary>
    public const double ConfScale    = 5.0;

    // ── 影响分 impact-v2 ────────────────────────────────
    public const double ImpactBase            = 0.55;
    public const double TransferGapMultiplier = 1.15;
    /// <summary>软门限中心：parent 充分 / child 薄弱</summary>
    public const double ParentScoreGate       = 0.55;
    public const double ChildScoreGate        = 0.50;
    /// <summary>Sigmoid 斜率（越大越接近原硬门限）</summary>
    public const double GateSteepness         = 8.0;
    /// <summary>命中线：impact 超过该值视为红边</summary>
    public const double ImpactHitThreshold    = 0.08;
    /// <summary>级联：1 + α × ln(1+下游节点数)</summary>
    public const double CascadeAlpha          = 0.12;
    /// <summary>错题体量：1 + β × ln(1+freq)</summary>
    public const double VolumeBeta            = 0.08;

    // ── 销账 sale-v2 ────────────────────────────────────
    /// <summary>需要达标次数（近期窗口）</summary>
    public const int    SaleStreakRequired    = 2;
    /// <summary>窗口内至少作答次数（防止两题就清账）</summary>
    public const int    SaleMinAttempts       = 3;
    /// <summary>加权达标线：0.7×acc + 0.3×(conf/5) ≥ 该值</summary>
    public const double SaleBar               = 0.65;
    public const double SaleAccMin            = 0.7;
    public const int    SaleConfMin           = 3;

    // ── 计划 ────────────────────────────────────────────
    public const int DefaultHorizonDays       = 14;
    public const int SpacingPasses            = 3;
    public static readonly int[] SpacingOffsets = { 0, 2, 6 };

    // ── 校验 ────────────────────────────────────────────
    public const double GoldenTolerance       = 1e-6;

    // 兼容 v1 字段（迁移期只读）
    public const double RecencyHalfLifeDays   = 21.0;
    public const double StabilityBonusMax     = 0.08;
    public const double CrossCourseAlpha      = 0.15;
}
