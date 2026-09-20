using System.Collections.ObjectModel;
using AstralPath.Contracts;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>甘特任务条（把 <see cref="PlanItemDto"/> 包装为可直接绑定的展示块）。</summary>
public sealed record PlanTaskItem(PlanItemDto Dto)
{
    /// <summary>
    /// 任务类型色。颜色只用于加快视觉扫读，类型名与分钟数同时以文字给出，
    /// 不依赖颜色单独表意（方案 附录 X）。
    ///
    /// 这里存**色值字符串**而不是画刷：Avalonia 画刷依赖已初始化的渲染平台，
    /// ViewModel 的静态字段持有画刷会让纯 ViewModel 单测在类型初始化阶段直接失败。
    /// 由视图用 <c>AppConverters.HexToBrush</c> 转换。
    /// </summary>
    private static readonly Dictionary<string, string> TypeColors = new(StringComparer.Ordinal)
    {
        ["concept"] = "#3B82F6",
        ["drill"] = "#8B5CF6",
        ["quiz"] = "#F59E0B",
        ["review"] = "#10B981"
    };

    private const string UnknownTypeColor = "#94A3B8";

    public string KpName => Dto.KpName;

    public string Type => Dto.Type;

    public string TypeLabel => Dto.Type switch
    {
        "concept" => "概念",
        "drill" => "演练",
        "quiz" => "测验",
        "review" => "复习",
        _ => Dto.Type
    };

    public int EstMin => Dto.EstMin;

    /// <summary>被规划器放弃的任务（通常为超出每日预算后的降级结果）。</summary>
    public bool IsDropped => string.Equals(Dto.Status, "dropped", StringComparison.Ordinal);

    /// <summary>放弃的任务整条降透明度，避免与有效任务混淆。</summary>
    public double BarOpacity => IsDropped ? 0.45 : 1.0;

    /// <summary>任务条底色（十六进制色值；由视图转成画刷）。</summary>
    public string BarColor => TypeColors.TryGetValue(Dto.Type, out var hex) ? hex : UnknownTypeColor;

    public string Meta => $"{TypeLabel} · {EstMin} 分钟";

    public string DroppedNote => IsDropped ? "已放弃（不占预算）" : string.Empty;

    public string AccessibleName => IsDropped
        ? $"{KpName}，{Meta}，已放弃。{Dto.Why}"
        : $"{KpName}，{Meta}。{Dto.Why}";
}

/// <summary>甘特中的一天：左侧天序号与合计分钟，右侧任务条序列。</summary>
public sealed record PlanDayRow(int Day, int Minutes, int DayBudgetMin, IReadOnlyList<PlanTaskItem> Tasks)
{
    public string DayLabel => $"第 {Day} 天";

    public string MinutesText => $"{Minutes} 分钟";

    public bool IsOverBudget => Minutes > DayBudgetMin;

    public string OverBudgetText => $"超预算 {Minutes - DayBudgetMin} 分钟";

    public bool HasTasks => Tasks.Count > 0;

    public string AccessibleName => IsOverBudget
        ? $"{DayLabel}，合计 {Minutes} 分钟，超出每日预算 {Minutes - DayBudgetMin} 分钟"
        : $"{DayLabel}，合计 {Minutes} 分钟";
}

/// <summary>约束违规行（K1 到 K5 检查器的失败项）。</summary>
public sealed record ConstraintViolationRow(ConstraintViolationDto Dto)
{
    public string Code => Dto.Code;

    public string DayText => Dto.Day is null ? "全局" : $"第 {Dto.Day} 天";

    public string Message => Dto.Message;

    public string AccessibleName => $"约束 {Code}，{DayText}：{Message}";
}

/// <summary>
/// 14 天计划页（方案 §12.3）。
///
/// 只读展示规划器输出：每天一行甘特、当日合计与超预算提示、约束校验结果。
/// 重新生成走 <see cref="IAppDataSource.CreatePlan"/>，判定逻辑全部在 Core 的确定性规划器内。
/// </summary>
public sealed partial class PlanViewModel : ViewModelBase
{
    /// <summary>数据源默认每日预算（与 <see cref="IAppDataSource.CreatePlan"/> 的默认值一致）。</summary>
    private const int DefaultDayBudgetMin = 35;

    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    public PlanViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "14 天计划";

    public ObservableCollection<PlanDayRow> Days { get; } = new();

    public ObservableCollection<ConstraintViolationRow> Violations { get; } = new();

    [ObservableProperty]
    private PlanDto? _plan;

    public bool HasPlan => Plan is not null;

    public bool IsEmpty => Plan is null;

    public bool HasViolations => Plan is not null && Plan.ConstraintViolations.Count > 0;

    /// <summary>有计划且零违规时给出正向结论，避免用户以为「没显示就是没校验」。</summary>
    public bool NoViolations => Plan is not null && Plan.ConstraintViolations.Count == 0;

    public string StudentLine => $"学生：{_ctx.StudentName} · 数据源 {_data.ModeLabel}";

    public string PlanIdText => Plan is null ? "计划 ID 未生成" : $"计划 ID {Plan.Id}";

    public string GraphVersionText => Plan is null
        ? "图版本 未知"
        : $"图版本 v{Plan.GraphVersion} · 权重 {Plan.WeightVer}";

    public string BudgetText => Plan is null
        ? $"每日预算 {DefaultDayBudgetMin} 分钟（默认）"
        : $"每日预算 {Plan.DayBudgetMin} 分钟";

    public string ConstraintsText => Plan is null
        ? "约束校验 未执行"
        : Plan.ConstraintsChecked
            ? "K1–K5 约束校验通过"
            : $"约束校验未通过（{Plan.ConstraintViolations.Count} 条）";

    public string TotalText => Plan is null
        ? string.Empty
        : $"合计 {Plan.Days.Count} 天 / {Plan.Days.Sum(d => d.Minutes)} 分钟";

    public string PlannerLine => Plan is null
        ? string.Empty
        : $"规划器 {Plan.PlannerVersion} · 有效至 {Plan.ExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}";

    public override void Load(PageContext context)
    {
        _ctx = context;
        Reload();
    }

    private void Reload() => RunGuarded(() => ApplyPlan(_data.GetPlan(_ctx.StudentId)));

    [RelayCommand]
    private void Refresh() => Reload();

    /// <summary>生成或重新生成计划：等价于「按当前债边重排未来 14 天」。</summary>
    [RelayCommand]
    private void GeneratePlan() => RunGuarded(() => ApplyPlan(_data.CreatePlan(_ctx.StudentId)));

    [RelayCommand]
    private void OpenGraph() => _nav.Navigate(AppPage.Graph);

    private void ApplyPlan(PlanDto? plan)
    {
        Plan = plan;
        Days.Clear();
        Violations.Clear();

        if (plan is not null)
        {
            foreach (var day in plan.Days.OrderBy(d => d.Day))
            {
                var tasks = day.Items.Select(i => new PlanTaskItem(i)).ToList();
                Days.Add(new PlanDayRow(day.Day, day.Minutes, plan.DayBudgetMin, tasks));
            }

            foreach (var violation in plan.ConstraintViolations)
                Violations.Add(new ConstraintViolationRow(violation));
        }

        NotifyDerived();
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasViolations));
        OnPropertyChanged(nameof(NoViolations));
        OnPropertyChanged(nameof(StudentLine));
        OnPropertyChanged(nameof(PlanIdText));
        OnPropertyChanged(nameof(GraphVersionText));
        OnPropertyChanged(nameof(BudgetText));
        OnPropertyChanged(nameof(ConstraintsText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(PlannerLine));
    }
}
