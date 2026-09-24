using System.Collections.ObjectModel;
using AstralPath.Contracts;
using AstralPath.Core.Algorithms;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>
/// 债边展示行：把 <see cref="GraphViewEdgeDto"/> 包装为下拉框可直接绑定的选项。
/// 同时透传原始字段，便于对比卡读取「当前债边的真实 impact」。
/// </summary>
public sealed record WhatIfEdgeItem(GraphViewEdgeDto Edge)
{
    public string From => Edge.From;

    public string To => Edge.To;

    public string FromName => Edge.FromName;

    public string ToName => Edge.ToName;

    public double Impact => Edge.Impact;

    public string Status => Edge.Status;

    public double Weight => Edge.Weight;

    public double ScoreFrom => Edge.ScoreFrom;

    public double ScoreTo => Edge.ScoreTo;

    public int Freq => Edge.Freq;

    /// <summary>「前置知识点 → 后继知识点」。</summary>
    public string Label => $"{Edge.FromName} → {Edge.ToName}";

    /// <summary>当前债边的关键参数，作为推演滑条的初始值来源。</summary>
    public string Detail =>
        $"当前 impact {Edge.Impact:0.##} · score {Edge.ScoreFrom:0.##} → {Edge.ScoreTo:0.##} · 近 7 天错 {Edge.Freq} 次";

    public string AccessibleName => $"{Label}，{StatusLabel(Edge.Status)}，当前影响 {Edge.Impact:0.##}";

    private static string StatusLabel(string status) => status switch
    {
        "open" => "开放",
        "repairing" => "修复中",
        "cleared" => "已销账",
        "dropped" => "已放弃",
        _ => status
    };
}

/// <summary>
/// What-if 影响推演页（方案 §12.5）。
///
/// 目的：让「如果起点/后继掌握度、出错频次、距上次出错天数变化，impact 会怎样」这件事可被亲手验证。
/// 任意参数变化即实时重算；结果卡同时给出 API 返回值与端上复算值（同源容差 1e-6）。
/// </summary>
public sealed partial class WhatIfViewModel : ViewModelBase
{
    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;

    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");
    private bool _suppress;

    public WhatIfViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "What-if 影响推演";

    /// <summary>可推演的开放债边（按 impact 降序）。</summary>
    public ObservableCollection<WhatIfEdgeItem> Edges { get; } = new();

    [ObservableProperty]
    private WhatIfEdgeItem? _selectedEdge;

    /// <summary>起点掌握度（0–100），对应 <c>OverrideScoreFrom</c>。</summary>
    [ObservableProperty]
    private double _scoreFromSlider;

    /// <summary>后继掌握度（0–100），对应 <c>OverrideScoreTo</c>。</summary>
    [ObservableProperty]
    private double _scoreToSlider;

    /// <summary>近 7 天出错次数（0–10）。Slider 的 Value 是 double，这里承载滑条值。</summary>
    [ObservableProperty]
    private double _freqSlider;

    /// <summary>出错次数（int），对应 <c>OverrideFreq</c>（int?）。</summary>
    [ObservableProperty]
    private int _freq;

    /// <summary>距上次出错天数（0–30）。Slider 的 Value 是 double，这里承载滑条值。</summary>
    [ObservableProperty]
    private double _daysSlider;

    /// <summary>距上次出错天数（int），对应 <c>OverrideDays</c>（int?）。</summary>
    [ObservableProperty]
    private int _days;

    [ObservableProperty]
    private double _simImpact;

    [ObservableProperty]
    private bool _isDetected;

    /// <summary>新近度（0–1），视图中用 RatioToPercent 显示为百分比。</summary>
    [ObservableProperty]
    private double _recency;

    [ObservableProperty]
    private string _recomputeText = string.Empty;

    [ObservableProperty]
    private string _emptyReason = string.Empty;

    public bool HasEdges => Edges.Count > 0;

    public bool IsEmpty => Edges.Count == 0;

    public string ScoreFromText => $"score_from {ScoreFromSlider:0.#}";

    public string ScoreToText => $"score_c {ScoreToSlider:0.#}";

    public string FreqText => $"近 7 天错 {Freq} 次";

    public string DaysText => $"距上次出错 {Days} 天";

    /// <summary>推演 impact 的大号数字文本。</summary>
    public string SimImpactText => $"{SimImpact:0.##}";

    /// <summary>当前债边的真实 impact（来自 <see cref="GraphViewEdgeDto.Impact"/>）。</summary>
    public string RealImpactText => SelectedEdge is null ? "—" : $"{SelectedEdge.Impact:0.##}";

    public string EdgeDetail => SelectedEdge?.Detail ?? "未选择债边";

    public string DetectedText => IsDetected
        ? "判定为知识债（impact 已达阈值）"
        : "当前参数下未判定为知识债（impact 未达阈值）";

    public string DeltaArrow => SelectedEdge is null
        ? "="
        : SimImpact > SelectedEdge.Impact + 1e-6 ? "↑"
        : SimImpact < SelectedEdge.Impact - 1e-6 ? "↓"
        : "=";

    public string DeltaText
    {
        get
        {
            if (SelectedEdge is null) return "未选择债边，无法对比。";

            var delta = SimImpact - SelectedEdge.Impact;
            if (Math.Abs(delta) <= 1e-6) return "推演结果与当前债边一致（差值 0）。";
            return delta > 0
                ? $"推演比当前债边高 {delta:0.##}（↑）"
                : $"推演比当前债边低 {Math.Abs(delta):0.##}（↓）";
        }
    }

    public string FormulaText => "impact = freq × (50 − score_c) × recency × weight";

    public string FormulaNote => "端上复算与 API 同源，容差 1e-6。";

    public override void Load(PageContext context)
    {
        _ctx = context;
        Reload();
    }

    private void Reload() => RunGuarded(() =>
    {
        var previous = SelectedEdge;
        var view = _data.GetGraphView(_ctx.StudentId);

        Edges.Clear();
        foreach (var edge in view.Edges.OrderByDescending(e => e.Impact).ThenBy(e => e.From, StringComparer.Ordinal))
            Edges.Add(new WhatIfEdgeItem(edge));

        EmptyReason = Edges.Count == 0
            ? "当前没有开放的知识债可推演。可以先做一次诊断，或回到图谱页查看掌握度分布。"
            : string.Empty;

        // 尽量保持用户已选中的债边；不可用时退回 impact 最高的一条。
        var target = previous is null
            ? Edges.FirstOrDefault()
            : Edges.FirstOrDefault(e => e.From == previous.From && e.To == previous.To) ?? Edges.FirstOrDefault();

        SelectedEdge = target;
        ApplyEdge(target);
    });

    partial void OnSelectedEdgeChanged(WhatIfEdgeItem? value) => ApplyEdge(value);

    partial void OnScoreFromSliderChanged(double value)
    {
        if (_suppress) return;
        OnPropertyChanged(nameof(ScoreFromText));
        Recalculate();
    }

    partial void OnScoreToSliderChanged(double value)
    {
        if (_suppress) return;
        OnPropertyChanged(nameof(ScoreToText));
        Recalculate();
    }

    partial void OnFreqSliderChanged(double value)
    {
        if (_suppress) return;
        Freq = (int)Math.Round(value);
        OnPropertyChanged(nameof(FreqText));
    }

    partial void OnDaysSliderChanged(double value)
    {
        if (_suppress) return;
        Days = (int)Math.Round(value);
        OnPropertyChanged(nameof(DaysText));
    }

    partial void OnFreqChanged(int value)
    {
        if (_suppress) return;
        Recalculate();
    }

    partial void OnDaysChanged(int value)
    {
        if (_suppress) return;
        Recalculate();
    }

    /// <summary>把滑条参数写入请求并实时取回推演结果。</summary>
    private void Recalculate() => RunGuarded(() =>
    {
        if (SelectedEdge is null)
        {
            SimImpact = 0;
            IsDetected = false;
            Recency = 0;
            RecomputeText = string.Empty;
            NotifyDerived();
            return;
        }

        var response = _data.WhatIf(new WhatIfRequest(
            _ctx.StudentId,
            SelectedEdge.From,
            SelectedEdge.To,
            ScoreFromSlider,
            ScoreToSlider,
            Freq,
            Days));

        SimImpact = response.Impact;
        IsDetected = response.Detected;
        Recency = response.Recency;

        // 端上复算直接调用 Core 纯函数（与 API、离线数据源同一份实现），因此差值必然在 1e-6 内。
        var client = DebtScannerCompat.ComputeImpactV1(
            new ImpactInput(ScoreFromSlider, ScoreToSlider, Freq, Days, SelectedEdge.Weight));

        RecomputeText =
            $"端上复算 {client.Impact:0.######}，与 API 差值 {Math.Abs(client.Impact - response.Impact):0.000000}（容差 1e-6）";

        NotifyDerived();
    });

    /// <summary>切换债边时把滑条初始化到该债边的真实参数。</summary>
    private void ApplyEdge(WhatIfEdgeItem? edge)
    {
        _suppress = true;
        try
        {
            if (edge is null)
            {
                ScoreFromSlider = 0;
                ScoreToSlider = 0;
                FreqSlider = 0;
                Freq = 0;
                DaysSlider = 0;
                Days = 0;
            }
            else
            {
                ScoreFromSlider = Math.Clamp(edge.ScoreFrom, 0, 100);
                ScoreToSlider = Math.Clamp(edge.ScoreTo, 0, 100);
                FreqSlider = Math.Clamp(edge.Freq, 0, 10);
                Freq = (int)Math.Round(FreqSlider);
                DaysSlider = EstimateDays(edge);
                Days = (int)Math.Round(DaysSlider);
            }
        }
        finally
        {
            _suppress = false;
        }

        OnPropertyChanged(nameof(ScoreFromText));
        OnPropertyChanged(nameof(ScoreToText));
        OnPropertyChanged(nameof(FreqText));
        OnPropertyChanged(nameof(DaysText));
        Recalculate();
    }

    /// <summary>
    /// 反推「距上次出错天数」：债边 DTO 不含该字段，这里用当前 impact 反解 recency，
    /// 使推演初值与该债边的真实状态一致（对比卡初始差值为 0）。
    /// </summary>
    private static int EstimateDays(WhatIfEdgeItem edge)
    {
        var denominator = edge.Freq * (50.0 - edge.ScoreTo) * edge.Weight;
        if (edge.Impact <= 0 || denominator <= 0) return 0;

        var recency = edge.Impact / denominator;
        if (recency <= 0 || recency > 1) return 0;

        return Math.Clamp((int)Math.Round(7.0 * (1.0 / recency - 1.0)), 0, 30);
    }

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void GoGraph() => _nav.Navigate(AppPage.Graph);

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasEdges));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SimImpactText));
        OnPropertyChanged(nameof(RealImpactText));
        OnPropertyChanged(nameof(EdgeDetail));
        OnPropertyChanged(nameof(DetectedText));
        OnPropertyChanged(nameof(DeltaArrow));
        OnPropertyChanged(nameof(DeltaText));
    }
}
