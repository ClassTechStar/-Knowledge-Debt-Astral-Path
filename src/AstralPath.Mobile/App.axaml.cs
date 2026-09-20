using AstralPath.Shared.Services;
using AstralPath.Shared.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AstralPath.Mobile;

/// <summary>
/// 移动端应用入口（方案 §13）。
///
/// 生命周期适配：
/// <list type="bullet">
///   <item>Android（<c>-p:EnableAndroidHead=true</c>）：<see cref="ISingleViewApplicationLifetime"/>，
///         由 <c>Android/MainActivity</c> 承载；</item>
///   <item>默认 TFM（net10.0，无 Android 头）：用桌面生命周期打开一个手机尺寸窗口，
///         便于本机验证与 Headless 测试。</item>
/// </list>
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 移动端触达下限 48px（方案 附录 X.1）
        var (shell, _) = Composition.CreateShell(touchTarget: 48);

        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single:
                single.MainView = new Views.MobileShell { DataContext = shell };
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new Window
                {
                    Title = "知债：星穹学途（手机形态预览）",
                    Width = 390,
                    Height = 844,
                    MinWidth = 320,
                    MinHeight = 480,
                    Content = new Views.MobileShell { DataContext = shell }
                };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
