using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AstralPath.Shared.Converters;

/// <summary>
/// 客户端转换器集合（方案 §12.2 / 附录 X）。
///
/// 设计原则：
/// <list type="bullet">
///   <item>颜色一律取自 <c>Styles/AppTheme.axaml</c> 的资源键，避免把色值散落在代码里；</item>
///   <item>配色**不单独表意**——债边状态同时用颜色 + 线型（见 <see cref="StatusToDashConverter"/>），
///         掌握度 band 同时用颜色 + 文字标签（见 <see cref="BandToTextConverter"/>）。</item>
/// </list>
/// </summary>
public static class AppConverters
{
    /// <summary>band（green/yellow/red）→ 色盲安全三色画刷。</summary>
    public static readonly IValueConverter BandToBrush = new FuncValueConverter<string?, IBrush?>(band =>
        Lookup(band switch
        {
            "green" => "BandHighBrush",
            "yellow" => "BandMidBrush",
            "red" => "BandLowBrush",
            _ => "PrereqNormalBrush"
        }));

    /// <summary>债边状态 → 画刷（open 红 / repairing 橙 / cleared 绿 / dropped 灰）。</summary>
    public static readonly IValueConverter StatusToBrush = new FuncValueConverter<string?, IBrush?>(status =>
        Lookup(status switch
        {
            "open" => "DebtOpenBrush",
            "repairing" => "DebtRepairingBrush",
            "cleared" => "DebtClearedBrush",
            "dropped" => "DebtDroppedBrush",
            _ => "PrereqNormalBrush"
        }));

    /// <summary>债边状态 → 线型（颜色之外的第二重编码，色盲可用）。</summary>
    public static readonly IValueConverter StatusToDash = new FuncValueConverter<string?, string?>(status =>
        status switch
        {
            "open" => "4,2",
            "repairing" => "2,2",
            "cleared" => null,
            "dropped" => "1,4",
            _ => null
        });

    /// <summary>band → 中文标签（避免只靠颜色区分掌握度）。</summary>
    public static readonly IValueConverter BandToText = new FuncValueConverter<string?, string?>(band =>
        band is null ? null : DemoMeta.BandLabel(band));

    /// <summary>bool → 取反。</summary>
    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(v => !v);

    /// <summary>bool → 显示（true 可见）。</summary>
    public static readonly IValueConverter BoolToVisible = new FuncValueConverter<bool, bool>(v => v);

    /// <summary>bool → 显示（false 可见，用于 opt-out 空态与正常态互斥）。</summary>
    public static readonly IValueConverter NotToVisible = new FuncValueConverter<bool, bool>(v => !v);

    /// <summary>字符串非空 → 可见（用于错误条、空态原因）。</summary>
    public static readonly IValueConverter TextToVisible = new FuncValueConverter<string?, bool>(
        s => !string.IsNullOrWhiteSpace(s));

    /// <summary>集合数量 &gt; 0 → 可见。</summary>
    public static readonly IValueConverter CountToVisible = new FuncValueConverter<int, bool>(c => c > 0);

    /// <summary>集合数量 == 0 → 可见（空态提示）。</summary>
    public static readonly IValueConverter EmptyToVisible = new FuncValueConverter<int, bool>(c => c == 0);

    /// <summary>0–1 比例 → 百分比文本（如 0.735 → 73.5%）。</summary>
    public static readonly IValueConverter RatioToPercent = new FuncValueConverter<double, string>(
        v => $"{Math.Clamp(v, 0, 1) * 100:0.#}%");

    /// <summary>0–1 比例 → [0,1] 的进度值（ProgressBar 用）。</summary>
    public static readonly IValueConverter RatioToProgress = new FuncValueConverter<double, double>(
        v => Math.Clamp(v, 0, 1));

    /// <summary>数值 → 是否命中（用于 What-if 检测标记）。</summary>
    public static readonly IValueConverter ImpactToVisible = new FuncValueConverter<double, bool>(v => v > 0);

    /// <summary>分钟数 → 甘特条宽度像素（默认 1 分钟 3px，可用 ConverterParameter 覆盖）。</summary>
    public static readonly IValueConverter MinToWidth = new FuncValueConverter<int, double>(min => Math.Max(6, min * 3.0));

    /// <summary>难度 1–5 → 点状强度文本（如 3 → ●●●○○）。</summary>
    public static readonly IValueConverter DifficultyToDots = new FuncValueConverter<int, string>(d =>
    {
        var n = Math.Clamp(d, 0, 5);
        return new string('●', n) + new string('○', 5 - n);
    });

    /// <summary>consent 状态 → 中文文案（fail-closed：未知状态一律按未授权描述）。</summary>
    public static readonly IValueConverter ConsentToText = new FuncValueConverter<string?, string?>(state =>
        state switch
        {
            "granted" => "已授权",
            "revoked" => "已撤销授权",
            _ => "未授权"
        });

    /// <summary>
    /// 十六进制色值 → 画刷。
    ///
    /// 存在的意义：ViewModel 的**静态字段不得持有渲染对象**——Avalonia 的画刷依赖
    /// 已初始化的渲染平台，在只跑 ViewModel 的纯单测里会在类型初始化阶段抛
    /// <c>TypeInitializationException</c>，并表现为「某个可视化莫名是空的」这种极难定位的故障。
    /// 因此 ViewModel 只给出色值字符串，由视图层（渲染平台已就绪）完成转换。
    /// </summary>
    public static readonly IValueConverter HexToBrush = new FuncValueConverter<string?, IBrush?>(hex =>
        string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out var color)
            ? null
            : new SolidColorBrush(color));

    private static IBrush? Lookup(string key)
        => Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : null;
}

/// <summary>
/// 比例 × 可用宽度 → 条宽（雷达/时间线/掌握度条用）。
/// MultiBinding 顺序：(ratio, availableWidth)。
/// </summary>
public sealed class RatioToBarWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return 0d;
        var ratio = values[0] is double d ? d : 0d;
        var width = values[1] is double w ? w : 0d;
        if (double.IsNaN(width) || width <= 0) return 0d;
        return Math.Max(0, Math.Clamp(ratio, 0, 1) * width);
    }
}
