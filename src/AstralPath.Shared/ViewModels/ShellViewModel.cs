using System.Windows.Input;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>侧边/底部导航项。命令由 Shell 注入，避免在 XAML 里做祖先绑定。</summary>
public sealed partial class NavItem : ObservableObject
{
    public NavItem(AppPage page, string label, string glyph, bool mobileTab, ICommand navigate)
    {
        Page = page;
        Label = label;
        Glyph = glyph;
        IsMobileTab = mobileTab;
        Navigate = navigate;
    }

    public AppPage Page { get; }

    public string Label { get; }

    /// <summary>简单字形图标（不依赖外部图标字体，避免打包体积与缺字风险）。</summary>
    public string Glyph { get; }

    /// <summary>是否出现在移动端底部 5 Tab（方案 §13：今日/练习/债边/进度/我的）。</summary>
    public bool IsMobileTab { get; }

    public ICommand Navigate { get; }

    /// <summary>当前页高亮；由 <see cref="ShellViewModel"/> 统一维护。</summary>
    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// 应用外壳视图模型（方案 §11.8）。
///
/// 职责：
/// <list type="bullet">
///   <item>导航宿主：按 <see cref="AppPage"/> 惰性创建并缓存页面 VM，切换时注入 <see cref="PageContext"/>；</item>
///   <item>学生切换：演示 A/B 两生，切换后广播到所有已创建页面；</item>
///   <item>consent 门禁：学生可一键撤销教师可见性（fail-closed），并给出空态文案；</item>
///   <item>响应式断点：由各端 Shell 视图回写宽度，决定三栏 / 单栏布局。</item>
/// </list>
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    /// <summary>
    /// 教师可见性的授权用途标识。必须与 API / 种子数据一致（<c>teacher_hotspots</c>），
    /// 否则会出现「学生以为撤销了、教师端仍可见」的双份 consent 漏洞。
    /// </summary>
    public const string TeacherViewPurpose = "teacher_hotspots";

    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private readonly PageFactory _factory;
    private readonly Dictionary<AppPage, ViewModelBase> _pages = new();

    private PageContext _context;

    public ShellViewModel(IAppDataSource data, INavigationService nav, PageFactory factory)
    {
        _data = data;
        _nav = nav;
        _factory = factory;

        Students = data.Students;
        _selectedStudent = Students.FirstOrDefault();
        _context = BuildStudentContext();

        NavItems = new List<NavItem>
        {
            new(AppPage.Today, "今日", "☀", true, NavigateCommand),
            new(AppPage.Graph, "图谱", "◉", false, NavigateCommand),
            new(AppPage.Plan, "计划", "▤", false, NavigateCommand),
            new(AppPage.Practice, "练习", "✎", true, NavigateCommand),
            new(AppPage.DebtList, "债边", "⚑", true, NavigateCommand),
            new(AppPage.Progress, "进度", "▲", true, NavigateCommand),
            new(AppPage.Teacher, "教师视图", "⚖", false, NavigateCommand),
            new(AppPage.WhatIf, "What-if", "⇄", false, NavigateCommand),
            new(AppPage.Profile, "我的画像", "◍", true, NavigateCommand),
            new(AppPage.Knowledge, "知识库", "▣", false, NavigateCommand),
            new(AppPage.Settings, "设置", "⚙", false, NavigateCommand)
        };

        MobileTabs = NavItems.Where(n => n.IsMobileTab).ToList();

        _nav.Navigated += (_, page) => Show(page);
        Show(_nav.Current);
        RefreshConsent();
    }

    // ── 顶栏 ───────────────────────────────────────────────────

    public override string Title => Current?.Title ?? "今日";

    /// <summary>数据源模式（离线演示 / API），顶栏可见，避免把演示数据误当生产。</summary>
    public string ModeLabel => _data.ModeLabel;

    public IReadOnlyList<NavItem> NavItems { get; }

    public IReadOnlyList<NavItem> MobileTabs { get; }

    public IReadOnlyList<StudentSummary> Students { get; }

    [ObservableProperty]
    private StudentSummary? _selectedStudent;

    [ObservableProperty]
    private ViewModelBase? _current;

    [ObservableProperty]
    private AppPage _currentPage = AppPage.Today;

    /// <summary>一次性操作反馈（撤销授权、重新生成计划等），供 a11y LiveRegion 播报。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    partial void OnSelectedStudentChanged(StudentSummary? value)
    {
        if (value is null) return;
        _context = BuildStudentContext();
        RefreshAllPages();
        RefreshConsent();
    }

    partial void OnCurrentChanged(ViewModelBase? value)
    {
        OnPropertyChanged(nameof(Title));
        foreach (var item in NavItems) item.IsActive = item.Page == CurrentPage;
    }

    // ── 响应式断点（由各端视图回写） ────────────────────────────

    [ObservableProperty]
    private double _shellWidth;

    /// <summary>宽屏：左侧导航 + 内容 + 右侧信息栏三栏（方案 §12.4 桌面布局）。</summary>
    public bool IsWide => ShellWidth >= 1180;

    /// <summary>窄屏：仅内容单栏 + 底部 Tab（方案 §13 移动布局）。</summary>
    public bool IsNarrow => ShellWidth < 900;

    partial void OnShellWidthChanged(double value)
    {
        OnPropertyChanged(nameof(IsWide));
        OnPropertyChanged(nameof(IsNarrow));
    }

    // ── 导航 ───────────────────────────────────────────────────

    [RelayCommand]
    private void Navigate(AppPage page) => _nav.Navigate(page);

    [RelayCommand]
    private void NavigateTo(string pageName)
    {
        if (Enum.TryParse<AppPage>(pageName, ignoreCase: true, out var page))
        {
            _nav.Navigate(page);
        }
    }

    private void Show(AppPage page)
    {
        CurrentPage = page;

        if (!_pages.TryGetValue(page, out var vm))
        {
            vm = _factory.Create(page);
            _pages[page] = vm;
        }

        vm.Load(_context);
        Current = vm;
        StatusMessage = null;
    }

    private void RefreshAllPages()
    {
        foreach (var vm in _pages.Values) vm.Load(_context);
    }

    private PageContext BuildStudentContext()
    {
        var id = SelectedStudent?.StudentId ?? Shared.DemoMeta.StudentAId;
        var name = SelectedStudent?.DisplayName ?? "演示学生";
        return PageContext.ForStudent(id, name);
    }

    // ── 快捷键（方案 §12.4：Ctrl+1/2 切学生、Ctrl+D 诊断、Ctrl+T 教师、Ctrl+R 撤销）──

    [RelayCommand]
    private void SelectStudent(string? studentId)
    {
        if (string.IsNullOrWhiteSpace(studentId))
        {
            SelectedStudent = Students.Count > 1 ? Students[1] : Students.FirstOrDefault();
            return;
        }

        var match = Students.FirstOrDefault(s => s.StudentId == studentId);
        if (match is not null) SelectedStudent = match;
    }

    [RelayCommand]
    private void ToggleStudent()
    {
        if (Students.Count < 2) return;
        var idx = 0;
        for (var i = 0; i < Students.Count; i++)
        {
            if (ReferenceEquals(Students[i], SelectedStudent)) { idx = i; break; }
        }
        SelectedStudent = Students[(idx + 1) % Students.Count];
    }

    [RelayCommand]
    private void OpenTeacherView() => _nav.Navigate(AppPage.Teacher);

    [RelayCommand]
    private void RevokeTeacherConsent()
    {
        RunGuarded(() =>
        {
            var dto = _data.Revoke(_context.StudentId, _context.TeacherId, TeacherViewPurpose);
            ConsentState = dto.State;
            ConsentAllowTeacher = dto.AllowTeacher;
            // 方案 §15 指定空态文案：撤销后教师视图必须显示这一句
            StatusMessage = "已撤销授权：教师视图已更新为空。";
            RefreshAllPages();
        });
    }

    [RelayCommand]
    private void GrantTeacherConsent()
    {
        RunGuarded(() =>
        {
            var dto = _data.Grant(_context.StudentId, _context.TeacherId, TeacherViewPurpose);
            ConsentState = dto.State;
            ConsentAllowTeacher = dto.AllowTeacher;
            StatusMessage = "已授权：教师可在班级热点中看到你的债边（不含精确分数）。";
            RefreshAllPages();
        });
    }

    [RelayCommand]
    private void RebuildPlan()
    {
        RunGuarded(() =>
        {
            var plan = _data.CreatePlan(_context.StudentId);
            StatusMessage = $"已重新生成 {plan.Days.Count} 天计划（{plan.ConstraintsChecked} 项约束已校验）。";
            RefreshAllPages();
        });
    }

    // ── consent 状态 ───────────────────────────────────────────

    [ObservableProperty]
    private string _consentState = "none";

    [ObservableProperty]
    private bool _consentAllowTeacher;

    /// <summary>consent 撤销后的合规空态（方案 §15）。</summary>
    public string ConsentEmptyText => "已撤销授权：教师视图已更新为空。";

    public bool TeacherViewEmpty => !ConsentAllowTeacher;

    private void RefreshConsent()
    {
        var dto = _data.GetConsent(_context.StudentId, _context.TeacherId, TeacherViewPurpose);
        ConsentState = dto?.State ?? "none";
        ConsentAllowTeacher = dto?.AllowTeacher ?? false;
        OnPropertyChanged(nameof(TeacherViewEmpty));
    }

    partial void OnConsentAllowTeacherChanged(bool value) => OnPropertyChanged(nameof(TeacherViewEmpty));

    /// <summary>供页面 VM 读取当前上下文（如今日页跳练习页时携带 kpId）。</summary>
    public PageContext Context => _context;

    /// <summary>页面工厂（供子页面创建嵌套 VM，如画像页内嵌时间线）。</summary>
    internal PageFactory Factory => _factory;

    /// <summary>跨页面统一取数入口。</summary>
    internal IAppDataSource Data => _data;
}
