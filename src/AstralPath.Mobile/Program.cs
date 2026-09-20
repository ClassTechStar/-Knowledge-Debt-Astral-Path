using Avalonia;

namespace AstralPath.Mobile;

/// <summary>
/// 移动端入口（方案 §13）。
///
/// Android 打包时由 <c>Android/MainActivity</c>（<c>AvaloniaMainActivity</c>）承载，
/// 本文件不提供 <c>Main</c>；默认 TFM（net10.0）下提供 <c>Main</c>，
/// 以手机尺寸窗口预览同一套 UI，便于本机验证与截图。
/// </summary>
internal static class Program
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

#if !ANDROID
    [STAThread]
    public static int Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }
#endif
}
