using AstralPath.Contracts;
using AstralPath.Shared;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using AstralPath.Shared.ViewModels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AstralPath.Desktop.Tests;

/// <summary>Shell 导航、响应式断点与页面解析（方案 §11.8 / §12.4 / §13）。</summary>
public sealed class ShellNavigationTests
{
    [AvaloniaFact]
    public void 逐个导航到全部页面都不报错且高亮正确()
    {
        AppSettings.Current.Reset();
        var (shell, _) = Composition.CreateShell(touchTarget: 44);
        var window = new ShellWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var item in shell.NavItems.ToList())
        {
            item.Navigate.Execute(item.Page);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(item.Page, shell.CurrentPage);
            Assert.NotNull(shell.Current);
            Assert.False(string.IsNullOrWhiteSpace(shell.Current!.Title));
            Assert.Null(shell.ErrorMessage);
            Assert.True(item.IsActive, $"{item.Label} 应处于选中态");
            Assert.Single(shell.NavItems, n => n.IsActive);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void 每个页面都有独立视图模型且都能解析出视图()
    {
        AppSettings.Current.Reset();
        var data = new OfflineDemoDataSource();
        var nav = new NavigationService();
        var factory = new PageFactory(data, nav);
        var locator = new Shared.Views.ViewLocator();

        var pages = Enum.GetValues<AppPage>();

        foreach (var page in pages)
        {
            var vm = factory.Create(page);
            Assert.False(string.IsNullOrWhiteSpace(vm.Title), $"{page} 的标题不应为空");
            Assert.NotNull(locator.Build(vm));
        }

        // 防止「工厂退化为全部返回同一个页面」这类静默错误
        var distinctTypes = pages.Select(p => factory.Create(p).GetType()).Distinct().Count();
        Assert.Equal(pages.Length, distinctTypes);
    }

    [AvaloniaFact]
    public void 宽屏显示左右栏_窄屏收起()
    {
        AppSettings.Current.Reset();
        var (shell, _) = Composition.CreateShell(touchTarget: 44);
        var window = new ShellWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.IsWide);
        Assert.False(shell.IsNarrow);
        Assert.True(window.FindControl<Border>("LeftNav")!.IsVisible);
        Assert.True(window.FindControl<Border>("RightInfo")!.IsVisible);

        window.Width = 390;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.True(shell.IsNarrow, $"宽度 {shell.ShellWidth} 应判定为窄屏");
        Assert.False(shell.IsWide);
        Assert.False(window.FindControl<Border>("LeftNav")!.IsVisible);
        Assert.False(window.FindControl<Border>("RightInfo")!.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void 快捷键与命令能切换学生并撤销授权()
    {
        AppSettings.Current.Reset();
        var (shell, _) = Composition.CreateShell(touchTarget: 44);
        var window = new ShellWindow { DataContext = shell, Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, shell.Students.Count);

        shell.SelectStudentCommand.Execute(DemoMeta.StudentBId);
        Assert.Equal(DemoMeta.StudentBId, shell.SelectedStudent!.StudentId);

        shell.SelectStudentCommand.Execute(DemoMeta.StudentAId);
        Assert.Equal(DemoMeta.StudentAId, shell.SelectedStudent!.StudentId);

        shell.ToggleStudentCommand.Execute(null);
        Assert.Equal(DemoMeta.StudentBId, shell.SelectedStudent!.StudentId);

        window.Close();
    }

    [AvaloniaFact]
    public void 撤销授权后教师视图为空且给出方案指定文案()
    {
        AppSettings.Current.Reset();
        var (shell, data) = Composition.CreateShell(touchTarget: 44);

        shell.GrantTeacherConsentCommand.Execute(null);
        Assert.True(shell.ConsentAllowTeacher);
        Assert.Equal("granted", shell.ConsentState);

        shell.RevokeTeacherConsentCommand.Execute(null);

        Assert.False(shell.ConsentAllowTeacher);
        Assert.Equal("revoked", shell.ConsentState);
        Assert.Equal("已撤销授权：教师视图已更新为空。", shell.StatusMessage);
        Assert.True(shell.TeacherViewEmpty);

        var hotspots = data.GetHotspots(DemoMeta.TeacherId);
        Assert.Equal(0, hotspots.AuthorizedCount);
        Assert.Empty(hotspots.Hotspots);
        Assert.Contains("fail-closed", hotspots.EmptyReason, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void 撤销授权会清除教师端缓存_重新授权后恢复()
    {
        AppSettings.Current.Reset();
        var (shell, data) = Composition.CreateShell(touchTarget: 44);

        shell.GrantTeacherConsentCommand.Execute(null);
        var granted = data.GetHotspots(DemoMeta.TeacherId);
        Assert.Equal(1, granted.AuthorizedCount);

        shell.RevokeTeacherConsentCommand.Execute(null);
        Assert.Equal(0, data.GetHotspots(DemoMeta.TeacherId).AuthorizedCount);

        shell.GrantTeacherConsentCommand.Execute(null);
        Assert.Equal(1, data.GetHotspots(DemoMeta.TeacherId).AuthorizedCount);
    }
}
