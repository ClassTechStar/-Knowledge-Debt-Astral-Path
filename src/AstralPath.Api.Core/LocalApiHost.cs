using Microsoft.AspNetCore.Builder;

namespace AstralPath.Api;

/// <summary>
/// 进程内嵌 API 宿主（P2 审计 M6：自 AndroidApp/EmbeddedWebApi 下沉）。
///
/// 在本进程把 AstralPath.Api 起在一个仅绑定 <c>127.0.0.1</c> 的回环端口上，
/// 供 WebView 壳（Android 内嵌 / 未来桌面内嵌形态）指向。壳只保留平台侧职责
/// （如 Android 的 APK 资产解包），宿主生命周期（启动/就绪等待/停止）统一在此。
///
/// 鉴权默认关闭：调用方是「页面与 API 同机同进程」的内嵌壳，无网络暴露面；
/// 需要开启时显式传 <c>requireAuth: true</c>（生产 CLI/容器宿主走 appsettings 默认开启）。
/// </summary>
public static class LocalApiHost
{
    private static WebApplication? _app;
    private static readonly object Gate = new();

    /// <summary>已启动的 API 根地址（形如 <c>http://127.0.0.1:5190</c>）；未启动为空串。</summary>
    public static string BaseUrl { get; private set; } = string.Empty;

    /// <summary>启动失败原因；null 表示启动成功。</summary>
    public static string? Error { get; private set; }

    /// <summary>启动内嵌 API（幂等；重复调用直接返回已启动的地址）。</summary>
    public static string Start(
        string? contentRoot = null,
        string? webRoot = null,
        string? graphPackDir = null,
        bool requireAuth = false)
    {
        lock (Gate)
        {
            if (_app is not null && !string.IsNullOrEmpty(BaseUrl)) return BaseUrl;

            if (!string.IsNullOrWhiteSpace(graphPackDir))
                Environment.SetEnvironmentVariable("ASTRALPATH_GRAPH_PACK", graphPackDir);

            var port = FreeLoopbackPort();
            // 仅绑定 127.0.0.1 的内嵌壳：页面与 API 同机同进程链路，无网络暴露面。
            var urls = new List<string> { "--urls", $"http://127.0.0.1:{port}" };
            if (!requireAuth) urls.Add("Security:RequireAuth=false");

            try
            {
                _app = ApiBootstrapper.Build(
                    args: urls.ToArray(),
                    contentRoot: contentRoot,
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
            BaseUrl = string.Empty;
        }
    }

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
