using System.Globalization;
using Android.Content;
using Android.Content.Res;

namespace AstralPath.AndroidApp;

/// <summary>
/// Android 壳的资源准备层（P2 审计 M6 瘦身：宿主生命周期已下沉到
/// <see cref="AstralPath.Api.LocalApiHost"/>，本类只负责 Android 平台侧职责——
/// 把 APK 资产解包到应用私有目录并设定 Core 的 CWD 探测根）。
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
/// 文件路径，用 <c>PhysicalFileProvider</c> 指向真实目录比自建 AssetFileProvider
/// 更少代码、也更贴近 Web 端行为。
/// </summary>
public static class EmbeddedWebApi
{
    /// <summary>资源布局版本号：改了资源结构就 +1，触发全量重新解包。</summary>
    private const string AssetLayoutVersion = "1";

    private const string MarkerFile = ".astral-assets.version";

    /// <summary>已启动的 API 根地址（形如 <c>http://127.0.0.1:5190</c>）。委托给 LocalApiHost。</summary>
    public static string BaseUrl => AstralPath.Api.LocalApiHost.BaseUrl;

    private static string? _shellError;

    /// <summary>启动失败原因；null 表示启动成功。壳层资源错误优先于宿主错误。</summary>
    public static string? Error => _shellError ?? AstralPath.Api.LocalApiHost.Error;

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

    /// <summary>启动内嵌 API（幂等）。webRoot/图包目录由解包布局决定，委托 LocalApiHost。</summary>
    public static string Start()
    {
        _shellError = null;
        var webRoot = FirstExistingDirectory(
            Path.Combine(Directory.GetCurrentDirectory(), "webroot"),
            Path.Combine(AppContext.BaseDirectory, "webroot"));
        var graphPack = FirstExistingDirectory(
            Path.Combine(Directory.GetCurrentDirectory(), "graph-packs", "accounting-v1"),
            Path.Combine(AppContext.BaseDirectory, "graph-packs", "accounting-v1"));

        if (webRoot is null)
        {
            _shellError = "资源解包失败：找不到 webroot（应用界面文件缺失，请重新安装）。";
            return string.Empty;
        }

        return AstralPath.Api.LocalApiHost.Start(
            contentRoot: Directory.GetCurrentDirectory(),
            webRoot: webRoot,
            graphPackDir: graphPack);
    }

    /// <summary>等待 /health/ready 就绪（图包加载与首次编译需要一点时间）。</summary>
    public static Task<bool> WaitReadyAsync(TimeSpan timeout)
        => AstralPath.Api.LocalApiHost.WaitReadyAsync(timeout);

    /// <summary>释放内嵌宿主。</summary>
    public static void Stop() => AstralPath.Api.LocalApiHost.Stop();

    // ── Android 平台工具 ───────────────────────────────────────

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
}
