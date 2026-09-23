using System.Collections.ObjectModel;
using AstralPath.Core.Formatting;
using AstralPath.Mobile.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Mobile.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
}

public sealed record TaskCard(int Id, string KpId, string KpTitle, int Minutes, string Layer, string Status);

public partial class TodayViewModel : PageViewModel
{
    private readonly LocalStore _store;
    [ObservableProperty] private string _coach = "今天先完成相关真题，再回到课程债边。";
    [ObservableProperty] private string _emptyText = "今天没有任务，休息一下。";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private int _day = 1;

    public ObservableCollection<TaskCard> Tasks { get; } = new();

    public TodayViewModel(LocalStore store) => _store = store;

    [RelayCommand]
    private void Load()
    {
        Tasks.Clear();
        var items = _store.GetPlan(Day).Where(i => i.Status != "done").Take(3).ToList();
        if (items.Count == 0)
        {
            _store.BuildAndSavePlan();
            items = _store.GetPlan(Day).Where(i => i.Status != "done").Take(3).ToList();
        }
        foreach (var i in items)
            Tasks.Add(new TaskCard(i.Id, i.KpId, _store.TitleOf(i.KpId), i.Minutes, i.Layer, i.Status));
        var all = _store.GetPlan(Day);
        Progress = all.Count == 0 ? 0 : (double)all.Count(i => i.Status == "done") / Math.Max(3, all.Count);
        OnPropertyChanged(nameof(Tasks));
    }

    [RelayCommand]
    private void NextDay()
    {
        if (Day < 14) Day++;
        Load();
    }
}

public partial class PracticeViewModel : PageViewModel
{
    private readonly LocalStore _store;
    private string _kpId = "N3";
    private int _qIndex;

    [ObservableProperty] private string _stem = "";
    [ObservableProperty] private string _answer = "";
    [ObservableProperty] private int _confidence = 3;
    [ObservableProperty] private string _feedback = "";
    [ObservableProperty] private int _streak;
    [ObservableProperty] private bool _answered;
    [ObservableProperty] private string _kpTitle = "";

    public ObservableCollection<string> Options { get; } = new();
    public ObservableCollection<bool> Selected { get; } = new();

    public PracticeViewModel(LocalStore store) => _store = store;

    [RelayCommand]
    private void LoadForKp(string? kpId)
    {
        _kpId = string.IsNullOrWhiteSpace(kpId) ? _kpId : kpId!;
        _qIndex = 0;
        LoadQuestion();
    }

    [RelayCommand]
    private void Next()
    {
        _qIndex++;
        LoadQuestion();
    }

    private void LoadQuestion()
    {
        Answered = false;
        Feedback = "";
        Options.Clear();
        Selected.Clear();
        var q = _store.NextQuestion(_kpId, _qIndex);
        if (q is null)
        {
            Stem = "暂无题目";
            return;
        }
        KpTitle = _store.TitleOf(q.KpId);
        _kpId = q.KpId;
        Stem = q.Stem;
        // 简易选项：正确 + 3 干扰
        Options.Add(q.CorrectPayload);
        Options.Add("以上都不对");
        Options.Add("不确定");
        Options.Add("与本题无关");
        for (var i = 0; i < Options.Count; i++) Selected.Add(false);
    }

    [RelayCommand]
    private void Submit()
    {
        var idx = Selected.ToList().FindIndex(s => s);
        if (idx < 0) { Feedback = "请先选择一项"; return; }
        var correct = idx == 0;
        var r = _store.SubmitAttempt(_kpId, correct, Confidence);
        Streak = r.SaleStreak;
        Feedback = r.Feedback;
        Answered = true;
    }
}

public sealed record DebtCard(string FromKp, string ToKp, string FromTitle, string ToTitle, double Impact, string Status);

public partial class DebtListViewModel : PageViewModel
{
    private readonly LocalStore _store;
    public ObservableCollection<DebtCard> Items { get; } = new();
    [ObservableProperty] private string _emptyText = "暂无债边，保持节奏。";
    [ObservableProperty] private string _planBrief = "";

    public DebtListViewModel(LocalStore store) => _store = store;

    [RelayCommand]
    private void Refresh()
    {
        _store.RefreshDebtsFromGraph();
        Items.Clear();
        foreach (var d in _store.GetDebts())
            Items.Add(new DebtCard(d.FromKp, d.ToKp, _store.TitleOf(d.FromKp), _store.TitleOf(d.ToKp),
                d.Impact, d.Status));
        var plan = _store.GetPlan();
        PlanBrief = plan.Count == 0 ? "尚未生成计划" : $"计划 {plan.Count} 项 · 已完成 {plan.Count(p => p.Status == "done")}";
        OnPropertyChanged(nameof(Items));
    }

    [RelayCommand]
    private void BuildPlan()
    {
        _store.BuildAndSavePlan();
        Refresh();
    }
}

public partial class ProgressViewModel : PageViewModel
{
    private readonly LocalStore _store;
    [ObservableProperty] private int _streak;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _summary = "";

    public ObservableCollection<string> Lines { get; } = new();

    public ProgressViewModel(LocalStore store) => _store = store;

    [RelayCommand]
    private void Refresh()
    {
        var debts = _store.GetDebts();
        Streak = debts.Count == 0 ? 2 : debts.Max(d => d.Streak);
        Progress = Math.Min(1.0, Streak / 2.0);
        var open = debts.Count(d => d.Status == "open");
        var repairing = debts.Count(d => d.Status == "repairing");
        Summary = $"开放 {open} · 修复中 {repairing} · 销账进度 {Streak}/2";
        Lines.Clear();
        foreach (var d in debts.Take(6))
            Lines.Add($"{_store.TitleOf(d.FromKp)} → {_store.TitleOf(d.ToKp)}  impact={NumberFormatter.FormatImpact(d.Impact)}  {d.Status}");
        OnPropertyChanged(nameof(Lines));
    }
}

public partial class SettingsViewModel : PageViewModel
{
    private readonly LocalStore _store;
    [ObservableProperty] private string _exportPath = "";
    [ObservableProperty] private string _statusMessage = "";

    public SettingsViewModel(LocalStore store) => _store = store;

    [RelayCommand]
    private void Export()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "astralpath-export.json");
            ExportPath = _store.ExportJson(path);
            StatusMessage = "导出路径";
        }
        catch (Exception ex)
        {
            StatusMessage = "导出失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void Reset()
    {
        _store.ResetAll();
        StatusMessage = "已重置";
    }

    [RelayCommand]
    private void GrantConsent() => StatusMessage = "已保存";

    [RelayCommand]
    private void RevokeConsent() => StatusMessage = "已保存";
}
