using System;
using System.Collections.Generic;
using System.Linq;
using AstralPath.Core.Agent;
using AstralPath.Core.Profiling;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// v3 优化回归：用户画像（波动归一化 / ECE / 时间衰减 / 真 k-匿名 / Laplace）
/// 与智能体（阈值启用 / 多锚点累加 / 负例修复 / 槽位收敛 / 话题切换 / 省略指代）。
/// 每条对应 v2 的一个具体缺陷。
/// </summary>
public class ProfileAgentV3Tests
{
    private static AttemptSignal A(int conf, bool ok, int diff = 3, long ms = 8000, int hints = 0, string kp = "N1", int dayOffset = 0)
        => new(DateTime.UtcNow.Date.AddDays(-dayOffset), kp, ok, conf, ms, diff, hints);

    // ── 画像 · 超额波动 ─────────────────────────────────
    [Fact]
    public void P31_ExcessVolatility_RemovesAccuracyConfound()
    {
        // A：正确率 0.5 且对错交替 —— 比随机还抖
        var zig = new[] { A(3, true), A(3, false), A(3, true), A(3, false), A(3, true), A(3, false) };
        var fa = UserProfiler.Extract(zig);
        Assert.True(fa.ExcessVolatility > 1.0, $"excess={fa.ExcessVolatility} 应 >1（比随机更抖）");

        // B：正确率 0.8，只有一次翻转 —— 比随机更稳
        var steady = new[] { A(4, true), A(4, true), A(4, true), A(4, true),
                             A(4, true), A(4, true), A(4, true), A(4, true),
                             A(4, true), A(4, false) };
        var fb = UserProfiler.Extract(steady);
        Assert.True(fb.ExcessVolatility < 1.0, $"excess={fb.ExcessVolatility} 应 <1（比随机更稳）");

        // v2 只看原始翻转率：两者分别是 1.0 与 0.111，跨正确率直接比较没有意义
        Assert.True(fa.Volatility > fb.Volatility);
    }

    // ── 画像 · ECE 揭示被 Brier 平均掉的系统偏差 ─────────
    [Fact]
    public void P32_Ece_SurfacesSystematicBias()
    {
        // 全部答对但一律只报 3 分（p=0.6）→ 系统性"低估自己"
        // Brier 只有 0.16（看起来还行），但校准曲线显示 gap = -0.4
        var under = Enumerable.Range(0, 10).Select(_ => A(3, true)).ToArray();
        var f = UserProfiler.Extract(under);
        Assert.True(f.Brier < 0.2, $"Brier={f.Brier}");
        Assert.True(f.Ece > 0.35, $"ECE={f.Ece} 应揭示系统低估");

        var curve = UserProfiler.CalibrationCurve(under);
        Assert.Single(curve);
        Assert.True(curve[0].Gap < -0.3, "该档位系统性低估");
    }

    // ── 画像 · 主题兴趣时间衰减 ─────────────────────────
    [Fact]
    public void P33_Topics_RecencyDecay_PrefersRecent()
    {
        var now = DateTime.UtcNow.Date;
        var signals = new List<AttemptSignal>();
        for (var i = 0; i < 5; i++) signals.Add(new AttemptSignal(now.AddDays(-30), "OLD", false, 3, 6000, 3));
        for (var i = 0; i < 2; i++) signals.Add(new AttemptSignal(now, "NEW", false, 3, 6000, 3));

        var mastery = new Dictionary<string, double> { ["OLD"] = 0.3, ["NEW"] = 0.3 };
        var topics = UserProfiler.Topics(signals, mastery, halfLifeDays: 14, now);

        // v2 按 n/total：OLD(5) > NEW(2)，会把一个月前的旧兴趣排第一
        Assert.Equal("NEW", topics[0].KpId);
    }

    // ── 画像 · 真 k-匿名 ────────────────────────────────
    [Fact]
    public void P34_TeacherViewAnonymized_PerTagSuppression()
    {
        var tags = new[]
        {
            new UserProfiler.TagOut("稳健型", "style", 0.9),   //  cohort 10 → 可见
            new UserProfiler.TagOut("夜猫子", "habit", 0.8),   //  cohort 2  → 抑制
            new UserProfiler.TagOut("crisis", "crisis", 1.0)   //  禁列域   → 抑制
        };
        var cohort = new Dictionary<string, int> { ["稳健型"] = 10, ["夜猫子"] = 2 };

        var (vis, suppressed) = UserProfiler.TeacherViewAnonymized(tags, cohort, k: 3);

        Assert.Single(vis);
        Assert.Equal("稳健型", vis[0].Tag);
        Assert.Contains("夜猫子", suppressed);   // 样本 <k，抑制而非隐藏全部
        Assert.Contains("crisis", suppressed);
    }

    // ── 画像 · Laplace 噪声 ─────────────────────────────
    [Fact]
    public void P35_PrivacyNoise_ValueIndependent_And_Reproducible()
    {
        const long seed = 7;
        var n1 = UserProfiler.PrivacyNoise(0.5, epsilon: 1.0, sensitivity: 1.0, seed);
        var n2 = UserProfiler.PrivacyNoise(0.5, epsilon: 1.0, sensitivity: 1.0, seed);
        var n3 = UserProfiler.PrivacyNoise(0.9, epsilon: 1.0, sensitivity: 1.0, seed);

        Assert.Equal(n1, n2);                                  // 可复现
        // v2 用 value 做哈希 → 不同值噪声不同，可被查表反推；v3 噪声与 value 无关
        Assert.Equal(n1 - 0.5, n3 - 0.9, 9);
        // 支持值域裁剪
        var clamped = UserProfiler.PrivacyNoise(0.0, epsilon: 1.0, sensitivity: 1.0, seed, min: 0.0, max: 1.0);
        Assert.InRange(clamped, 0.0, 1.0);
    }

    // ── 画像 · 软提示判据 ───────────────────────────────
    [Fact]
    public void P36_SoftNudge_UsesCalibrationBias()
    {
        // 真·过度自信：正确率低但信心高
        var over = new[] { A(5, false), A(5, false), A(5, false), A(5, false), A(5, true) };
        Assert.Contains("信心", UserProfiler.SoftNudge(UserProfiler.Extract(over)));

        // 确实会、信心也高 —— 不该被误判
        var good = new[] { A(5, true), A(5, true), A(5, true), A(5, true), A(5, true) };
        Assert.DoesNotContain("信心", UserProfiler.SoftNudge(UserProfiler.Extract(good)));

        // 连续答错有新提示
        var streak = new[] { A(3, false), A(3, false), A(3, false) };
        Assert.Contains("先修", UserProfiler.SoftNudge(UserProfiler.Extract(streak)));
    }

    [Fact]
    public void P37_Build_OneShot()
    {
        var sig = new[] { A(5, true), A(3, false), A(4, true, 4, 12000, 1, "N2") };
        var p = UserProfiler.Build(sig, new Dictionary<string, double> { ["N1"] = 0.4, ["N2"] = 0.6 });
        Assert.Equal(3, p.Features.Attempts);
        Assert.True(p.Topics.Count > 0);
        Assert.True(p.Calibration.Count > 0);
        Assert.False(string.IsNullOrWhiteSpace(p.Nudge));
    }

    // ── 智能体 ──────────────────────────────────────────
    private static AgentRouter Router() => AgentRouter.CreateDefault();

    [Fact]
    public void A31_Crisis_AlwaysWins()
    {
        var r = Router().Classify("我最近压力很大，不想活了", AgentRoles.Student);
        Assert.Equal("crisis.handoff", r.IntentId);
        Assert.Equal("crisis.handoff", r.Decision);
    }

    [Fact]
    public void A32_NegativeLexicon_DoesNotKillRealRequest()
    {
        // v2：ContainsAny("好的") 命中 → 整句 fallback，请求被吞掉
        var r = Router().Classify("好的，帮我生成计划", AgentRoles.Student);
        Assert.Equal("plan.create", r.IntentId);
        Assert.Equal("execute", r.Decision);
    }

    [Fact]
    public void A33_PureSmallTalk_StillFallsBack()
    {
        var r = Router().Classify("ok", AgentRoles.Student);
        Assert.Equal("fallback", r.Decision);
    }

    [Fact]
    public void A34_MultiAnchor_Accumulates()
    {
        // 单锚点"计划"只有 ~0.63；多锚点同时命中应显著更高
        var one = Router().Score("计划", AgentRoles.Student);
        var many = Router().Score("生成一个十四天的复习计划安排", AgentRoles.Student);
        var s1 = one.First(x => x.IntentId == "plan.create").Score;
        var s2 = many.First(x => x.IntentId == "plan.create").Score;
        Assert.True(s2 > s1, $"多锚点应加分：{s1} → {s2}");
        Assert.True(s2 >= AgentRouter.TauExec);
    }

    [Fact]
    public void A35_SlotFilling_Converges()
    {
        var intents = new[]
        {
            new AgentIntent(1, "book.search", "student", new[] { "检索", "找书" }, new[] { "书名" }, "检索书籍")
        };
        var router = new AgentRouter(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), intents);

        var ask = router.Classify("帮我检索", AgentRoles.Student, clarifyCount: 0, awaitingIntent: "book.search");
        Assert.Equal("clarify", ask.Decision);
        Assert.Contains("书名", ask.MissingSlots);

        var done = router.Classify("检索 会计基础", AgentRoles.Student, clarifyCount: 1, awaitingIntent: "book.search");
        Assert.Equal("execute", done.Decision);   // v2 永远返回 clarify，永不收敛
        Assert.Empty(done.MissingSlots);
    }

    [Fact]
    public void A36_MaxClarify_StopsNagging()
    {
        var intents = new[]
        {
            new AgentIntent(1, "book.search", "student", new[] { "检索" }, new[] { "书名" }, "检索书籍")
        };
        var router = new AgentRouter(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), intents);

        var r = router.Classify("帮我检索", AgentRoles.Student, clarifyCount: AgentRouter.MaxClarify, awaitingIntent: "book.search");
        Assert.Equal("execute", r.Decision);   // 追问次数用完，不再纠缠
    }

    [Fact]
    public void A37_TopicSwitch_Allowed()
    {
        var router = Router();
        var r = router.Classify("算了，帮我生成复习计划安排", AgentRoles.Student, clarifyCount: 0, awaitingIntent: "debt.explain");
        Assert.Equal("plan.create", r.IntentId); // 中途换话题应切换，而不是死守旧意图
    }

    [Fact]
    public void A38_RoleFilter_Respected()
    {
        var router = Router();
        // profile.optout 只允许 student/demo
        var teacher = router.Score("退出画像", AgentRoles.Teacher);
        Assert.DoesNotContain(teacher, x => x.IntentId == "profile.optout");

        var student = router.Score("退出画像", AgentRoles.Student);
        Assert.Contains(student, x => x.IntentId == "profile.optout");
    }

    [Fact]
    public void A39_Anaphora_InheritsPreviousIntent()
    {
        var router = Router();
        // "再解释一下" 单看分数只有中等，靠 prevIntent 继承才能落到 debt.explain
        var r = router.Classify("再解释一下", AgentRoles.Student, clarifyCount: 0, awaitingIntent: null, prevIntentId: "debt.explain");
        Assert.Equal("debt.explain", r.IntentId);
        Assert.Equal("execute", r.Decision);
    }

    [Fact]
    public void A40_Score_IsExplainable()
    {
        var ranked = Router().Score("帮我诊断知识债", AgentRoles.Student);
        Assert.True(ranked.Count >= 1);
        Assert.Equal("debt.diagnose", ranked[0].IntentId);
        Assert.False(string.IsNullOrWhiteSpace(ranked[0].Anchor)); // 命中的锚点可回溯
    }
}
