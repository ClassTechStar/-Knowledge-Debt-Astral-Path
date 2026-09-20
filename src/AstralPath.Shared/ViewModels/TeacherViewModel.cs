using System.Collections.ObjectModel;
using AstralPath.Contracts;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AstralPath.Shared.ViewModels;

/// <summary>
/// 班级热点行（方案 §15）。
///
/// 伦理硬约束：这里只承载**聚合**信息（受影响人数、平均 impact），
/// 不含任何学生身份、精确分数或排名，也不提供「处分 / 惩罚」类措辞。
/// </summary>
public sealed record HotspotRow(HotspotDto Dto)
{
    public string FromKpName => Dto.FromKpName;

    public string ToKpName => Dto.ToKpName;

    /// <summary>「前置知识点 → 后继知识点」。</summary>
    public string Route => $"{Dto.FromKpName} → {Dto.ToKpName}";

    public int StudentCount => Dto.StudentCount;

    public double AvgImpact => Dto.AvgImpact;

    public string CountText => $"{Dto.StudentCount} 人受影响";

    /// <summary>平均 impact，保留 2 位小数。</summary>
    public string ImpactText => $"平均 impact {Dto.AvgImpact:0.00}";

    public string AccessibleName => $"{Route}，{CountText}，{ImpactText}。";
}

/// <summary>
/// 教师视图（方案 §15）：班级热点聚合页。
///
/// 本页的灵魂是 **fail-closed**：没有有效授权时教师端什么都看不到，
/// 且必须把「为什么看不到」用一句固定文案讲清楚，而不是给一张空白页。
/// </summary>
public sealed partial class TeacherViewModel : ViewModelBase
{
    /// <summary>
    /// 教师可见性的授权用途标识。单一事实源在 <see cref="ShellViewModel.TeacherViewPurpose"/>，
    /// 与 API / 种子数据保持同一字符串，避免出现第二份互不相干的 consent。
    /// </summary>
    public const string Purpose = ShellViewModel.TeacherViewPurpose;

    /// <summary>方案 §15 指定文案：撤销授权后的教师视图空态，不得改写。</summary>
    public const string RevokedNoticeText = "已撤销授权：教师视图已更新为空。";

    /// <summary>k-匿名抑制提示（方案 §19.3）。</summary>
    public const string SuppressedNoticeText = "热点桶过小（少于 3 人），已按 k-匿名抑制展示";

    /// <summary>合规说明（方案 §15）。</summary>
    public const string ComplianceText =
        "教师端只看到聚合后的班级热点，不显示任何学生的精确分数、排名或个人债边明细；" +
        "无有效授权时默认不可见（fail-closed）；撤销授权会立即清除教师端缓存。";

    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;
    private PageContext _ctx = PageContext.ForStudent(Shared.DemoMeta.StudentAId, "演示学生");

    public TeacherViewModel(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
        Reload();
    }

    public override string Title => "教师视图 · 班级热点";

    public ObservableCollection<HotspotRow> Hotspots { get; } = new();

    [ObservableProperty]
    private string _teacherId = string.Empty;

    [ObservableProperty]
    private string _classId = string.Empty;

    /// <summary>有效授权（granted 且 allowTeacher）的学生数。</summary>
    [ObservableProperty]
    private int _authorizedCount;

    /// <summary>k-匿名抑制旗标（由数据源给出）。</summary>
    [ObservableProperty]
    private bool _suppressed;

    /// <summary>数据源给出的不可见原因，必须原样呈现（fail-closed 的可解释性）。</summary>
    [ObservableProperty]
    private string _emptyReason = string.Empty;

    [ObservableProperty]
    private string _consentState = "none";

    [ObservableProperty]
    private bool _consentAllowTeacher;

    /// <summary>一次性操作反馈（授权 / 撤销后的状态条文案）。</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>consent 审计信息，供学生确认「谁在什么时候拿到了可见性」。</summary>
    [ObservableProperty]
    private string _consentDetail = string.Empty;

    public bool HasHotspots => Hotspots.Count > 0;

    /// <summary>fail-closed：无有效授权，或授权后仍然拿不到任何热点。</summary>
    public bool IsFailClosed => AuthorizedCount == 0 || Hotspots.Count == 0;

    /// <summary>撤销 / 无授权时额外固定显示的那一句（方案 §15 指定文案）。</summary>
    public bool ShowRevokedNotice => AuthorizedCount == 0;

    /// <summary>
    /// k-匿名抑制提示：仅在「确实有授权数据」时提示，避免与 fail-closed 空态语义混淆。
    /// </summary>
    public bool ShowSuppressedNotice => Suppressed && AuthorizedCount > 0;

    public string RevokedNotice => RevokedNoticeText;

    public string SuppressedNotice => SuppressedNoticeText;

    public string Compliance => ComplianceText;

    /// <summary>空态卡正文：保证卡片永远有内容，且包含数据源给出的原因。</summary>
    public string EmptyText => string.IsNullOrWhiteSpace(EmptyReason)
        ? "当前没有可见的班级热点。"
        : EmptyReason;

    public override void Load(PageContext context)
    {
        _ctx = context;
        Reload();
    }

    private void Reload() => RunGuarded(() =>
    {
        var response = _data.GetHotspots(_ctx.TeacherId);

        TeacherId = response.TeacherId;
        ClassId = response.ClassId;
        AuthorizedCount = response.AuthorizedCount;
        Suppressed = response.Suppressed;
        EmptyReason = response.EmptyReason;

        Hotspots.Clear();
        foreach (var hotspot in response.Hotspots.OrderByDescending(h => h.AvgImpact))
            Hotspots.Add(new HotspotRow(hotspot));

        RefreshConsent();
        NotifyDerived();
    });

    private void RefreshConsent()
    {
        var consent = _data.GetConsent(_ctx.StudentId, _ctx.TeacherId, Purpose);
        ConsentState = consent?.State ?? "none";
        ConsentAllowTeacher = consent?.AllowTeacher ?? false;
        ConsentDetail = consent is null
            ? "尚未产生授权记录"
            : $"审计号 {consent.AuditId} · 更新于 {consent.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    [RelayCommand]
    private void Refresh() => Reload();

    /// <summary>学生视角：授权教师查看班级热点（不含精确分数）。</summary>
    [RelayCommand]
    private void GrantConsent() => RunGuarded(() =>
    {
        _data.Grant(_ctx.StudentId, _ctx.TeacherId, Purpose);
        StatusMessage = "已授权：教师可在班级热点中看到聚合后的债边（不含精确分数与个人明细）。";
        Reload();
    });

    /// <summary>学生视角：撤销授权（fail-closed，教师端立即清空并清除缓存）。</summary>
    [RelayCommand]
    private void RevokeConsent() => RunGuarded(() =>
    {
        _data.Revoke(_ctx.StudentId, _ctx.TeacherId, Purpose);
        StatusMessage = RevokedNoticeText;
        Reload();
    });

    /// <summary>本页含学生视角的 consent 演示区，提供回到画像页的入口。</summary>
    [RelayCommand]
    private void OpenProfile() => _nav.Navigate(AppPage.Profile);

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(HasHotspots));
        OnPropertyChanged(nameof(IsFailClosed));
        OnPropertyChanged(nameof(ShowRevokedNotice));
        OnPropertyChanged(nameof(ShowSuppressedNotice));
        OnPropertyChanged(nameof(EmptyText));
    }
}
