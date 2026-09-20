using System.Collections.ObjectModel;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>掌握度行（把 <see cref="MasteryRow"/> 包装为可直接绑定的展示行）。</summary>
public sealed record MasteryRowItem(MasteryRow Row)
{
    public string KpId => Row.KpId;

    public string Name => Row.Name;

    public double Score => Row.Score;

    /// <summary>band 原始键（green / yellow / red），供颜色与文字两个通道同时使用。</summary>
    public string Band => Row.Band;

    /// <summary>score 归一化到 0 到 1，供掌握度横条按容器宽度等比展开。</summary>
    public double Ratio => Math.Clamp(Row.Score / 100.0, 0, 1);

    public string ScoreText => $"score {Row.Score:0.##}";

    /// <summary>band 中文标签：颜色之外的第二重编码（色盲可用）。</summary>
    public string BandText => Shared.DemoMeta.BandLabel(Row.Band);

    public string DetailText => $"acc {Row.RecentAcc:0.##} · sev {Row.Sev:0.##} · conf {Row.SelfConf:0.#}/5";

    public string AccessibleName => $"{Name}，掌握度 {Row.Score:0.##}，{BandText}。{DetailText}";
}

/// <summary>
/// 学习进度页（方案 §12.5）。
///
/// 只展示知识点维度的掌握度：平均分、最弱若干知识点、band 分布，以及可排序的完整列表。
/// 明确不做学生之间的分数排名（方案 §15 伦理约束）。
/// </summary>
public sealed partial class ProgressViewModel : ViewModelBase
{
    private const string SortScoreAsc = "按 score 升序";
    private const string SortScoreDesc = "按 score 降序";
    private const string SortKpId = "按知识点 ID";

    /// <summary>「最弱知识点」的展示条数。</summary>
    private const int WeakestCount = 3;

    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private readonly List<MasteryRowItem> _all = new();
    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    public ProgressViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "学习进度";

    /// <summary>当前排序下的完整列表。</summary>
    public ObservableCollection<MasteryRowItem> Rows { get; } = new();

    /// <summary>最弱的若干个知识点（score 升序）。</summary>
    public ObservableCollection<MasteryRowItem> Weakest { get; } = new();

    public ObservableCollection<string> SortOptions { get; } = new() { SortScoreAsc, SortScoreDesc, SortKpId };

    [ObservableProperty]
    private string _selectedSort = SortScoreAsc;

    [ObservableProperty]
    private double _averageScore;

    [ObservableProperty]
    private int _greenCount;

    [ObservableProperty]
    private int _yellowCount;

    [ObservableProperty]
    private int _redCount;

    public bool HasRows => Rows.Count > 0;

    public bool IsEmpty => Rows.Count == 0;

    public bool HasWeakest => Weakest.Count > 0;

    public string StudentLine => $"学生：{_ctx.StudentName} · 数据源 {_data.ModeLabel}";

    public string AverageText => $"平均 score {AverageScore:0.##}";

    public string BandDistributionText => $"掌握良好 {GreenCount} · 需巩固 {YellowCount} · 优先修复 {RedCount}";

    public string WeakestCaption => $"最需要优先修复的 {WeakestCount} 个知识点";

    /// <summary>公式与伦理边界说明，必须原样展示在页面上（方案 §12.5 / §15）。</summary>
    public string FormulaText =>
        "掌握度由确定性公式计算（0.6*acc + 0.3*sev + 0.1*conf/5），仅用于学习规划，不构成处分依据。";

    public string RankDisclaimer => "列表按知识点掌握度排序，不对学生做分数排名或比较。";

    public override void Load(PageContext context)
    {
        _ctx = context;
        Reload();
    }

    private void Reload() => RunGuarded(() =>
    {
        var rows = _data.GetMastery(_ctx.StudentId);

        _all.Clear();
        foreach (var row in rows) _all.Add(new MasteryRowItem(row));

        AverageScore = _all.Count == 0 ? 0 : _all.Average(r => r.Score);
        GreenCount = _all.Count(r => r.Band == "green");
        YellowCount = _all.Count(r => r.Band == "yellow");
        RedCount = _all.Count(r => r.Band == "red");

        Weakest.Clear();
        foreach (var row in _all.OrderBy(r => r.Score).ThenBy(r => r.KpId, StringComparer.Ordinal).Take(WeakestCount))
            Weakest.Add(row);

        ApplySort();
    });

    partial void OnSelectedSortChanged(string value) => ApplySort();

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void OpenDebtList() => _nav.Navigate(AppPage.DebtList);

    private void ApplySort()
    {
        var ordered = SelectedSort switch
        {
            SortScoreDesc => _all.OrderByDescending(r => r.Score).ThenBy(r => r.KpId, StringComparer.Ordinal),
            SortKpId => _all.OrderBy(r => r.KpId, StringComparer.Ordinal),
            _ => _all.OrderBy(r => r.Score).ThenBy(r => r.KpId, StringComparer.Ordinal)
        };

        Rows.Clear();
        foreach (var row in ordered) Rows.Add(row);

        NotifyDerived();
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasWeakest));
        OnPropertyChanged(nameof(StudentLine));
        OnPropertyChanged(nameof(AverageText));
        OnPropertyChanged(nameof(BandDistributionText));
    }
}
