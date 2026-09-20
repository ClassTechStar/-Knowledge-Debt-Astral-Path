using System.Globalization;
using AstralPath.Shared.Converters;
using AstralPath.Shared.Services;
using Avalonia.Data.Converters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace AstralPath.Desktop.Tests;

/// <summary>
/// 转换器与可访问性配色（方案 §12.2 / 附录 X）。
/// 颜色需要 <c>Application.Current</c> 的资源字典，因此用 <see cref="AvaloniaFactAttribute"/>。
/// </summary>
public sealed class ConverterTests
{
    private static object? Convert(IValueConverter converter, object? value)
        => converter.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    [AvaloniaFact]
    public void 债边状态映射到方案指定的四种颜色()
    {
        Assert.Equal(Color.Parse("#E53935"), Assert.IsType<SolidColorBrush>(Convert(AppConverters.StatusToBrush, "open")).Color);
        Assert.Equal(Color.Parse("#FB8C00"), Assert.IsType<SolidColorBrush>(Convert(AppConverters.StatusToBrush, "repairing")).Color);
        Assert.Equal(Color.Parse("#43A047"), Assert.IsType<SolidColorBrush>(Convert(AppConverters.StatusToBrush, "cleared")).Color);
        Assert.Equal(Color.Parse("#9E9E9E"), Assert.IsType<SolidColorBrush>(Convert(AppConverters.StatusToBrush, "dropped")).Color);
    }

    [AvaloniaFact]
    public void 掌握度band使用色盲安全三色()
    {
        Assert.Equal(Color.Parse("#1B9E77"), Assert.IsType<SolidColorBrush>(Convert(AppConverters.BandToBrush, "green")).Color);
        Assert.Equal(Color.Parse("#D95F02"), Assert.IsType<SolidColorBrush>(Convert(AppConverters.BandToBrush, "yellow")).Color);
        Assert.Equal(Color.Parse("#7570B3"), Assert.IsType<SolidColorBrush>(Convert(AppConverters.BandToBrush, "red")).Color);
    }

    [Fact]
    public void 债边状态除颜色外还有线型编码()
    {
        // 不靠颜色单独表意：四种状态必须给出互不相同的线型（cleared 为实线 = null）
        var dashes = new[] { "open", "repairing", "cleared", "dropped" }
            .Select(s => Convert(AppConverters.StatusToDash, s) as string)
            .ToList();

        Assert.Equal(4, dashes.Distinct().Count());
        Assert.Null(dashes[2]);
    }

    [Fact]
    public void band转换为中文标签而不是只给颜色()
    {
        Assert.Equal("掌握良好", Convert(AppConverters.BandToText, "green"));
        Assert.Equal("需巩固", Convert(AppConverters.BandToText, "yellow"));
        Assert.Equal("优先修复", Convert(AppConverters.BandToText, "red"));
    }

    [Fact]
    public void 空字符串不显示_非空字符串显示()
    {
        Assert.False((bool)Convert(AppConverters.TextToVisible, "")!);
        Assert.False((bool)Convert(AppConverters.TextToVisible, "   ")!);
        Assert.False((bool)Convert(AppConverters.TextToVisible, null)!);
        Assert.True((bool)Convert(AppConverters.TextToVisible, "已撤销授权：教师视图已更新为空。")!);
    }

    [Fact]
    public void 比例格式化为百分比并裁剪到0到1()
    {
        Assert.Equal("73.5%", Convert(AppConverters.RatioToPercent, 0.735));
        Assert.Equal("100%", Convert(AppConverters.RatioToPercent, 1.4));
        Assert.Equal("0%", Convert(AppConverters.RatioToPercent, -0.2));
    }

    [Fact]
    public void 比例乘可用宽度得到条宽()
    {
        var converter = new RatioToBarWidthConverter();
        var result = converter.Convert(
            new object?[] { 0.5, 240d }, typeof(double), null, CultureInfo.InvariantCulture);

        Assert.Equal(120d, result);
    }

    [Fact]
    public void 难度转换为点状强度()
    {
        Assert.Equal("●●●○○", Convert(AppConverters.DifficultyToDots, 3));
        Assert.Equal("○○○○○", Convert(AppConverters.DifficultyToDots, 0));
    }

    [Fact]
    public void 分钟转换为甘特条宽度且不小于最小可见宽度()
    {
        Assert.Equal(24d, Convert(AppConverters.MinToWidth, 8));
        Assert.Equal(6d, Convert(AppConverters.MinToWidth, 1));
    }

    [Fact]
    public void consent状态未知时按未授权描述_fail_closed()
    {
        Assert.Equal("已授权", Convert(AppConverters.ConsentToText, "granted"));
        Assert.Equal("已撤销授权", Convert(AppConverters.ConsentToText, "revoked"));
        Assert.Equal("未授权", Convert(AppConverters.ConsentToText, "none"));
        Assert.Equal("未授权", Convert(AppConverters.ConsentToText, null));
        Assert.Equal("未授权", Convert(AppConverters.ConsentToText, "something-new"));
    }

    [Fact]
    public void 触达尺寸默认桌面四十四移动四十八()
    {
        AppSettings.Current.Reset();
        Assert.Equal(44, AppSettings.Current.TouchTarget);

        AppSettings.Current.TouchTarget = 48;
        Assert.Equal(48, AppSettings.Current.TouchTarget);

        AppSettings.Current.Reset();
        Assert.Equal(44, AppSettings.Current.TouchTarget);
    }
}
