using AstralPath.Shared.Navigation;
using AstralPath.Shared.ViewModels;

namespace AstralPath.Shared.Services;

/// <summary>
/// 组合根（Composition Root）。两端 Shell（Desktop / Mobile）与 Headless 测试共用同一份组装，
/// 从而保证「测试里跑的」与「用户看到的」是同一套对象图（方案 §11.2）。
/// </summary>
public static class Composition
{
    /// <summary>
    /// 创建 Shell。
    /// <paramref name="touchTarget"/>：桌面 44px、移动 48px（方案 附录 X.1）。
    /// </summary>
    public static (ShellViewModel Shell, IAppDataSource Data) CreateShell(double touchTarget)
        => CreateShell(new OfflineDemoDataSource(), touchTarget);

    /// <summary>允许注入自定义数据源（Headless 测试用于构造隔离实例）。</summary>
    public static (ShellViewModel Shell, IAppDataSource Data) CreateShell(
        IAppDataSource data, double touchTarget)
    {
        var nav = new NavigationService();
        var factory = new PageFactory(data, nav);

        AppSettings.Current.TouchTarget = touchTarget;

        var shell = new ShellViewModel(data, nav, factory);
        return (shell, data);
    }
}
