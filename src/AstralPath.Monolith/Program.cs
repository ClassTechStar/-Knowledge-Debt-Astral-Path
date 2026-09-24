using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AstralPath.Monolith;

/// <summary>
/// 知债：星穹学途 · 单体版 Windows 壳（无微服务、无本地 HTTP）。
/// 直接以 file:// 加载同目录 Resources\index.html，全部业务在页面内完成。
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
            // 本地单体：禁止页面再依赖远程 API；脚本即全部能力
            _web.CoreWebView2.Navigate(new Uri(html).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动失败：" + ex.Message, "知债：星穹学途",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
