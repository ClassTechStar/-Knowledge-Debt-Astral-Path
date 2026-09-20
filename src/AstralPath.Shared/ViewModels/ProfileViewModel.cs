using System.Collections.ObjectModel;
using AstralPath.Contracts;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>
/// 雷达图单根轴（方案 §46.5.2）。
///
/// 坐标由 VM 统一算好，视图只做绝对定位绘制，不引入任何自定义控件。
/// 中心 (110,110)，半径 90px，标签在端点外侧 100px 处。
/// </summary>
public sealed record RadarAxisItem(
    string Axis,
    double Value,
    Point StartPoint,
    Point EndPoint,
    double DotX,
    double DotY,
    double LabelX,
    double LabelY)
{
    /// <summary>数值文本：雷达图不能只靠图形，必须给出可读数字。</summary>
    public string ValueText => $"{Value * 100:0}";

    public string LabelText => $"{Axis} {Value * 100:0}";

    public string AccessibleName => $"{Axis}：{Value * 100:0}（满分 100）";
}

/// <summary>画像标签云中的一个标签：字号随权重变化。</summary>
public sealed record TagItem(string Tag, string Domain, double Weight)
{
    /// <summary>字号随权重线性变化，上限 20，保证标签云有层次但不过度。</summary>
    public double FontSize => Math.Clamp(11 + Weight * 6, 11, 20);

    public string AccessibleName => $"{Tag}（{Domain}），权重 {Weight:0.##}";
}

/// <summary>画像时间线的单日快照。</summary>
public sealed record TimelineItem(string Date, int Attempts, double Accuracy)
{
    /// <summary>条宽比例（0 至 1），由 MultiBinding 乘上容器实际宽度。</summary>
    public double BarRatio => Accuracy;

    public string AttemptsText => $"{Attempts} 次作答";

    public string AccuracyText => $"正确率 {Accuracy * 100:0.#}%";

    public string AccessibleName => $"{Date}，{AttemptsText}，{AccuracyText}";
}

/// <summary>
/// 热力图单元格：颜色分档 + 数值文本（不能只靠颜色表意）。
///
/// 颜色以**十六进制色值字符串**承载，由视图用 <c>AppConverters.HexToBrush</c> 转成画刷：
/// 渲染对象必须在渲染平台就绪后创建，不能出现在 ViewModel 的静态字段里。
/// </summary>
public sealed record HeatCell(string HexColor, string ValueText, string AccessibleName);

/// <summary>热力图的一列（对应时间线中的一天），纵向 7 格。</summary>
public sealed record HeatColumn(
    string DateLabel,
    string AccuracyText,
    string AccessibleName,
    ObservableCollection<HeatCell> Cells);

/// <summary>
/// 用户画像页（方案 §46）。
///
/// 四件套：雷达图 / 时间线 / 标签云 / 热力图。
/// 硬约束：opt-out 时全部可视化必须隐藏，只留空态卡与「重新开启」入口。
/// </summary>
public sealed partial class ProfileViewModel : ViewModelBase
{
    private const double RadarCenter = 110;
    private const double RadarRadius = 90;
    private const double RadarLabelRadius = 100;
    private const int MaxAxes = 6;
    private const int HeatRows = 7;

    /// <summary>opt-out 空态文案（方案 §46，不得改写）。</summary>
    public const string OptOutText = "已关闭个性化：你的行为数据不再用于画像，界面不展示任何画像可视化。";

    /// <summary>样本不足时的雷达图替代提示。</summary>
    public const string SuppressedText = "样本不足（少于 5 次作答），暂不展示画像明细";

    /// <summary>合规说明（方案 §46）。</summary>
    public const string ComplianceText =
        "画像仅用于学习规划与推荐排序，不用于评分、排名或任何评价性用途；可随时关闭，关闭后行为等同无画像。";

    // 热力图 5 档绿（浅 → 深），档位边界：0.3 / 0.5 / 0.7 / 0.85。
    // 只存色值字符串：Avalonia 画刷依赖渲染平台，放进静态字段会让纯 ViewModel
    // 单测在类型初始化阶段就抛 TypeInitializationException。
    private const string HeatLevel0 = "#FEE2E2";
    private const string HeatLevel1 = "#FEF3C7";
    private const string HeatLevel2 = "#DCFCE7";
    private const string HeatLevel3 = "#BBF7D0";
    private const string HeatLevel4 = "#86EFAC";

    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    public ProfileViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "我的画像";

    public ObservableCollection<RadarAxisItem> RadarAxes { get; } = new();

    public ObservableCollection<TagItem> Tags { get; } = new();

    public ObservableCollection<TimelineItem> Timeline { get; } = new();

    public ObservableCollection<HeatColumn> HeatColumns { get; } = new();

    [ObservableProperty]
    private string _studentName = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private int _sampleCount;

    [ObservableProperty]
    private bool _optOut;

    /// <summary>雷达图是否被数据源抑制（样本不足 / k-匿名）。</summary>
    [ObservableProperty]
    private bool _radarSuppressed;

    [ObservableProperty]
    private string? _statusMessage;

    public string SampleText => $"样本数：{SampleCount} 次作答";

    /// <summary>opt-out 空态卡可见（此时所有可视化必须隐藏）。</summary>
    public bool ShowOptOutCard => OptOut;

    /// <summary>正常态：可视化区域可见。</summary>
    public bool ShowVisualizations => !OptOut;

    public bool ShowRadarCanvas => !OptOut && !RadarSuppressed && RadarAxes.Count > 0;

    public bool ShowRadarSuppressedHint => !OptOut && RadarSuppressed;

    public bool HasTags => Tags.Count > 0;

    public bool HasTimeline => Timeline.Count > 0;

    public bool HasHeatColumns => HeatColumns.Count > 0;

    public string OptOutNotice => OptOutText;

    public string SuppressedNotice => SuppressedText;

    public string Compliance => ComplianceText;

    public override void Load(PageContext context)
    {
        _ctx = context;
        StudentName = context.StudentName;
        Reload();
    }

    private void Reload() => RunGuarded(() =>
    {
        var teacherSide = _ctx.TeacherSide;

        var overview = _data.GetProfile(_ctx.StudentId, teacherSide);
        var radarRow = _data.GetProfileRadar(_ctx.StudentId, teacherSide);
        var tagRows = _data.GetProfileTags(_ctx.StudentId, teacherSide);
        var timelineRows = _data.GetProfileTimeline(_ctx.StudentId, teacherSide);

        // opt-out 是隐私硬约束：只要任一来源表明已关闭个性化，就按已关闭处理。
        OptOut = overview?.OptOut ?? tagRows.Any(t => t.OptOut);
        Summary = string.IsNullOrWhiteSpace(overview?.Summary) ? "暂无画像摘要" : overview!.Summary;
        SampleCount = overview?.SampleCount ?? timelineRows.Sum(t => t.Attempts);
        RadarSuppressed = radarRow?.Suppressed ?? false;

        RadarAxes.Clear();
        Tags.Clear();
        Timeline.Clear();
        HeatColumns.Clear();

        if (!OptOut)
        {
            BuildRadar(radarRow);
            BuildTags(tagRows);
            BuildTimeline(timelineRows);
            BuildHeatmap(timelineRows);
        }

        NotifyDerived();
    });

    private void BuildRadar(ProfileRadarRow? radar)
    {
        if (radar is null || radar.Suppressed) return;

        var axes = radar.Axes.Take(MaxAxes).ToList();
        if (axes.Count == 0) return;

        var step = 2 * Math.PI / axes.Count;
        for (var i = 0; i < axes.Count; i++)
        {
            // 从正上方开始，顺时针均分
            var angle = -Math.PI / 2 + i * step;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var value = Normalize(axes[i].Value);

            RadarAxes.Add(new RadarAxisItem(
                Axis: axes[i].Axis,
                Value: value,
                StartPoint: new Point(RadarCenter, RadarCenter),
                EndPoint: new Point(RadarCenter + RadarRadius * cos, RadarCenter + RadarRadius * sin),
                DotX: RadarCenter + RadarRadius * value * cos - 4,
                DotY: RadarCenter + RadarRadius * value * sin - 4,
                LabelX: RadarCenter + RadarLabelRadius * cos - 24,
                LabelY: RadarCenter + RadarLabelRadius * sin - 8));
        }
    }

    private void BuildTags(ProfileTagRow[] tags)
    {
        foreach (var tag in tags.Where(t => !t.OptOut).OrderByDescending(t => t.Weight))
        {
            Tags.Add(new TagItem(tag.Tag, DomainLabel(tag.Domain), Math.Clamp(tag.Weight, 0, 1)));
        }
    }

    private void BuildTimeline(ProfileTimelineRow[] timeline)
    {
        foreach (var row in timeline)
        {
            Timeline.Add(new TimelineItem(row.Date, row.Attempts, Normalize(row.Accuracy)));
        }
    }

    private void BuildHeatmap(ProfileTimelineRow[] timeline)
    {
        foreach (var day in timeline)
        {
            var accuracy = Normalize(day.Accuracy);
            var brush = HeatBrush(accuracy);
            var valueText = $"{accuracy * 100:0}%";
            var cells = new ObservableCollection<HeatCell>();

            for (var r = 0; r < HeatRows; r++)
            {
                cells.Add(new HeatCell(brush, valueText,
                    $"{day.Date}，{day.Attempts} 次作答，正确率 {accuracy * 100:0.#}%"));
            }

            HeatColumns.Add(new HeatColumn(
                ShortDate(day.Date),
                valueText,
                $"{day.Date}，{day.Attempts} 次作答，正确率 {accuracy * 100:0.#}%",
                cells));
        }
    }

    /// <summary>关闭个性化（隐私硬约束）：关闭后所有画像可视化立即消失。</summary>
    [RelayCommand]
    private void ClosePersonalization() => RunGuarded(() =>
    {
        _data.SetProfileOptOut(_ctx.StudentId, true);
        Reload();
        // 状态文案以数据源回读结果为准，避免界面声称已生效而存储并未变更。
        StatusMessage = OptOut
            ? "已关闭个性化：你的行为数据不再用于画像。"
            : "关闭请求已提交，但数据源仍返回已开启个性化。";
    });

    /// <summary>重新开启个性化。</summary>
    [RelayCommand]
    private void ReopenPersonalization() => RunGuarded(() =>
    {
        _data.SetProfileOptOut(_ctx.StudentId, false);
        Reload();
        StatusMessage = OptOut
            ? "重新开启请求已提交，但数据源仍标记为已关闭个性化。"
            : "已重新开启个性化：画像可视化已恢复。";
    });

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void OpenToday() => _nav.Navigate(AppPage.Today);

    /// <summary>归一到 0 至 1：大于 1 的值按 0 至 100 分制处理。</summary>
    private static double Normalize(double value)
    {
        var v = value > 1 ? value / 100.0 : value;
        return Math.Clamp(v, 0, 1);
    }

    private static string HeatBrush(double accuracy) => accuracy switch
    {
        < 0.3 => HeatLevel0,
        < 0.5 => HeatLevel1,
        < 0.7 => HeatLevel2,
        < 0.85 => HeatLevel3,
        _ => HeatLevel4
    };

    private static string ShortDate(string date)
        => date.Length >= 10 ? date[5..] : date;

    private static string DomainLabel(string domain) => domain switch
    {
        "concept" => "概念",
        "pace" => "节奏",
        "consent" => "授权",
        "practice" => "练习",
        _ => domain
    };

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(SampleText));
        OnPropertyChanged(nameof(ShowOptOutCard));
        OnPropertyChanged(nameof(ShowVisualizations));
        OnPropertyChanged(nameof(ShowRadarCanvas));
        OnPropertyChanged(nameof(ShowRadarSuppressedHint));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(HasHeatColumns));
    }
}
