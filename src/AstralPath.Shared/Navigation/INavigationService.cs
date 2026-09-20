namespace AstralPath.Shared.Navigation;

/// <summary>页面标识（方案 §11.8：共享 <c>AppPage</c> 枚举，两端导航共用）。</summary>
public enum AppPage
{
    Today,
    Graph,
    Plan,
    Practice,
    Teacher,
    WhatIf,
    Profile,
    Knowledge,
    Settings,
    DebtList,
    Progress
}

/// <summary>导航服务（方案 §11.8：<c>Navigate(page, parameter)</c> + <c>Navigated</c> 事件）。</summary>
public interface INavigationService
{
    AppPage Current { get; }

    /// <summary>当前页面参数（如从图谱跳今日时携带 kpId）。</summary>
    object? Parameter { get; }

    event EventHandler<AppPage>? Navigated;

    void Navigate(AppPage page, object? parameter = null);
}

/// <summary>默认实现：仅记录当前页并广播事件，由各端 Shell 订阅后切换内容区。</summary>
public sealed class NavigationService : INavigationService
{
    public AppPage Current { get; private set; } = AppPage.Today;

    public object? Parameter { get; private set; }

    public event EventHandler<AppPage>? Navigated;

    public void Navigate(AppPage page, object? parameter = null)
    {
        Current = page;
        Parameter = parameter;
        Navigated?.Invoke(this, page);
    }
}
