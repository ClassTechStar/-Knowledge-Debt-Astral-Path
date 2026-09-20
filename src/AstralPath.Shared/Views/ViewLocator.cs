using AstralPath.Shared.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace AstralPath.Shared.Views;

/// <summary>
/// VM → View 定位器（方案 §11.8）。
///
/// 为什么不用 XAML 的隐式 DataTemplate 列表：
/// <list type="bullet">
///   <item>隐式模板必须写在 <c>Application.DataTemplates</c>，Desktop 与 Mobile 会各存一份，
///         新增页面时极易漏改一端；</item>
///   <item>显式 <c>switch</c> 是编译期检查的，删掉视图会直接编译失败，不会静默退化为显示类型名；</item>
///   <item>不用反射，NativeAOT/裁剪安全（方案 §14.10）。</item>
/// </list>
/// 两端只需在 App.axaml 里登记一行 <c>&lt;views:ViewLocator /&gt;</c>。
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        TodayViewModel => new TodayView(),
        GraphViewModel => new GraphView(),
        PlanViewModel => new PlanView(),
        PracticeViewModel => new PracticeView(),
        DebtListViewModel => new DebtListView(),
        ProgressViewModel => new ProgressView(),
        TeacherViewModel => new TeacherView(),
        WhatIfViewModel => new WhatIfView(),
        ProfileViewModel => new ProfileView(),
        KnowledgeViewModel => new KnowledgeView(),
        SettingsViewModel => new SettingsView(),
        _ => null
    };

    public bool Match(object? data) => data is ViewModelBase;
}
