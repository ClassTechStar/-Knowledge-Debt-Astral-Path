using System.Collections.ObjectModel;
using AstralPath.Contracts;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>债边行（把 <see cref="GraphViewEdgeDto"/> 包装为可直接绑定的展示行）。</summary>
public sealed record DebtEdgeItem(GraphViewEdgeDto Dto)
{
    public string FromKp => Dto.From;

    public string ToKp => Dto.To;

    public string Route => $"{Dto.FromName} → {Dto.ToName}";

    /// <summary>债边状态原始键（open / repairing / cleared / dropped）。</summary>
    public string Status => Dto.Status;

    /// <summary>状态中文标签：颜色之外的第二重编码（色盲可用）。</summary>
    public string StatusLabel => Dto.Status switch
    {
        "open" => "开放",
        "repairing" => "修复中",
        "cleared" => "已销账",
        "dropped" => "已放弃",
        _ => Dto.Status
    };

    public string ImpactText => $"impact {Dto.Impact:0.##}";

    public string ScoreText => $"score {Dto.ScoreFrom:0.##} → {Dto.ScoreTo:0.##}";

    public string FreqText => $"近 7 天错 {Dto.Freq} 次";

    public string Meta => $"{ImpactText} · {ScoreText} · {FreqText}";

    public string AccessibleName => $"{Dto.FromName} 到 {Dto.ToName} 的知识债，{StatusLabel}，{Meta}";
}

/// <summary>
/// 债边清单页（方案 §12.2 / §6）。
///
/// 列出当前学生的全部非销账债边，支持按状态筛选，并逐个触发销账校验。
/// 销账判定来自确定性状态机（连续 2 次达标探测），本页不做任何模型推断。
/// </summary>
public sealed partial class DebtListViewModel : ViewModelBase
{
    private const string FilterAll = "全部";
    private const string FilterOpen = "开放";
    private const string FilterRepairing = "修复中";
    private const string FilterCleared = "已销账";

    /// <summary>销账所需的连续达标次数（与 Core 的 SaleStateMachine 一致）。</summary>
    private const int SaleStreakRequired = 2;

    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private readonly List<DebtEdgeItem> _all = new();
    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    public DebtListViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "债边清单";

    /// <summary>当前筛选条件下的债边。</summary>
    public ObservableCollection<DebtEdgeItem> Edges { get; } = new();

    public ObservableCollection<string> FilterOptions { get; } = new() { FilterAll, FilterOpen, FilterRepairing, FilterCleared };

    [ObservableProperty]
    private string _selectedFilter = FilterAll;

    /// <summary>销账检查结果状态条（本页自己定义，语义与 Shell 的状态提示一致）。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>状态条副标题：本次检查的债边与当前 impact。</summary>
    [ObservableProperty]
    private string _statusDetail = string.Empty;

    [ObservableProperty]
    private bool _statusCleared;

    public bool HasEdges => Edges.Count > 0;

    public bool IsEmpty => Edges.Count == 0;

    /// <summary>非销账结论时状态条可见（与销账结论互斥）。</summary>
    public bool StatusNotCleared => !StatusCleared && !string.IsNullOrWhiteSpace(StatusMessage);

    public string StudentLine => $"学生：{_ctx.StudentName} · 数据源 {_data.ModeLabel}";

    public string CountText => $"筛选后 {Edges.Count} 条 · 全部 {_all.Count} 条";

    public override void Load(PageContext context)
    {
        _ctx = context;
        Reload();
    }

    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    partial void OnStatusMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(StatusNotCleared));
    }

    partial void OnStatusClearedChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusNotCleared));
    }

    private void Reload() => RunGuarded(() =>
    {
        var view = _data.GetGraphView(_ctx.StudentId);

        _all.Clear();
        foreach (var edge in view.Edges)
            _all.Add(new DebtEdgeItem(edge));

        ApplyFilter();
    });

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void OpenGraph() => _nav.Navigate(AppPage.Graph);

    /// <summary>检查某条债边是否可销账，并把结论写进顶部状态条。</summary>
    [RelayCommand]
    private void CheckSale(DebtEdgeItem? item)
    {
        if (item is null) return;

        RunGuarded(() =>
        {
            var result = _data.SaleCheck(_ctx.StudentId, item.FromKp, item.ToKp);

            StatusCleared = result.Cleared;
            StatusMessage = result.Cleared
                ? $"已销账（连续达标 {result.Streak} 次）"
                : $"还差 {Math.Max(0, SaleStreakRequired - result.Streak)} 次连续达标";

            if (!result.Cleared && !string.IsNullOrWhiteSpace(result.Reason))
                StatusMessage += $"：{result.Reason}";

            StatusDetail = $"{item.Route} · 当前 impact {result.CurrentImpact:0.##} · 状态 {StatusLabel(result.Status)}";

            // 销账后该债边不再出现在图谱视图中，需要重新取数。
            Reload();
        });
    }

    private static string StatusLabel(string status) => status switch
    {
        "open" => "开放",
        "repairing" => "修复中",
        "cleared" => "已销账",
        "dropped" => "已放弃",
        _ => status
    };

    private void ApplyFilter()
    {
        var status = FilterToStatus(SelectedFilter);

        Edges.Clear();
        foreach (var edge in _all)
        {
            if (status is null || string.Equals(edge.Status, status, StringComparison.Ordinal))
                Edges.Add(edge);
        }

        NotifyDerived();
    }

    private static string? FilterToStatus(string? filter) => filter switch
    {
        FilterOpen => "open",
        FilterRepairing => "repairing",
        FilterCleared => "cleared",
        _ => null
    };

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasEdges));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StudentLine));
        OnPropertyChanged(nameof(CountText));
    }
}
