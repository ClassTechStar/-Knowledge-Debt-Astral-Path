using CommunityToolkit.Mvvm.ComponentModel;

namespace AstralPath.Shared.Services;

/// <summary>
/// 应用级可访问性与显示设置（方案 §12.6 / 附录 X）。
///
/// 为什么用单例而不是注入：主题/可访问性偏好是**进程级**的显示语义，
/// 两个 Shell、图谱控件、设置页都必须读到同一份值；注入一份实例只是把
/// 同一个单例包了一层。Headless 测试通过 <see cref="Reset"/> 保证用例隔离。
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    private static readonly AppSettings Instance = new();

    public static AppSettings Current => Instance;

    /// <summary>色盲安全配色（债边与掌握度改用 Okabe-Ito 三色）。</summary>
    [ObservableProperty]
    private bool _colorBlindSafe;

    /// <summary>高对比模式（提升边框与文本对比度至 ≥7:1）。</summary>
    [ObservableProperty]
    private bool _highContrast;

    /// <summary>尊重系统「减少动画」偏好（方案 附录 X）。</summary>
    [ObservableProperty]
    private bool _reduceMotion;

    /// <summary>最小触达尺寸：桌面 44px / 移动 48px（方案 附录 X.1）。</summary>
    [ObservableProperty]
    private double _touchTarget = 44;

    /// <summary>测试用：恢复默认值，避免用例间串扰。</summary>
    public void Reset()
    {
        ColorBlindSafe = false;
        HighContrast = false;
        ReduceMotion = false;
        TouchTarget = 44;
    }
}
