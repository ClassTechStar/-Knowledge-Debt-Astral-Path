using System.Globalization;
using Android.Content;
using Android.Content.Res;
using AstralPath.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AstralPath.AndroidApp;

/// <summary>
/// 内嵌的 AstralPath.Api 宿主（方案 §13 / §14）。
///
/// 与桌面壳的关系：Windows 版是「起一个 API 子进程 + WebView2 指向它」，
/// Android 版做不到有独立可执行文件可起，因此改为「把同一份 API 跑在本进程内」，
/// 再用系统 WebView 指向它。两者加载的是同一份 <c>wwwroot/index.html</c> 与同一批控制器，
/// 界面与数据不存在第二套实现。
///
/// 资源布局（全部解包到 <see cref="Context.FilesDir"/>，随 APK 版本号增量更新）：
/// <code>
///   {FilesDir}/webroot/                     → Kestrel UseWebRoot（与 Web 端逐字节一致的 index.html）
///   {FilesDir}/graph-packs/accounting-v1/   → ASTRALPATH_GRAPH_PACK（图包）
///   {FilesDir}/eval/textbook-questions.json → 教材题库（TextbookQuestionBank 的 CWD 探测）
///   {FilesDir}/tools/ocr_pipeline.py        → OCR 脚本（仅在装有 Python 的宿主上有意义）
/// </code>
/// 之所以解包到文件系统而不是直接从 APK 资产读：ASP.NET 的静态文件与图包加载都基于
/// <see cref="IFileProvider"/> / 文件路径，用 <c>PhysicalFileProvider</c> 指向真实目录
/// 比自建 AssetFileProvider 更少代码、也更贴近 Web 端行为。
/// </summary>
public static class EmbeddedWebApi
{
    /// <summary>资源布局版本号：改了资源结构就 +1，触发全量重新解包。</summary>
    private const string AssetLayoutVersion = "1";

    private const string MarkerFile = ".astral-assets.version";

    private static WebApplication? _app;
    private static readonly object Gate = new();

    /// <summary>已启动的 API 根地址（形如 <c>http://127.0.0.1:5190</c>）。</summary>
    public static string BaseUrl { get; private set; } = string.Empty;

    /// <summary>启动失败原因；null 表示启动成功。</summary>
    public static string? Error { get; private set; }

    /// <summary>本次运行是否为首次解包（用于在状态栏展示「正在准备资源」）。</summary>
    public static bool ExtractedOnThisRun { get; private set; }

    /// <summary>
    /// 把 APK 内的 Web UI、图包、题库解包到应用私有目录，并让 Core 的
    /// 「当前工作目录」探测链生效（AstralPathStore / TextbookQuestionBank 都用它）。
    /// </summary>
    public static void PrepareAssets(Context context)
    {
        var filesDir = context.FilesDir!.AbsolutePath;
        var versionFile = Path.Combine(filesDir, MarkerFile);
        var currentVersion = $"{AssetLayoutVersion}|{GetApkStamp(context)}";

        if (File.Exists(versionFile) && File.ReadAllText(versionFile).Trim() == currentVersion)
        {
            Directory.SetCurrentDirectory(filesDir);
            return;
        }

        // 版本不匹配：全量重解，保证「升级 APK → 资源一定跟着升级」
        if (Directory.Exists(filesDir))
        {
            foreach (var name in new[] { "webroot", "graph-packs", "eval", "tools" })
            {
                var dir = Path.Combine(filesDir, name);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        ExtractAssetTree(context.Assets, filesDir, prefix: "");

        File.WriteAllText(versionFile, currentVersion);
        ExtractedOnThisRun = true;

        // Core 的资源解析同时探测运行目录；把它指到解包根，GUI/CLI 两条链路都受益
        Directory.SetCurrentDirectory(filesDir);
    }

    /// <summary>启动内嵌 API（幂等；重复调用直接返回已启动的地址）。</summary>
    public static string Start()
    {
        lock (Gate)
        {
            if (_app is not null && !string.IsNullOrEmpty(BaseUrl)) return BaseUrl;

            var webRoot = FirstExistingDirectory(
                Path.Combine(Directory.GetCurrentDirectory(), "webroot"),
                Path.Combine(AppContext.BaseDirectory, "webroot"));
            var graphPack = FirstExistingDirectory(
                Path.Combine(Directory.GetCurrentDirectory(), "graph-packs", "accounting-v1"),
                Path.Combine(AppContext.BaseDirectory, "graph-packs", "accounting-v1"));

            if (webRoot is null)
            {
                Error = "资源解包失败：找不到 webroot（应用界面文件缺失，请重新安装）。";
                return string.Empty;
            }

            if (graphPack is not null)
                Environment.SetEnvironmentVariable("ASTRALPATH_GRAPH_PACK", graphPack);

            var port = FreeLoopbackPort();
            var urls = new[] { "--urls", $"http://127.0.0.1:{port}" };

            try
            {
                _app = ApiBootstrapper.Build(
                    args: urls,
                    contentRoot: Directory.GetCurrentDirectory(),
                    webRoot: webRoot);

                // 前台同步启动：Kestrel 绑定回环是亚秒级操作，阻塞可换来更简单的错误处理
                _app.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Error = $"本地服务启动失败：{ex.Message}";
                return string.Empty;
            }

            BaseUrl = $"http://127.0.0.1:{port}";
            Error = null;
            return BaseUrl;
        }
    }

    /// <summary>等待 /health/ready 就绪（图包加载与首次编译需要一点时间）。</summary>
    public static async Task<bool> WaitReadyAsync(TimeSpan timeout)
    {
        if (string.IsNullOrEmpty(BaseUrl)) return false;

        using var http = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3),
            BaseAddress = new Uri(BaseUrl)
        };

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await http.GetAsync("/health/ready");
                if (resp.IsSuccessStatusCode) return true;
            }
            catch
            {
                // 服务可能还在绑定，继续等
            }
            await Task.Delay(300);
        }
        return false;
    }

    /// <summary>释放内嵌宿主。</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (_app is null) return;
            try
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _app.StopAsync(cts.Token).GetAwaiter().GetResult();
                _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // 进程即将退出时失败也无所谓
            }
            _app = null;
        }
    }

    // ── 内部工具 ───────────────────────────────────────────────

    private static void ExtractAssetTree(AssetManager assets, string targetDir, string prefix)
    {
        var names = assets.List(prefix) ?? Array.Empty<string>();
        foreach (var name in names)
        {
            var assetPath = string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";
            var targetPath = Path.Combine(targetDir, prefix, name);

            if (IsDirectory(assets, assetPath))
            {
                Directory.CreateDirectory(targetPath);
                ExtractAssetTree(assets, targetDir, assetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using (var input = assets.Open(assetPath))
            using (var output = File.Create(targetPath))
            {
                input.CopyTo(output);
            }
        }
    }

    /// <summary>
    /// AssetManager 不区分「空目录」与「空文件」：用能否打开流来判定。
    /// 这是 Android 资源遍历的标准做法。
    /// </summary>
    private static bool IsDirectory(AssetManager assets, string path)
    {
        try
        {
            using var _ = assets.Open(path);
            return false;
        }
        catch (Java.IO.FileNotFoundException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string GetApkStamp(Context context)
    {
        try
        {
            var ai = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!;
            return ai.LastUpdateTime.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return "unknown";
        }
    }

    private static string? FirstExistingDirectory(params string[] candidates)
        => candidates.FirstOrDefault(Directory.Exists);

    private static int FreeLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
