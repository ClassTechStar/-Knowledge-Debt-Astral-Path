using AstralPath.Core.Profiling;
using Xunit;

namespace AstralPath.Mobile.Tests;

/// <summary>画像 v2：校准 / 特征 / 风格轴 / 主题 / EMA / k-匿名 / 隐私噪声。</summary>
public class UserProfilerTests
{
    private static AttemptSignal A(int conf, bool ok, int diff = 3, long ms = 8000, int hints = 0, string kp = "N1")
        => new(DateTime.UtcNow, kp, ok, conf, ms, diff, hints);

    [Fact]
    public void UP01_Brier_PerfectCalibration_Low()
    {
        var perfect = new[] { A(5, true), A(1, false), A(5, true), A(1, false) };
        Assert.True(UserProfiler.Brier(perfect) < 0.05);
        var bad = new[] { A(5, false), A(1, true), A(5, false), A(1, true) };
        Assert.True(UserProfiler.Brier(bad) > 0.8);
    }

    [Fact]
    public void UP02_CalibrationBias_Overconfident()
    {
        var over = new[] { A(5, false), A(5, false), A(4, true) };
        Assert.True(UserProfiler.CalibrationBias(over) > 0.3);
    }

    [Fact]
    public void UP03_Features_VolatilityAndPace()
    {
        var zig = new[] { A(3, true), A(3, false), A(3, true), A(3, false) };
        var f = UserProfiler.Extract(zig);
        Assert.Equal(0.5, f.Accuracy, 6);
        Assert.True(f.Volatility > 0.6);
        var slow = UserProfiler.Extract(new[] { A(3, true, 3, 60000), A(3, true, 3, 80000) });
        Assert.True(slow.Pace < 0.3); // 慢
    }

    [Fact]
    public void UP04_Style_CalibratedAndIndependent()
    {
        var sig = new[] { A(5, true), A(1, false), A(5, true), A(1, false), A(3, true, 4, 30000) };
        var s = UserProfiler.Style(UserProfiler.Extract(sig), sig);
        Assert.True(s.Calibrated > 0.8);
        Assert.True(s.Independent > 0.8); // 无提示
    }

    [Fact]
    public void UP05_Topics_WeightPrefersWeakInteressed()
    {
        var sig = new[]
        {
            A(2, false, 3, 5000, 0, "N1"), A(2, false, 3, 5000, 0, "N1"),
            A(5, true, 3, 5000, 0, "N2")
        };
        var mastery = new Dictionary<string, double> { ["N1"] = 0.2, ["N2"] = 0.9 };
        var topics = UserProfiler.Topics(sig, mastery);
        Assert.Equal("N1", topics[0].KpId); // 薄弱且多次
    }

    [Fact]
    public void UP06_Smooth_Ema()
    {
        var old = new UserProfiler.StyleVector(0.2, 0.2, 0.2, 0.2, 0.2);
        var neu = new UserProfiler.StyleVector(0.8, 0.8, 0.8, 0.8, 0.8);
        var mid = UserProfiler.Smooth(old, neu, 0.25);
        Assert.Equal(0.35, mid.AccuracyFirst, 6); // 0.75*0.2 + 0.25*0.8
    }

    [Fact]
    public void UP07_TeacherView_KAnonAndBanned()
    {
        var tags = new[]
        {
            new UserProfiler.TagOut("稳健型", "style", 0.9),
            new UserProfiler.TagOut("crisis", "crisis", 1.0), // 禁列
            new UserProfiler.TagOut("节奏快", "pace", 0.7),
            new UserProfiler.TagOut("夜猫子", "habit", 0.6)
        };
        var (vis, sup) = UserProfiler.TeacherView(tags, k: 3);
        Assert.False(sup);
        Assert.DoesNotContain(vis, t => t.Domain == "crisis");
        Assert.Equal(3, vis.Count);

        var onlyOne = new[] { new UserProfiler.TagOut("x", "style", 1) };
        var (v2, s2) = UserProfiler.TeacherView(onlyOne, 3);
        Assert.True(s2);
        Assert.Empty(v2);
    }

    [Fact]
    public void UP08_PrivacyNoise_DeterministicAndBounded()
    {
        var a = UserProfiler.PrivacyNoise(0.5, epsilon: 2.0);
        var b = UserProfiler.PrivacyNoise(0.5, epsilon: 2.0);
        Assert.Equal(a, b); // 可复现
        Assert.True(Math.Abs(a - 0.5) < 3.0);
    }

    [Fact]
    public void UP09_SoftNudge_Overconfident()
    {
        var sig = new[] { A(5, false), A(5, false), A(5, false), A(5, false), A(5, true, 3, 5000, 0, "N3") };
        var n = UserProfiler.SoftNudge(UserProfiler.Extract(sig));
        Assert.Contains("信心", n);
    }

    [Fact]
    public void UP10_BannedDomainGuard()
    {
        Assert.True(UserProfiler.IsBannedDomain("Sensitive"));
        Assert.False(UserProfiler.IsBannedDomain("pace"));
    }
}
