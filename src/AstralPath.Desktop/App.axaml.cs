using AstralPath.Shared.Services;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AstralPath.Desktop;

/// <summary>
/// 桌面端应用入口（方案 §12）。
///
/// 组装交给 <see cref="Composition"/>（Shared 内），默认数据源为
/// <see cref="OfflineDemoDataSource"/>（进程内、零外部依赖），
/// 因此「打开即用」，且与 Headless 测试走同一条代码路径。
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (shell, _) = Composition.CreateShell(touchTarget: 44);
            desktop.MainWindow = new Views.MainWindow { DataContext = shell };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
