using AstralPath.Shared.Services;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(AstralPath.Desktop.Tests.TestAppBuilder))]

namespace AstralPath.Desktop.Tests;

/// <summary>
/// Headless UI 测试宿主（方案 §14.9）。
///
/// 关键点：测试跑的是 <see cref="ShellWindow"/>（与桌面壳同构的最小宿主）+ 真实的
/// Shell 组合根（<see cref="Composition"/>）与 <c>AstralPath.Shared</c> 的真实视图，
/// 因此「测试通过」与「应用能跑」是同一条代码路径。
///
/// 为什么壳放在测试项目里而不是引用 <c>AstralPath.Desktop</c>：
/// 生产桌面壳已改为 WinForms + WebView2（net10.0-windows，见 <c>AstralPath.Desktop</c>），
/// 不再编译 Avalonia 窗口；而 Headless 测试需要跨平台可运行的 net10.0 宿主。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}
