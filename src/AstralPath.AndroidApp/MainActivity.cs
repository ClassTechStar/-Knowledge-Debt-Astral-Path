using Android.App;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AndroidX.WebKit;

namespace AstralPath.AndroidApp;

/// <summary>
/// 手机独立壳：进程内启动 AstralPath.Api（EmbeddedWebApi）→ WebView 打开同一套 index.html。
/// 不依赖电脑 / adb reverse；全部核心功能（资料、识网、知债、今日、智能体、画像）在本机完成。
/// </summary>
[Activity(Label = "@string/app_name", MainLauncher = true, Theme = "@android:style/Theme.Material.Light.NoActionBar",
    ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize | Android.Content.PM.ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    private WebView? _web;
    private TextView? _status;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _status = new TextView(this)
        {
            Text = "正在启动本地服务与功能…",
            TextSize = 13
        };
        _status.SetPadding(16, 12, 16, 12);
        _status.SetBackgroundColor(Android.Graphics.Color.Argb(255, 243, 244, 245));
        _web = new WebView(this);
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
        root.AddView(_status);
        root.AddView(_web, lp);
        SetContentView(root);

        _web.Settings.JavaScriptEnabled = true;
        _web.Settings.DomStorageEnabled = true;
        _web.Settings.UseWideViewPort = true;
        _web.Settings.LoadWithOverviewMode = true;
        _web.Settings.TextZoom = 100;
        _web.Settings.MixedContentMode = MixedContentHandling.AlwaysAllow;
        _web.SetWebChromeClient(new WebChromeClient());

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                EmbeddedWebApi.PrepareAssets(this);
                var baseUrl = EmbeddedWebApi.Start();
                RunOnUiThread(() =>
                {
                    if (string.IsNullOrEmpty(baseUrl))
                    {
                        _status!.Text = "本地服务启动失败：" + (EmbeddedWebApi.Error ?? "未知错误") + "。已切换离线独立模式。";
                        LoadOfflineUi();
                        return;
                    }
                    _status!.Text = "本地服务已启动 · " + baseUrl + " · 正在预热功能…";
                });

                var ready = EmbeddedWebApi.WaitReadyAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
                RunOnUiThread(() =>
                {
                    if (!ready)
                    {
                        _status!.Text = "服务未就绪，已切换离线独立模式。";
                        LoadOfflineUi();
                        return;
                    }
                    _status!.Text = "全部服务已就绪 · 独立运行 · " + baseUrl;
                    LoadOnlineUi(baseUrl);
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    _status!.Text = "启动异常：" + ex.Message + "。已切换离线独立模式。";
                    LoadOfflineUi();
                });
            }
        });
    }

    private void LoadOnlineUi(string baseUrl)
    {
        try
        {
            var loader = new WebViewAssetLoader.Builder()
                .AddPathHandler("/assets/", new WebViewAssetLoader.AssetsPathHandler(this))
                .Build();
            _web!.SetWebViewClient(new AssetWebViewClient(loader));
            _web.LoadUrl(baseUrl.TrimEnd('/') + "/");
        }
        catch (Exception ex)
        {
            _status!.Text = "界面加载失败：" + ex.Message;
            LoadOfflineUi();
        }
    }

    private void LoadOfflineUi()
    {
        try
        {
            string html;
            using (var reader = new System.IO.StreamReader(Assets!.Open("www/index.html")))
            {
                html = reader.ReadToEnd();
            }
            if (!html.Contains("__ASTRALPATH_OFFLINE__=true") && !html.Contains("__ASTRALPATH_OFFLINE__ = true"))
            {
                var idx = html.IndexOf("<script>", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                    html = html.Insert(idx, "<script>window.__ASTRALPATH_OFFLINE__=true;</script>");
                else
                    html = "<script>window.__ASTRALPATH_OFFLINE__=true;</script>" + html;
            }
            _web!.LoadDataWithBaseURL("https://appassets.androidplatform.net/assets/www/", html, "text/html", "utf-8", null);
        }
        catch (Exception ex)
        {
            _status!.Text = "离线界面加载失败：" + ex.Message;
        }
    }

    protected override void OnDestroy()
    {
        try { EmbeddedWebApi.Stop(); } catch { /* ignore */ }
        base.OnDestroy();
    }

    private sealed class AssetWebViewClient : WebViewClient
    {
        private readonly WebViewAssetLoader _loader;
        public AssetWebViewClient(WebViewAssetLoader loader) => _loader = loader;

        public override WebResourceResponse? ShouldInterceptRequest(WebView? view, WebResourceRequest? request)
        {
            var uri = request?.Url;
            if (uri != null && uri.Host == "appassets.androidplatform.net")
                return _loader.ShouldInterceptRequest(uri!);
            return base.ShouldInterceptRequest(view, request);
        }
    }
}
