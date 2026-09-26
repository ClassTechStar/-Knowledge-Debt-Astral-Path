using System.Diagnostics;
using System.Text.Json;
using AstralPath.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AstralPath.Monolith;

/// <summary>
/// 知债：星穹学途 · 单体版 Windows 壳（无微服务、无本地 HTTP）。
/// 直接以 file:// 加载同目录 Resources\index.html，全部业务在页面内完成。
/// 页面文本层解析失败/乱码时，通过 postMessage 桥请求本壳执行 OCR
/// （tools\ocr_pipeline.py + tesseract，均为本机文件，无网络、无端口）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public MainForm()
    {
        Text = "知债：星穹学途 · 单体版（无微服务）";
        Width = 1280;
        Height = 840;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Resources", "astralpath-icon.ico");
            if (File.Exists(ico)) Icon = new Icon(ico);
        }
        catch { /* icon optional */ }

        Controls.Add(_web);
        Shown += async (_, _) => await BootAsync();
    }

    private async Task BootAsync()
    {
        try
        {
            var html = Path.Combine(AppContext.BaseDirectory, "Resources", "index.html");
            if (!File.Exists(html))
            {
                MessageBox.Show(this, "缺少 Resources\\index.html，请重新安装完整组件。", "知债：星穹学途",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AstralPath.Monolith", "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            // ★ OCR 宿主桥：页面 postMessage({type:"astralpath.ocr",...}) → 本壳跑管线 → 回传文本
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            // 本地单体：禁止页面再依赖远程 API；脚本即全部能力
            _web.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动失败：" + ex.Message, "知债：星穹学途",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── OCR 宿主桥 ─────────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record OcrRequest(string? Type, string? Id, string? Name, string? Mode, string? Pages, string? Data);

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        OcrRequest? req;
        try { req = JsonSerializer.Deserialize<OcrRequest>(e.WebMessageAsJson, JsonOpts); }
        catch { return; } // 非 JSON 消息一律忽略
        if (req is not { Type: "astralpath.ocr" } || string.IsNullOrEmpty(req.Id) || string.IsNullOrEmpty(req.Data)) return;

        var id = req.Id;
        var name = string.IsNullOrWhiteSpace(req.Name) ? "material.pdf" : req.Name!;
        var mode = string.IsNullOrWhiteSpace(req.Mode) ? "standard" : req.Mode!;
        _ = Task.Run(() => RunOcrAndReplyAsync(id, name, mode, req.Pages, req.Data!));
    }

    private async Task RunOcrAndReplyAsync(string id, string name, string mode, string? pages, string base64)
    {
        string? text = null;
        var error = "";
        try { text = await OcrHost.RunAsync(name, mode, pages, base64); }
        catch (Exception ex) { error = ex.Message; }
        Reply(id, text, error);
    }

    private void Reply(string id, string? text, string error)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "astralpath.ocr.result",
            ["id"] = id,
            ["ok"] = !string.IsNullOrEmpty(text),
            ["text"] = text,
            ["error"] = string.IsNullOrEmpty(text)
                ? (string.IsNullOrWhiteSpace(error) ? "OCR 未返回文本" : error)
                : null
        };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        void Post()
        {
            try { _web.CoreWebView2?.PostWebMessageAsJson(json); }
            catch { /* 页面已关闭，忽略 */ }
        }
        if (InvokeRequired) BeginInvoke((Action)Post); else Post();
    }
}

