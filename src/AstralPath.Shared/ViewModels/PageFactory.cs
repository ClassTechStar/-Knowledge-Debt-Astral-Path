using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;

namespace AstralPath.Shared.ViewModels;

/// <summary>
/// 页面工厂（方案 §11.8）：把 <see cref="AppPage"/> 映射到页面视图模型。
/// 两端 Shell 共用同一份映射，保证 Desktop / Mobile 页面集合与行为一致。
///
/// 未登记的页面直接抛异常而不是回退到某个默认页——静默回退会让「加了导航项但忘了页面」
/// 这类缺陷以「点了没反应」的形式出现，很难被发现。
/// </summary>
public sealed class PageFactory
{
    private readonly IAppDataSource _data;
    private readonly INavigationService _nav;

    public PageFactory(IAppDataSource data, INavigationService nav)
    {
        _data = data;
        _nav = nav;
    }

    public ViewModelBase Create(AppPage page) => page switch
    {
        AppPage.Today => new TodayViewModel(_data, _nav),
        AppPage.Graph => new GraphViewModel(_data, _nav),
        AppPage.Plan => new PlanViewModel(_data, _nav),
        AppPage.Practice => new PracticeViewModel(_data, _nav),
        AppPage.DebtList => new DebtListViewModel(_data, _nav),
        AppPage.Progress => new ProgressViewModel(_data, _nav),
        AppPage.Teacher => new TeacherViewModel(_data, _nav),
        AppPage.WhatIf => new WhatIfViewModel(_data, _nav),
        AppPage.Profile => new ProfileViewModel(_data, _nav),
        AppPage.Knowledge => new KnowledgeViewModel(_data, _nav),
        AppPage.Settings => new SettingsViewModel(_data, _nav),
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "该页面尚未在 PageFactory 中登记")
    };
}
