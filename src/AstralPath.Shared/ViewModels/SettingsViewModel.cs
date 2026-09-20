using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>
/// 设置页（方案 §12.6 / 附录 X）。
///
/// 可访问性偏好是进程级语义，直接绑定单例 <see cref="AppSettings.Current"/>：
/// 它是 <see cref="ObservableObject"/>，双向绑定即可与图谱控件、Shell 实时联动。
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    /// <summary>端上复算与 API 的公式说明（方案 §9.3 / §12.6）。</summary>
    public const string FormulaText =
        "score = 100*(0.6*acc + 0.3*sev_norm + 0.1*conf/5)；impact = freq*(50-score_c)*recency*weight";

    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    public SettingsViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "设置";

    /// <summary>应用级可访问性与显示设置（单例，XAML 中直接双向绑定其属性）。</summary>
    public AppSettings Settings => AppSettings.Current;

    [ObservableProperty]
    private string _modeLabel = string.Empty;

    [ObservableProperty]
    private string _studentId = string.Empty;

    [ObservableProperty]
    private int _graphVer;

    [ObservableProperty]
    private string _weightVer = string.Empty;

    /// <summary>一次性操作反馈（恢复默认设置等）。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    public string AppVersion => "1.3.0";

    public string AboutFormula => FormulaText;

    public string AboutParity => "端上复算与 API 同源（容差 1e-6）";

    public string Disclaimer => Shared.DemoMeta.FooterDisclaimer;

    public override void Load(PageContext context)
    {
        _ctx = context;
        Reload();
    }

    private void Reload() => RunGuarded(() =>
    {
        var view = _data.GetGraphView(_ctx.StudentId);
        ModeLabel = _data.ModeLabel;
        StudentId = _ctx.StudentId;
        GraphVer = view.GraphVer;
        WeightVer = view.WeightVer;
    });

    /// <summary>恢复默认设置（触达尺寸回到桌面 44px）。</summary>
    [RelayCommand]
    private void ResetSettings()
    {
        Settings.Reset();
        StatusMessage = "已恢复默认设置";
    }

    [RelayCommand]
    private void Refresh() => Reload();

    /// <summary>设置页里查看图谱版本变更的效果，提供直达入口。</summary>
    [RelayCommand]
    private void OpenGraph() => _nav.Navigate(AppPage.Graph);
}
