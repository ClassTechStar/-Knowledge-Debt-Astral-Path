using CommunityToolkit.Mvvm.ComponentModel;

namespace AstralPath.Shared.ViewModels;

/// <summary>
/// 视图模型基类（方案 §11.1：MVVM + CommunityToolkit.Mvvm 源生成器）。
/// 统一承载「忙碌 / 错误 / 页脚合规声明」三类跨页面状态。
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>页面标题，供 Shell 顶栏显示。</summary>
    public abstract string Title { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>页脚合规声明（方案 §15：不构成处分依据）。</summary>
    public string FooterDisclaimer { get; } = Shared.DemoMeta.FooterDisclaimer;

    /// <summary>
    /// 由 Shell 在「首次导航」与「切换学生 / 教师视图」时调用。
    /// 默认空实现，仅需要上下文的页面覆写（方案 §11.8 导航契约）。
    /// </summary>
    public virtual void Load(PageContext context) { }

    /// <summary>统一的加载包装：捕获异常写入 <see cref="ErrorMessage"/>，避免 UI 崩溃。</summary>
    protected async Task RunGuardedAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>同步版本（本地数据源多为同步计算，无需线程切换）。</summary>
    protected void RunGuarded(Action action)
    {
        ErrorMessage = null;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
