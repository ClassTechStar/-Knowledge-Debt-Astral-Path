using AstralPath.Contracts;
using AstralPath.Shared;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using AstralPath.Shared.ViewModels;
using AstralPath.Shared.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AstralPath.Mobile.Views;
using Xunit;

namespace AstralPath.Desktop.Tests;

/// <summary>
/// 双端布局与响应式（方案 §12.3 / §12.4 / §13 / 附录 X.1）。
///
/// 触达下限：Shared 主题给出桌面下限 44px；移动端由 <c>Mobile/App.axaml</c> 覆盖为 48px。
/// 本测试在共享下限（44px）上断言，移动端 Tab 另按 48px 单独断言。
/// </summary>
public sealed class TodayViewResponsiveTests
{
    private const double DesktopWidth = 1440;
    private const double DesktopHeight = 900;
    private const double MobileWidth = 390;
    private const double MobileHeight = 844;
    private const double SharedTouchFloor = 44;

    [AvaloniaTheory]
    [InlineData(DesktopWidth, DesktopHeight)]
    [InlineData(MobileWidth, MobileHeight)]
    public void 今日页在两种尺寸下都能渲染出任务卡且触达达标(double width, double height)
    {
        AppSettings.Current.Reset();

        var (shell, _) = Composition.CreateShell(touchTarget: 44);
        var window = new ShellWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var today = window.GetVisualDescendants().OfType<TodayView>().FirstOrDefault();
        Assert.NotNull(today);

        var list = today!.FindControl<ItemsControl>("TaskList");
        Assert.NotNull(list);
        Assert.True(list!.ItemCount > 0, $"{width}x{height} 下今日任务不应为空");

        // 只检查真正渲染出来的按钮：空态卡在 HasTasks=true 时是折叠的，其子元素高度为 0 属正常
        var buttons = today.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible)
            .ToList();
        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.True(
            b.Bounds.Height >= SharedTouchFloor - 0.5,
            $"{width}x{height} 下按钮高度 {b.Bounds.Height} 小于触达下限 {SharedTouchFloor}"));

        window.Close();
    }

    [AvaloniaFact]
    public void 窄屏判定与断点一致()
    {
        AppSettings.Current.Reset();
        var (shell, _) = Composition.CreateShell(44);
        var window = new ShellWindow { DataContext = shell, Width = MobileWidth, Height = MobileHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.True(shell.IsNarrow);
        Assert.False(shell.IsWide);
        Assert.False(window.FindControl<Border>("LeftNav")!.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void 移动端外壳渲染出五个底部Tab且内容区有页面()
    {
        AppSettings.Current.Reset();
        var (shell, _) = Composition.CreateShell(touchTarget: 48);

        var mobileShell = new MobileShell { DataContext = shell, Width = MobileWidth, Height = MobileHeight };
        var window = new Window { Content = mobileShell, Width = MobileWidth, Height = MobileHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal(5, shell.MobileTabs.Count);
        Assert.Equal(
            new[] { AppPage.Today, AppPage.Practice, AppPage.DebtList, AppPage.Progress, AppPage.Profile },
            shell.MobileTabs.Select(t => t.Page).ToArray());

        // 移动端触达下限 48px（Mobile/App.axaml 声明）
        Assert.Equal(48, AppSettings.Current.TouchTarget);

        var tabButtons = mobileShell.GetVisualDescendants().OfType<Button>()
            .Where(b => shell.MobileTabs.Any(t => t.Label == b.Content?.ToString()
                                                  || b.GetVisualDescendants().OfType<TextBlock>()
                                                      .Any(tb => tb.Text == t.Label)))
            .ToList();
        Assert.Equal(5, tabButtons.Count);
        Assert.All(tabButtons, b => Assert.True(b.Bounds.Height >= 48 - 0.5,
            $"移动端 Tab 高度 {b.Bounds.Height} 小于 48px 下限"));

        // 启动页必须是今日
        Assert.Equal(AppPage.Today, shell.CurrentPage);
        Assert.IsType<TodayViewModel>(shell.Current);

        window.Close();
    }

    [AvaloniaFact]
    public void 今日页包含预算胶囊与免责声明且无评价性措辞()
    {
        AppSettings.Current.Reset();
        var (shell, _) = Composition.CreateShell(44);
        var window = new ShellWindow { DataContext = shell, Width = DesktopWidth, Height = DesktopHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var today = window.GetVisualDescendants().OfType<TodayView>().Single();
        var texts = today.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty).ToList();

        Assert.Contains(texts, t => t.Contains("预算", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("不构成处分依据", StringComparison.Ordinal));

        // 伦理约束：不得出现排名 / 惩罚性措辞。
        // 注意「处分」一词会出现在必需的免责声明「不构成处分依据」里，故单独排除该句后再检查。
        var withoutDisclaimer = texts
            .Where(t => !t.Contains("不构成处分依据", StringComparison.Ordinal))
            .ToList();
        foreach (var word in new[] { "排名", "处分", "惩罚", "倒数" })
        {
            Assert.DoesNotContain(withoutDisclaimer, t => t.Contains(word, StringComparison.Ordinal));
        }

        window.Close();
    }

    [AvaloniaFact]
    public void 今日页任务来自真实规划器且可作答更新掌握度()
    {
        AppSettings.Current.Reset();
        var data = new OfflineDemoDataSource();
        var today = new TodayViewModel(data, new NavigationService());
        today.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));

        Assert.NotEmpty(today.Tasks);
        Assert.True(today.TotalMinutes > 0);
        Assert.False(today.IsEmpty);
        Assert.Equal("今日", today.Title);

        var first = today.Tasks[0];
        var before = data.GetMastery(DemoMeta.StudentAId).First(m => m.KpId == first.Dto.KpId).RecentAcc;

        var attempt = data.SubmitAttempt(new CreateAttemptRequest(
            DemoMeta.StudentAId,
            first.Dto.PlanItemId,
            first.Dto.KpId,
            first.QuestionId,
            Correct: true,
            SelfConf: 4));

        Assert.True(attempt.Correct);
        var after = data.GetMastery(DemoMeta.StudentAId).First(m => m.KpId == first.Dto.KpId).RecentAcc;
        Assert.True(after >= before, "答对后近期正确率不应下降");
    }
}
