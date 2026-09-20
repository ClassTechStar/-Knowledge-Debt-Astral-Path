using AstralPath.Desktop;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(AstralPath.Desktop.Tests.TestAppBuilder))]

namespace AstralPath.Desktop.Tests;

/// <summary>
/// Headless UI 测试宿主（方案 §14.9）。
///
/// 关键点：测试用的是**真实的 Desktop App 与真实的 Shell 组合根**，
/// 而不是为测试另写的简化版本，因此「测试通过」与「应用能跑」是同一条代码路径。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}
