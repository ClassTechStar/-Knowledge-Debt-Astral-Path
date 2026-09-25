using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Webkit;
using AstralPath.AndroidApp;

namespace AstralPath.AndroidApp;

/// <summary>
/// 知债：星穹学途 · Android 单 Activity 壳（方案 §13 / §14）。
///
/// 与桌面壳同构：Windows 版是「本机起 AstralPath.Api + WebView2 指向它」，
/// 本壳是「进程内起同一个 API（<see cref="EmbeddedWebApi"/>）+ 系统 WebView 指向它」。
/// 两端加载同一份 wwwroot/index.html、连同一套控制器，界面与数据逻辑零分叉。
///
/// 生命周期：加载中显示状态文案；就绪后整屏 WebView。端口由系统分配，
/// URL 取自 <see cref="EmbeddedWebApi.BaseUrl"/>。旋转等配置变化不重建
/// （manifest configChanges），重建时 Start() 幂等、资源解包有版本标记，均安全。
/// </summary>
[Activity(
    Name = "com.astralpath.app.MainActivity",
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.KeyboardHidden
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.Density,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ResizeableActivity = true,
    Theme = "@style/Theme.AstralPath")]
public class MainActivity : Activity
{
    private const string Tag = "AstralPath";

    private WebView? _web;
    private TextView? _status;
    private View? _loading;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 浅色系统栏，与 Web 端底色一致
        Window?.SetStatusBarColor(Android.Graphics.Color.ParseColor("#F3F4F5"));
        Window?.SetNavigationBarColor(Android.Graphics.Color.ParseColor("#F3F4F5"));
        Window?.AddFlags(WindowManagerFlags.DrawsSystemBarBackgrounds);
        try
        {
            Window?.DecorView.SystemUiVisibility = (StatusBarVisibility)
                (SystemUiFlags.LayoutStable
                | SystemUiFlags.LightStatusBar
                | SystemUiFlags.LightNavigationBar);
        }
        catch { /* 旧系统忽略 */ }

        var root = new FrameLayout(this);
        root.SetFitsSystemWindows(true);

        // 加载中视图
        var loading = new LinearLayout(this)
        {
            Orientation = Android.Widget.Orientation.Vertical,
        };
        loading.SetGravity(GravityFlags.Center);
        var progress = new ProgressBar(this) { Indeterminate = true };
        _status = new TextView(this)
        {
            Gravity = GravityFlags.Center,
            Text = GetString(Resource.String.boot_status_starting),
            TextSize = 15,
        };
        _status.SetPadding(48, 32, 48, 0);
        _status.SetTextColor(Android.Graphics.Color.ParseColor("#475569"));
        loading.AddView(progress, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        loading.AddView(_status, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _loading = loading;
        root.AddView(loading, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // WebView：先建好并隐藏，服务就绪后填充与显示
        _web = CreateWebView();
        _web.Visibility = ViewStates.Gone;
        root.AddView(_web, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        SetContentView(root);

        // 解包资源与启动服务放后台线程：首次解包 + 图包加载可能超过 1 秒，
        // 不能阻塞主线程（否则 ANR）。EmbeddedWebApi 内部自持锁，重复调用安全。
        Task.Run(async () =>
        {
            try
            {
                EmbeddedWebApi.PrepareAssets(this);

                RunOnUiThread(() =>
                    _status?.SetText(Resource.String.boot_status_starting));

                var url = EmbeddedWebApi.Start();
                if (string.IsNullOrEmpty(url))
                {
                    var err = EmbeddedWebApi.Error ?? "未知启动错误";
                    RunOnUiThread(() => ShowFatal(err));
                    return;
                }

                var ready = await EmbeddedWebApi.WaitReadyAsync(TimeSpan.FromSeconds(90));
                if (!ready)
                {
                    RunOnUiThread(() =>
                        ShowFatal($"本地服务未在 90 秒内就绪（{url}）"));
                    return;
                }

                RunOnUiThread(() =>
                {
                    _loading?.Visibility = ViewStates.Gone;
                    _web?.Visibility = ViewStates.Visible;
                    _web?.LoadUrl(url + "/");
                });
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error(Tag, ex.ToString());
                RunOnUiThread(() => ShowFatal("启动失败：" + ex.Message));
            }
        });
    }

    private WebView CreateWebView()
    {
        var web = new WebView(this);
        var s = web.Settings;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;             // localStorage（离线兜底、会话保持）
        s.AllowFileAccess = false;
        s.AllowContentAccess = false;
        s.UseWideViewPort = true;
        s.LoadWithOverviewMode = true;
        s.SetSupportZoom(true);
        s.BuiltInZoomControls = false;
        s.TextZoom = 100;
        s.CacheMode = CacheModes.Default;
        s.MediaPlaybackRequiresUserGesture = false;
        s.BlockNetworkImage = false;
        s.MixedContentMode = MixedContentHandling.AlwaysAllow;

        web.SetLayerType(LayerType.None, null);
        web.OverScrollMode = OverScrollMode.Never;
        web.HorizontalScrollBarEnabled = false;
        web.VerticalScrollBarEnabled = false;
        web.SetBackgroundColor(Android.Graphics.Color.ParseColor("#F5F7FA"));
        web.SetWebChromeClient(new WebChromeClient());
        // 链接导航策略：本机服务在应用内打开，外链交给系统浏览器
        web.SetWebViewClient(new AppWebViewClient());
        return web;
    }

    /// <summary>仅本机（内嵌服务）在应用内打开；其余外链交给系统浏览器。</summary>
    private sealed class AppWebViewClient : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString();
            if (string.IsNullOrEmpty(url)) return false;

            if (url.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
            {
                return false;   // 本机服务：应用内加载
            }
            try
            {
                var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
                view!.Context!.StartActivity(intent);
            }
            catch { /* 无浏览器时忽略 */ }
            return true;
        }
    }

    private void ShowFatal(string message)
    {
        _loading?.Visibility = ViewStates.Visible;
        _web?.Visibility = ViewStates.Gone;
        if (_status != null)
        {
            _status.Text = message + "\n\n提示：完全退出应用（从最近任务划掉）后重新打开。";
        }
    }

    // 返回键：WebView 可后退则后退（与桌面/浏览器习惯一致）
    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back && _web is { } web && web.CanGoBack())
        {
            web.GoBack();
            return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    protected override void OnDestroy()
    {
        // 仅真正退出（而非旋转等配置变化）时停掉内嵌服务
        if (IsFinishing)
        {
            EmbeddedWebApi.Stop();
        }
        base.OnDestroy();
    }
}
