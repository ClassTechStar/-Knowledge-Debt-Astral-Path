using System.Collections.ObjectModel;
using AstralPath.Contracts;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>债边行（图谱右栏 Top-N 列表 + Story 卡）。</summary>
public sealed record DebtRow(
    string FromKp, string ToKp, string FromName, string ToName, string Status,
    double Impact, double ScoreFrom, double ScoreTo, int Freq,
    string Story, string[] Actions, bool Degraded)
{
    public string Title => $"{FromName} → {ToName}";

    public string ImpactText => $"impact {Impact:0.##}";

    public string FormulaText => $"score {ScoreFrom:0.##} → {ScoreTo:0.##} · 近 7 天错 {Freq} 次";

    public bool HasActions => Actions.Length > 0;

    /// <summary>叙事模板降级时显式标注，避免把兜底文案当成个性化诊断（方案 §10 安全旗标）。</summary>
    public string SafetyText => Degraded ? "叙事模板已降级（安全兜底）" : string.Empty;

    public string AccessibleName =>
        $"{FromName} 到 {ToName} 的知识债，{ImpactText}。{Story}";
}

/// <summary>右栏「掌握度最弱」列表行。</summary>
public sealed record GraphNodePosRow(string Id, string Name, string ScoreText);

/// <summary>
/// 图谱页（方案 §12.2 桌面 / §13.2 移动）。
///
/// 布局与绘制交给 <see cref="Controls.GraphCanvas"/>（自绘 + LOD），
/// 本 VM 只负责：Top-N 选择、节点选中态、Story 卡文案、跳今日/练习的导航。
/// </summary>
public sealed partial class GraphViewModel : ViewModelBase
{
    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    public GraphViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "知识债图谱";

    /// <summary>应用级可访问性设置（图谱配色需与设置页联动）。</summary>
    public AppSettings Settings => AppSettings.Current;

    public ObservableCollection<int> TopNOptions { get; } = new() { 3, 5, 10, 20 };

    public ObservableCollection<DebtRow> Debts { get; } = new();

    public ObservableCollection<GraphNodePosRow> HotNodes { get; } = new();

    [ObservableProperty]
    private int _topN = 5;

    /// <summary>窄屏时右栏收起（由视图 code-behind 按宽度回写）。</summary>
    [ObservableProperty]
    private bool _showSidePanel = true;

    [ObservableProperty]
    private GraphViewDto? _view;

    [ObservableProperty]
    private string? _selectedNodeId;

    [ObservableProperty]
    private string _selectedNodeTitle = "未选中节点";

    [ObservableProperty]
    private string _selectedNodeDetail = "点击图中的节点，查看该知识点的掌握度与相关债边。";

    [ObservableProperty]
    private string _graphMeta = string.Empty;

    [ObservableProperty]
    private string _emptyReason = string.Empty;

    public bool HasDebts => Debts.Count > 0;

    public bool IsEmpty => Debts.Count == 0;

    partial void OnTopNChanged(int value) => Reload();

    public override void Load(PageContext context)
    {
        _ctx = context;
        Reload();
    }

    private void Reload() => RunGuarded(() =>
    {
        var view = _data.GetGraphView(_ctx.StudentId);
        var diagnose = _data.Diagnose(_ctx.StudentId, TopN);

        View = view;
        GraphMeta = $"图版本 v{view.GraphVer} · 权重 {view.WeightVer} · 节点 {view.Nodes.Count} · 开放债边 {view.Edges.Count}";

        Debts.Clear();
        for (var i = 0; i < diagnose.TopDebts.Count; i++)
        {
            var d = diagnose.TopDebts[i];
            var n = i < diagnose.Narrative.Count ? diagnose.Narrative[i] : null;
            Debts.Add(new DebtRow(
                d.FromKp, d.ToKp, d.FromKpName, d.ToKpName, d.Status,
                d.Impact, d.ScoreFrom, d.ScoreTo, d.Freq,
                n?.Story ?? "（暂无叙事）",
                n?.Actions.ToArray() ?? Array.Empty<string>(),
                n?.SafetyFlags.DegradedTemplate ?? true));
        }

        HotNodes.Clear();
        foreach (var node in view.Nodes.OrderBy(n => n.Score).ThenBy(n => n.Id, StringComparer.Ordinal).Take(8))
            HotNodes.Add(new GraphNodePosRow(node.Id, node.Name, $"score {node.Score:0.##}"));

        EmptyReason = Debts.Count == 0
            ? "当前没有检测到开放的知识债。可以先做一次诊断，或降低 Top-N 门槛。"
            : string.Empty;

        if (SelectedNodeId is not null && view.Nodes.All(n => n.Id != SelectedNodeId))
            SelectedNodeId = null;

        NotifyDerived();
    });

    /// <summary>图谱控件的选中回调（也支持键盘 Enter 选中）。</summary>
    [RelayCommand]
    private void SelectNode(string? nodeId)
    {
        SelectedNodeId = nodeId;
        if (nodeId is null || View is null)
        {
            SelectedNodeTitle = "未选中节点";
            SelectedNodeDetail = "点击图中的节点，查看该知识点的掌握度与相关债边。";
            return;
        }

        var node = View.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null) return;

        var related = View.Edges.Where(e => e.From == nodeId || e.To == nodeId).ToList();
        SelectedNodeTitle = $"{node.Name}（{node.Id}）";

        var bandText = Shared.DemoMeta.BandLabel(node.Band);
        var lines = new List<string>
        {
            $"掌握度 score {node.Score:0.##} · {bandText}",
            $"课程 {node.Course}"
        };

        if (related.Count == 0)
        {
            lines.Add("该知识点当前没有关联的开放债边。");
        }
        else
        {
            lines.Add($"关联债边 {related.Count} 条：");
            foreach (var e in related.OrderByDescending(x => x.Impact).Take(4))
            {
                var other = e.From == nodeId ? e.ToName : e.FromName;
                var dir = e.From == nodeId ? "→" : "←";
                lines.Add($"  {dir} {other}：impact {e.Impact:0.##}（{StatusLabel(e.Status)}）");
            }
        }

        SelectedNodeDetail = string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private void ClearSelection() => SelectNode(null);

    [RelayCommand]
    private void Refresh() => Reload();

    /// <summary>从图谱跳到今日，携带该知识点，便于「看到债 → 立刻去还」。</summary>
    [RelayCommand]
    private void OpenToday()
    {
        if (SelectedNodeId is null) return;
        _nav.Navigate(AppPage.Today, SelectedNodeId);
    }

    private static string StatusLabel(string status) => status switch
    {
        "open" => "开放",
        "repairing" => "修复中",
        "cleared" => "已销账",
        "dropped" => "已放弃",
        _ => status
    };

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasDebts));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
