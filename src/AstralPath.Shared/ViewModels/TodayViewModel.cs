using System.Collections.ObjectModel;
using AstralPath.Contracts;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>今日任务行（把 <see cref="TodayTaskDto"/> 包装为可直接绑定的展示行）。</summary>
public sealed record TodayTaskItem(TodayTaskDto Dto)
{
    public string Id => Dto.Id;

    public string KpName => Dto.KpName;

    public string TypeLabel => Dto.Type switch
    {
        "concept" => "概念",
        "drill" => "演练",
        "quiz" => "测验",
        "review" => "复习",
        _ => Dto.Type
    };

    public int Difficulty => Dto.Difficulty;

    public int EstMin => Dto.EstMin;

    public string Why => Dto.Why;

    public string QuestionId => Dto.QuestionId;

    /// <summary>「概念 · 8 分钟 · 难度 2/5」，供卡片副标题。</summary>
    public string Meta => $"{TypeLabel} · {EstMin} 分钟 · 难度 {Difficulty}/5";

    /// <summary>无障碍名称：读屏用户听到的信息不弱于视觉用户（方案 附录 X）。</summary>
    public string AccessibleName => $"{KpName}，{TypeLabel}，{EstMin} 分钟，难度 {Difficulty} 分之 5。{Why}";
}

/// <summary>
/// 今日页（方案 §12.3 / §13.1）。
///
/// 移动端为启动页，桌面端为默认页。核心信息是「今日 35 分钟胶囊」：
/// 预算、实际合计、是否超预算，以及逐个任务卡（含「为什么做这个」）。
/// </summary>
public sealed partial class TodayViewModel : ViewModelBase
{
    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    public TodayViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "今日";

    public ObservableCollection<TodayTaskItem> Tasks { get; } = new();

    [ObservableProperty]
    private int _day = 1;

    [ObservableProperty]
    private int _totalMinutes;

    [ObservableProperty]
    private int _dayBudgetMin = 35;

    [ObservableProperty]
    private string _coachMessage = string.Empty;

    [ObservableProperty]
    private string _studentName = string.Empty;

    public bool HasTasks => Tasks.Count > 0;

    public bool IsEmpty => Tasks.Count == 0;

    /// <summary>预算占用比例（0–1），供进度条使用。</summary>
    public double BudgetRatio => DayBudgetMin <= 0 ? 0 : Math.Clamp((double)TotalMinutes / DayBudgetMin, 0, 1);

    /// <summary>超预算时给出提示（不阻断，只提示）。</summary>
    public bool IsOverBudget => TotalMinutes > DayBudgetMin;

    public string BudgetText => $"今日 {TotalMinutes} 分钟 / 预算 {DayBudgetMin} 分钟";

    public override void Load(PageContext context)
    {
        _ctx = context;
        StudentName = context.StudentName;
        Reload();
    }

    private void Reload() => RunGuarded(() =>
    {
        var today = _data.GetToday(_ctx.StudentId, Day);
        Day = today.Day;
        TotalMinutes = today.TotalMinutes;
        CoachMessage = today.CoachMessage;

        Tasks.Clear();
        foreach (var t in today.Tasks) Tasks.Add(new TodayTaskItem(t));

        NotifyDerived();
    });

    /// <summary>由练习页返回后刷新（作答会改变掌握度与债边状态）。</summary>
    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand(CanExecute = nameof(CanGoPreviousDay))]
    private void PreviousDay()
    {
        Day = Math.Max(1, Day - 1);
        Reload();
    }

    private bool CanGoPreviousDay() => Day > 1;

    [RelayCommand(CanExecute = nameof(CanGoNextDay))]
    private void NextDay()
    {
        Day = Math.Min(14, Day + 1);
        Reload();
    }

    private bool CanGoNextDay() => Day < 14;

    /// <summary>跳转到练习页，携带当前任务（含题目与 kpId）。</summary>
    [RelayCommand]
    private void StartTask(TodayTaskItem? task)
    {
        if (task is null) return;
        _nav.Navigate(AppPage.Practice, task);
    }

    /// <summary>无计划时的引导：先诊断，再生成计划。</summary>
    [RelayCommand]
    private void GeneratePlan() => _nav.Navigate(AppPage.Plan);

    [RelayCommand]
    private void OpenGraph() => _nav.Navigate(AppPage.Graph);

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(BudgetRatio));
        OnPropertyChanged(nameof(IsOverBudget));
        OnPropertyChanged(nameof(BudgetText));
        PreviousDayCommand.NotifyCanExecuteChanged();
        NextDayCommand.NotifyCanExecuteChanged();
    }
}
