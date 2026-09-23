using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Web.WebView2.WinForms;

namespace AstralPath.Desktop;

/// <summary>
/// 与 Web 端完全一致的桌面壳：
/// 启动本地 AstralPath.Api → WebView2 内嵌 http://127.0.0.1:PORT/ 的同一套 index.html。
/// 界面布局、交互、数据处理逻辑与浏览器访问 Web 端一致。
/// </summary>
public sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Panel _statusBar = new() { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(8, 0, 8, 0) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _retry = new() { Text = "重试启动", Dock = DockStyle.Right, Width = 100, Visible = false };
    private Process? _api;
    private readonly int _port;
    private readonly string _apiExe;
    private readonly string _apiWorkDir;
    private readonly string _logPath;

    public MainForm()
    {
        Text = "知债：星穹学途（Knowledge Debt: Astral Path）";
        Width = 1280;
        Height = 840;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Controls.Add(_web);
        _statusBar.Controls.Add(_retry);
        _statusBar.Controls.Add(_status);
        Controls.Add(_statusBar);
        _status.Text = "正在启动本地服务…";
        _statusBar.BackColor = Color.FromArgb(243, 244, 245);
        _retry.Click += async (_, _) =>
        {
            _retry.Visible = false;
            StopApi();
            await BootAsync();
        };

        _port = FindFreePort();
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "api", "AstralPath.Api.exe"),
            Path.Combine(baseDir, "..", "api", "AstralPath.Api.exe"),
            Path.Combine(baseDir, "AstralPath.Api.exe"),
        };
        _apiExe = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
                  ?? Path.GetFullPath(Path.Combine(baseDir, "api", "AstralPath.Api.exe"));
        _apiWorkDir = Path.GetDirectoryName(_apiExe) ?? baseDir;
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AstralPath", "api-stdout.log");

        FormClosing += (_, _) => StopApi();
        Shown += async (_, _) => await BootAsync();
    }

    private static int FindFreePort()
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)sock.LocalEndPoint!).Port;
    }

    private async Task BootAsync()
    {
        try
        {
            if (!File.Exists(_apiExe))
            {
                _status.Text = "未找到 AstralPath.Api.exe，请重新安装完整组件。";
                _retry.Visible = true;
                MessageBox.Show(this,
                    "缺少本地服务组件：\n" + _apiExe +
                    "\n\n请使用完整安装包重新安装。",
                    "知债：星穹学途", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var psi = new ProcessStartInfo
            {
                FileName = _apiExe,
                WorkingDirectory = _apiWorkDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // 命令行 --urls 优先级高于 appsettings.json，避免端口被写死覆盖
            psi.ArgumentList.Add("--urls");
            psi.ArgumentList.Add($"http://127.0.0.1:{_port}");
            psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{_port}";
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            psi.Environment["ASTRALPATH_DESKTOP"] = "1";

            _api = Process.Start(psi);
            _ = Task.Run(async () =>
            {
                try
                {
                    var so = await _api!.StandardOutput.ReadToEndAsync();
                    var se = await _api.StandardError.ReadToEndAsync();
                    await File.AppendAllTextAsync(_logPath,
                        $"[{DateTime.Now:O}] stdout:{so}\nstderr:{se}\n");
                }
                catch { /* ignore */ }
            });
            _status.Text = $"本地服务启动中 · http://127.0.0.1:{_port} · 首次启动可能需要数秒";

            var url = $"http://127.0.0.1:{_port}/";
            _status.Text = $"本地服务启动中 · http://127.0.0.1:{_port} · 正在预热全部功能…";
            var healthy = await WaitForHealthAsync(url + "health/ready", TimeSpan.FromSeconds(60));
            if (!healthy)
            {
                var dead = _api is { HasExited: true };
                var tail = ReadLogTail();
                _status.Text = dead
                    ? $"本地服务已退出（exit={_api?.ExitCode}）· 详见 {_logPath}"
                    : $"本地服务未在 60 秒内就绪 · http://127.0.0.1:{_port}";
                if (!string.IsNullOrWhiteSpace(tail))
                    _status.Text += " · " + tail.Replace('\n', ' ')[..Math.Min(80, tail.Length)];
                _retry.Visible = true;
                MessageBox.Show(this,
                    (dead ? "本地服务启动失败。\n" : "本地服务超时。\n") +
                    "地址：" + url + "\n日志：" + _logPath +
                    (string.IsNullOrWhiteSpace(tail) ? "" : "\n\n" + tail),
                    "知债：星穹学途", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                // 启动时立即拉起/确认全部服务：资料热加载、OCR、图谱、智能体
                var svcSummary = await WarmupServicesAsync(url);
                _status.Text = string.IsNullOrWhiteSpace(svcSummary)
                    ? $"全部服务已就绪 · http://127.0.0.1:{_port}/ · {DateTime.Now:HH:mm}"
                    : $"全部服务已就绪 · {svcSummary} · {DateTime.Now:HH:mm}";
            }

            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AstralPath", "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _web.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                var t = _web.CoreWebView2.DocumentTitle;
                Text = string.IsNullOrWhiteSpace(t)
                    ? "知债：星穹学途（Knowledge Debt: Astral Path）"
                    : t + " · 知债：星穹学途";
            };
            _web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess && e.HttpStatusCode == 200)
                {
                    _status.Text = $"已就绪 · Web 端同构 UI · http://127.0.0.1:{_port}/ · {DateTime.Now:HH:mm}";
                    _retry.Visible = false;
                }
                else
                {
                    _status.Text = $"页面加载失败（HTTP {e.HttpStatusCode}）· 请点「重试启动」";
                    _retry.Visible = true;
                }
            };
            _web.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            _status.Text = "启动失败：" + ex.Message;
            _retry.Visible = true;
            try { await File.AppendAllTextAsync(_logPath, $"[{DateTime.Now:O}] desktop:{ex}\n"); } catch { }
            MessageBox.Show(this, ex.ToString(), "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string ReadLogTail()
    {
        try
        {
            if (!File.Exists(_logPath)) return "";
            var all = File.ReadAllText(_logPath);
            return all.Length > 400 ? all[^400..] : all;
        }
        catch { return ""; }
    }

    private static async Task<bool> WaitForHealthAsync(string url, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                var resp = await http.GetAsync(url);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* retry */ }
            await Task.Delay(400);
        }
        return false;
    }

    /// <summary>启动时立即预热全部服务（资料/OCR/图谱/智能体），返回状态摘要。</summary>
    private static async Task<string> WarmupServicesAsync(string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            try { await http.PostAsync(baseUrl.TrimEnd('/') + "/api/services/warmup", null); } catch { /* best-effort */ }
            var json = await http.GetStringAsync(baseUrl.TrimEnd('/') + "/api/services");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("services", out var svc))
            {
                var parts = new List<string>();
                if (svc.TryGetProperty("materials", out var m) && m.TryGetProperty("count", out var mc))
                    parts.Add($"资料 {mc.GetInt32()}");
                if (svc.TryGetProperty("knowledgeGraphs", out var g) && g.TryGetProperty("count", out var gc))
                    parts.Add($"图谱 {gc.GetInt32()}");
                if (svc.TryGetProperty("ocr", out var o) && o.TryGetProperty("ok", out var ook))
                    parts.Add(ook.GetBoolean() ? "OCR 就绪" : "OCR 可选");
                if (svc.TryGetProperty("agent", out var a) && a.TryGetProperty("ok", out var aok) && aok.GetBoolean())
                    parts.Add("智能体就绪");
                return string.Join(" · ", parts);
            }
        }
        catch { /* non-fatal */ }
        return "";
    }

    private void StopApi()
    {
        try
        {
            if (_api is { HasExited: false })
            {
                _api.Kill(entireProcessTree: true);
                _api.WaitForExit(3000);
            }
        }
        catch { /* ignore */ }
        _api?.Dispose();
        _api = null;
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm());
    }
}
