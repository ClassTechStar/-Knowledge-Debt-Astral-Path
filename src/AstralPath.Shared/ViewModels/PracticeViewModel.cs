using System.Collections.ObjectModel;
using System.Diagnostics;
using AstralPath.Contracts;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>
/// 选项行：把题目选项包装为可直接绑定的单选行（前缀 A/B/C/D 由展示属性给出）。
/// </summary>
public sealed partial class PracticeOption : ObservableObject
{
    public PracticeOption(int index, string label, string text)
    {
        Index = index;
        Label = label;
        Text = text;
    }

    /// <summary>选项序号（0 起），与 <c>CorrectIndex</c> 同一坐标系。</summary>
    public int Index { get; }

    /// <summary>选项字母前缀（A/B/C/D）。</summary>
    public string Label { get; }

    /// <summary>选项原文。</summary>
    public string Text { get; }

    /// <summary>带前缀的展示文本，如「A. 权责发生制」。</summary>
    public string Display => $"{Label}. {Text}";

    /// <summary>无障碍名称：读屏用户能听到选项字母与原文（方案 附录 X）。</summary>
    public string AccessibleName => $"选项 {Label}：{Text}";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 答题页（方案 §12.3 桌面 / §13.1 移动）。
///
/// 闭环：题干与选项 → 信心值（1–5，必填）→ 提交写回作答 → 判题结果 → 下一题。
/// 计时：进入题目即启动 <see cref="Stopwatch"/>，提交时取毫秒数作为 <c>latency_ms</c>，
/// 不做 UI 定时刷新（避免无意义的重绘）。
/// </summary>
public sealed partial class PracticeViewModel : ViewModelBase
{
    private static readonly string[] OptionLabels = { "A", "B", "C", "D", "E", "F" };

    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private readonly Stopwatch _watch = new();

    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    private TodayTaskItem? _task;
    private int _correctIndex = -1;
    private bool _loading;

    public PracticeViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "练习";

    /// <summary>当前题目的选项（单选）。</summary>
    public ObservableCollection<PracticeOption> Options { get; } = new();

    [ObservableProperty]
    private string _stem = string.Empty;

    [ObservableProperty]
    private string _kpName = string.Empty;

    [ObservableProperty]
    private string _taskMeta = string.Empty;

    [ObservableProperty]
    private string _why = string.Empty;

    [ObservableProperty]
    private int _difficulty;

    [ObservableProperty]
    private int _estMin;

    [ObservableProperty]
    private int _selectedOptionIndex = -1;

    /// <summary>
    /// 信心滑条值（Slider 的 Value 是 double，必须用 double 属性承载）。
    /// 0 表示「尚未选择」——滑条最左端与「未填」一致，避免出现
    /// 「滑条停在 1 但系统认为未填」的自相矛盾状态。
    /// </summary>
    [ObservableProperty]
    private double _confidenceSlider;

    /// <summary>信心值（1–5）；0 表示未填，未填时不可提交。</summary>
    [ObservableProperty]
    private int _confidence;

    [ObservableProperty]
    private bool _isSubmitted;

    [ObservableProperty]
    private bool _isCorrect;

    [ObservableProperty]
    private long _latencyMs;

    [ObservableProperty]
    private string _correctAnswerText = string.Empty;

    [ObservableProperty]
    private string _yourAnswerText = string.Empty;

    [ObservableProperty]
    private string _emptyReason = string.Empty;

    public bool HasTask => _task is not null;

    public bool IsEmpty => _task is null;

    /// <summary>未填信心值时的提示可见性（提交前的必填约束）。</summary>
    public bool NeedsConfidence => !IsSubmitted && Confidence < 1;

    public string ConfidenceHint => "请先选择信心值（1–5）";

    public string ConfidenceText => Confidence < 1 ? "信心值：未填（必填 1–5）" : $"信心值：{Confidence}/5";

    public string SelectedAnswerText => SelectedOptionIndex < 0
        ? "你的选择：（未作答）"
        : $"你的选择：{OptionAt(SelectedOptionIndex)?.Display ?? "（未作答）"}";

    public string ResultTitle => IsCorrect ? "✓ 回答正确" : "✗ 回答有误";

    public string LatencyText => $"本题用时 {LatencyMs / 1000.0:0.0} 秒";

    /// <summary>作答会写回掌握度，这里显式说明影响范围，避免用户以为只是本地标记。</summary>
    public string ResultNote =>
        "这一题会更新该知识点的掌握度：最近正确率与严重度会随作答变化，进而影响相关债边的 impact，" +
        "下次打开今日页或图谱页即可看到新的排序与状态。";

    // 滑条与 5 个单选按钮是等价输入，二者共用同一个信心值。
    public bool IsConf1
    {
        get => Confidence == 1;
        set { if (value) SetConfidence(1); }
    }

    public bool IsConf2
    {
        get => Confidence == 2;
        set { if (value) SetConfidence(2); }
    }

    public bool IsConf3
    {
        get => Confidence == 3;
        set { if (value) SetConfidence(3); }
    }

    public bool IsConf4
    {
        get => Confidence == 4;
        set { if (value) SetConfidence(4); }
    }

    public bool IsConf5
    {
        get => Confidence == 5;
        set { if (value) SetConfidence(5); }
    }

    public override void Load(PageContext context)
    {
        _ctx = context;
        Reload();
    }

    private void Reload() => RunGuarded(() =>
    {
        // 入口参数：今日页/图谱页跳转时携带的具体任务。
        if (_nav.Parameter is TodayTaskItem fromNav)
        {
            LoadTask(fromNav);
            return;
        }

        // 兜底：直接进入练习页（侧边导航）时，取今日的第一个任务。
        var today = _data.GetToday(_ctx.StudentId);
        TodayTaskDto? first = today.Tasks.Count > 0 ? today.Tasks[0] : null;
        if (first is null)
        {
            ClearTask("今日没有可作答的任务。可以先回今日页看看教练建议，或生成一次学习计划。");
            return;
        }

        LoadTask(new TodayTaskItem(first));
    });

    private void LoadTask(TodayTaskItem item)
    {
        _loading = true;
        try
        {
            _task = item;
            _correctIndex = item.Dto.CorrectIndex;

            Stem = item.Dto.Stem;
            KpName = item.KpName;
            TaskMeta = item.Meta;
            Why = item.Why;
            Difficulty = item.Difficulty;
            EstMin = item.EstMin;

            Options.Clear();
            for (var i = 0; i < item.Dto.Options.Count; i++)
            {
                var label = i < OptionLabels.Length ? OptionLabels[i] : (i + 1).ToString();
                Options.Add(new PracticeOption(i, label, item.Dto.Options[i]));
            }

            SelectedOptionIndex = -1;
            Confidence = 0;
            ConfidenceSlider = 0;
            IsSubmitted = false;
            IsCorrect = false;
            LatencyMs = 0;
            CorrectAnswerText = string.Empty;
            YourAnswerText = string.Empty;
            EmptyReason = string.Empty;
        }
        finally
        {
            _loading = false;
        }

        _watch.Restart();
        NotifyDerived();
    }

    private void ClearTask(string reason)
    {
        _loading = true;
        try
        {
            _task = null;
            _correctIndex = -1;
            Options.Clear();
            Stem = string.Empty;
            KpName = string.Empty;
            TaskMeta = string.Empty;
            Why = string.Empty;
            Difficulty = 0;
            EstMin = 0;
            SelectedOptionIndex = -1;
            Confidence = 0;
            ConfidenceSlider = 1;
            IsSubmitted = false;
            IsCorrect = false;
            LatencyMs = 0;
            CorrectAnswerText = string.Empty;
            YourAnswerText = string.Empty;
            EmptyReason = reason;
        }
        finally
        {
            _loading = false;
        }

        _watch.Reset();
        NotifyDerived();
    }

    /// <summary>点选选项（由列表项的单选按钮触发）。</summary>
    [RelayCommand]
    private void SelectOption(int index)
    {
        if (_task is null || index < 0 || index >= Options.Count) return;

        SelectedOptionIndex = index;
        foreach (var option in Options) option.IsSelected = option.Index == index;
        OnPropertyChanged(nameof(SelectedAnswerText));
    }

    partial void OnConfidenceSliderChanged(double value)
    {
        if (_loading) return;

        var rounded = (int)Math.Round(value);
        if (rounded < 1)
        {
            // 滑条最左端（0）不是有效信心值，只表示「尚未选择」
            if (Confidence != 0) Confidence = 0;
            return;
        }

        SetConfidence(rounded);
    }

    private void SetConfidence(int value)
    {
        var clamped = Math.Clamp(value, 1, 5);
        if (Confidence != clamped) Confidence = clamped;
        if (!_loading && Math.Abs(ConfidenceSlider - clamped) > 0.001)
        {
            _loading = true;
            try
            {
                ConfidenceSlider = clamped;
            }
            finally
            {
                _loading = false;
            }
        }
    }

    partial void OnConfidenceChanged(int value)
    {
        OnPropertyChanged(nameof(IsConf1));
        OnPropertyChanged(nameof(IsConf2));
        OnPropertyChanged(nameof(IsConf3));
        OnPropertyChanged(nameof(IsConf4));
        OnPropertyChanged(nameof(IsConf5));
        NotifyDerived();
    }

    /// <summary>提交作答：correct 由「所选序号是否等于 CorrectIndex」判定，同时上报信心值与用时。</summary>
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit() => RunGuarded(() =>
    {
        if (_task is null || Confidence < 1) return;

        var chosen = Options.FirstOrDefault(o => o.IsSelected)?.Index ?? -1;
        SelectedOptionIndex = chosen;
        LatencyMs = _watch.ElapsedMilliseconds;

        var correct = chosen == _correctIndex;
        _data.SubmitAttempt(new CreateAttemptRequest(
            StudentId: _ctx.StudentId,
            PlanItemId: _task.Dto.PlanItemId,
            KpId: _task.Dto.KpId,
            QuestionId: _task.Dto.QuestionId,
            Correct: correct,
            SelfConf: Confidence,
            LatencyMs: LatencyMs));

        IsCorrect = correct;
        IsSubmitted = true;
        CorrectAnswerText = $"正确答案：{OptionAt(_correctIndex)?.Display ?? "（题目未提供）"}";
        YourAnswerText = SelectedAnswerText;
        OnPropertyChanged(nameof(ResultTitle));
        NotifyDerived();
    });

    private bool CanSubmit() => _task is not null && !IsSubmitted && Confidence >= 1;

    /// <summary>下一题：重新取今日任务，按当前题目在列表中的位置顺延；没有下一题则回今日页。</summary>
    [RelayCommand]
    private void NextQuestion() => RunGuarded(() =>
    {
        if (_task is null) return;

        var currentId = _task.Id;
        var tasks = _data.GetToday(_ctx.StudentId).Tasks;

        var index = tasks.FindIndex(t => t.Id == currentId);
        TodayTaskDto? next = index >= 0 && index + 1 < tasks.Count
            ? tasks[index + 1]
            : tasks.FirstOrDefault(t => t.Id != currentId);

        if (next is null)
        {
            _nav.Navigate(AppPage.Today);
            return;
        }

        LoadTask(new TodayTaskItem(next));
    });

    [RelayCommand]
    private void GoToday() => _nav.Navigate(AppPage.Today);

    private PracticeOption? OptionAt(int index)
        => index >= 0 ? Options.FirstOrDefault(o => o.Index == index) : null;

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasTask));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NeedsConfidence));
        OnPropertyChanged(nameof(ConfidenceText));
        OnPropertyChanged(nameof(SelectedAnswerText));
        OnPropertyChanged(nameof(ResultTitle));
        OnPropertyChanged(nameof(LatencyText));
        SubmitCommand.NotifyCanExecuteChanged();
        NextQuestionCommand.NotifyCanExecuteChanged();
    }
}
