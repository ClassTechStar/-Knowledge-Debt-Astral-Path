using AstralPath.Shared.Services;
using AstralPath.Shared.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AstralPath.Desktop.Tests;

/// <summary>
/// Headless 测试用的应用宿主：只注册主题样式与 VM→View 解析器，
/// 与生产壳（Desktop / Mobile）共用 <c>AstralPath.Shared</c> 的资源。
/// </summary>
public partial class TestApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lt)
            lt.MainWindow = new ShellWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
