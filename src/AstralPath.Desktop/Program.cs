using AstralPath.Desktop.Demo;
using Avalonia;

namespace AstralPath.Desktop;

/// <summary>
/// 桌面端入口（方案 §12）。
///
/// 两种模式：
/// <list type="bullet">
///   <item>默认：启动 Avalonia GUI（<c>AstralPath.Desktop</c>）；</item>
///   <item><c>--demo</c>：运行控制台复算自检（§12「手算 = API = 端上显示」），
///         成功返回 0、失败返回 1，供 CI 与人工快速验证。</item>
/// </list>
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            return DemoRunner.Run(args);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>供 Avalonia 设计器与 Headless 测试复用。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
