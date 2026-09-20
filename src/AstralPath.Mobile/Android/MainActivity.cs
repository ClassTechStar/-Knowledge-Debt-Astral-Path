#if ANDROID
using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace AstralPath.Mobile.Android;

/// <summary>
/// Android 宿主 Activity（方案 §13）。
///
/// 仅在 <c>-p:EnableAndroidHead=true</c> 时参与编译（见 AstralPath.Mobile.csproj）。
/// 前置条件：需先以管理员身份执行 <c>dotnet workload install wasm-tools</c>。
/// </summary>
[Activity(
    Label = "知债：星穹学途",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).WithInterFont();
}
#endif
