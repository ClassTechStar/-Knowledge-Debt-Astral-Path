using AstralPath.Contracts;
using AstralPath.Shared;
using AstralPath.Shared.Navigation;
using AstralPath.Shared.Services;
using AstralPath.Shared.ViewModels;
using Xunit;

namespace AstralPath.Desktop.Tests;

/// <summary>
/// 各页面视图模型的行为断言（方案 §11–§17 的硬约束）。
///
/// 这些用例只依赖 VM 与数据源，不需要 Avalonia 平台，因此用普通 <c>[Fact]</c>，
/// 跑得快且能精确定位是哪条业务规则被破坏。
/// </summary>
public sealed class PageViewModelTests
{
    private static (T Vm, IAppDataSource Data, NavigationService Nav) Create<T>(Func<IAppDataSource, INavigationService, T> factory)
        where T : ViewModelBase
    {
        AppSettings.Current.Reset();
        var data = new OfflineDemoDataSource();
        var nav = new NavigationService();
        var vm = factory(data, nav);
        vm.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));
        return (vm, data, nav);
    }

    // ── 计划 ───────────────────────────────────────────────────

    [Fact]
    public void 生成计划后得到不超十四天的计划且约束已校验()
    {
        var (vm, _, _) = Create((d, n) => new PlanViewModel(d, n));

        vm.GeneratePlanCommand.Execute(null);

        Assert.True(vm.HasPlan);
        Assert.False(vm.IsEmpty);
        Assert.NotNull(vm.Plan);
        Assert.Contains("约束", vm.ConstraintsText, StringComparison.Ordinal);

        // 计划跨度由实际债边数量决定，但不得超过 14 天上限
        Assert.InRange(vm.Days.Count, 1, 14);
        Assert.Equal(Enumerable.Range(1, vm.Days.Count), vm.Days.Select(d => d.Day));

        // 硬约束：K1–K5 校验结果与违规列表必须一致——不允许「未通过却不说为什么」
        Assert.Equal(vm.Plan!.ConstraintsChecked, vm.NoViolations);
        Assert.Equal(!vm.Plan.ConstraintsChecked, vm.HasViolations);
        Assert.Equal(vm.Plan.ConstraintViolations.Count, vm.Violations.Count);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void 计划每天都不超过每日预算_除非标记为超预算()
    {
        var (vm, _, _) = Create((d, n) => new PlanViewModel(d, n));
        vm.GeneratePlanCommand.Execute(null);

        var budget = vm.Plan!.DayBudgetMin;
        foreach (var day in vm.Days)
        {
            // 超预算必须被显式标注出来，不能静默超支
            Assert.Equal(day.Minutes > budget, day.IsOverBudget);
        }
    }

    // ── 进度 ───────────────────────────────────────────────────

    [Fact]
    public void 进度页给出掌握度分布且明确不做学生排名()
    {
        var (vm, _, _) = Create((d, n) => new ProgressViewModel(d, n));

        Assert.NotEmpty(vm.Rows);
        Assert.False(vm.IsEmpty);
        Assert.Contains("掌握良好", vm.BandDistributionText, StringComparison.Ordinal);
        Assert.Contains("平均 score", vm.AverageText, StringComparison.Ordinal);
        Assert.Contains("不对学生做分数排名", vm.RankDisclaimer, StringComparison.Ordinal);

        // 每行都必须有 band 中文标签，不能只靠颜色区分
        Assert.All(vm.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.BandText)));
    }

    // ── 债边 ───────────────────────────────────────────────────

    [Fact]
    public void 债边清单可筛选并执行销账检查()
    {
        var (vm, _, _) = Create((d, n) => new DebtListViewModel(d, n));

        Assert.True(vm.HasEdges);
        var first = vm.Edges[0];

        vm.CheckSaleCommand.Execute(first);

        // 销账检查必须给出结论：要么已销账，要么还差几次连续达标
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.True(vm.StatusCleared || vm.StatusNotCleared);
    }

    // ── 练习 ───────────────────────────────────────────────────

    [Fact]
    public void 练习页信心值必填_未填时不可提交()
    {
        var data = new OfflineDemoDataSource();
        var nav = new NavigationService();
        var today = new TodayViewModel(data, nav);
        today.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));
        Assert.NotEmpty(today.Tasks);

        nav.Navigate(AppPage.Practice, today.Tasks[0]);
        var vm = new PracticeViewModel(data, nav);
        vm.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));

        Assert.True(vm.HasTask);
        Assert.NotEmpty(vm.Options);
        Assert.False(string.IsNullOrWhiteSpace(vm.Stem));

        // 未选信心 → 未填 → 提交禁用
        Assert.Equal(0, vm.Confidence);
        Assert.Equal(0, vm.ConfidenceSlider);
        Assert.True(vm.NeedsConfidence);
        Assert.Contains("请先选择信心值", vm.ConfidenceHint, StringComparison.Ordinal);
        Assert.Contains("未填", vm.ConfidenceText, StringComparison.Ordinal);
        Assert.False(vm.SubmitCommand.CanExecute(null));

        vm.SelectOptionCommand.Execute(0);
        Assert.Equal(0, vm.SelectedOptionIndex);
        Assert.False(vm.SubmitCommand.CanExecute(null));

        // 填了信心才能提交
        vm.ConfidenceSlider = 4;
        Assert.Equal(4, vm.Confidence);
        Assert.False(vm.NeedsConfidence);
        Assert.True(vm.SubmitCommand.CanExecute(null));

        vm.SubmitCommand.Execute(null);
        Assert.True(vm.IsSubmitted);
        Assert.False(vm.SubmitCommand.CanExecute(null));
        Assert.False(string.IsNullOrWhiteSpace(vm.CorrectAnswerText));
        Assert.False(string.IsNullOrWhiteSpace(vm.ResultTitle));
    }

    [Fact]
    public void 练习页作答会写入尝试记录()
    {
        var data = new OfflineDemoDataSource();
        var nav = new NavigationService();
        var today = new TodayViewModel(data, nav);
        today.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));

        var task = today.Tasks[0];
        nav.Navigate(AppPage.Practice, task);
        var vm = new PracticeViewModel(data, nav);
        vm.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));

        var correctIndex = task.Dto.CorrectIndex;
        vm.SelectOptionCommand.Execute(correctIndex);
        vm.ConfidenceSlider = 5;
        vm.SubmitCommand.Execute(null);

        Assert.True(vm.IsCorrect, "选中正确选项时应判定为正确");
        Assert.True(vm.LatencyMs >= 0);
    }

    // ── What-if ───────────────────────────────────────────────

    [Fact]
    public void WhatIf初始推演与真实债边一致且可实时复算()
    {
        var (vm, _, _) = Create((d, n) => new WhatIfViewModel(d, n));

        Assert.True(vm.HasEdges);
        Assert.NotNull(vm.SelectedEdge);

        // 初始参数由债边反解得到，因此推演值应等于真实 impact（端上复算与 API 同源）
        Assert.True(vm.IsDetected);
        Assert.Equal(vm.SelectedEdge!.Impact, vm.SimImpact, 6);

        // 提高 score_c（下游掌握得更好）应使 impact 下降
        var before = vm.SimImpact;
        vm.ScoreToSlider = Math.Min(100, vm.ScoreToSlider + 30);
        Assert.True(vm.SimImpact < before, "下游掌握度提升后 impact 应下降");

        Assert.Contains("impact = freq", vm.FormulaText, StringComparison.Ordinal);
    }

    // ── 教师端 ─────────────────────────────────────────────────

    [Fact]
    public void 教师端默认fail_closed且撤销后显示指定空态()
    {
        AppSettings.Current.Reset();
        var data = new OfflineDemoDataSource();
        var vm = new TeacherViewModel(data, new NavigationService());
        vm.Load(PageContext.ForTeacher(DemoMeta.StudentAId, "演示学生 A"));

        // 种子数据里 demo-student-a 已授权，先撤销以验证 fail-closed
        vm.RevokeConsentCommand.Execute(null);

        Assert.Equal(0, vm.AuthorizedCount);
        Assert.True(vm.IsFailClosed);
        Assert.True(vm.ShowRevokedNotice);
        Assert.Equal("已撤销授权：教师视图已更新为空。", vm.RevokedNotice);
        Assert.False(vm.HasHotspots);
        Assert.Contains("fail-closed", vm.Compliance, StringComparison.Ordinal);

        // 重新授权后应恢复可见
        vm.GrantConsentCommand.Execute(null);
        Assert.Equal(1, vm.AuthorizedCount);
        Assert.False(vm.IsFailClosed);
        Assert.True(vm.HasHotspots);
        Assert.All(vm.Hotspots, h => Assert.True(h.StudentCount >= 1));
    }

    [Fact]
    public void 教师端不出现学生姓名与分数()
    {
        AppSettings.Current.Reset();
        var data = new OfflineDemoDataSource();
        var vm = new TeacherViewModel(data, new NavigationService());
        vm.Load(PageContext.ForTeacher(DemoMeta.StudentAId, "演示学生 A"));

        var texts = vm.Hotspots.Select(h => h.AccessibleName).ToList();
        foreach (var name in new[] { "王小明", "李华" })
        {
            Assert.DoesNotContain(texts, t => t.Contains(name, StringComparison.Ordinal));
        }
        Assert.DoesNotContain(texts, t => t.Contains("score", StringComparison.OrdinalIgnoreCase));
    }

    // ── 画像 ───────────────────────────────────────────────────

    [Fact]
    public void 画像时间线与热力图在产生作答后有数据()
    {
        AppSettings.Current.Reset();
        var data = new OfflineDemoDataSource();

        // 时间线按日聚合作答记录；先制造两次作答，验证端上确实能画出时间线
        // （回归：数据源曾把 days 计数当数组读，导致时间线恒为空）
        var today = new TodayViewModel(data, new NavigationService());
        today.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));
        foreach (var task in today.Tasks.Take(2))
        {
            data.SubmitAttempt(new CreateAttemptRequest(
                DemoMeta.StudentAId, task.Dto.PlanItemId, task.Dto.KpId,
                task.QuestionId, Correct: true, SelfConf: 4));
        }

        var vm = new ProfileViewModel(data, new NavigationService());
        vm.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));

        Assert.False(vm.OptOut);
        Assert.True(vm.ShowVisualizations);
        Assert.False(vm.ShowOptOutCard);
        Assert.True(vm.SampleCount > 0, "有作答后样本数应大于 0");
        if (vm.ErrorMessage is not null) Assert.Fail($"画像加载失败：{vm.ErrorMessage}");

        Assert.True(vm.HasTimeline, "画像时间线不应为空");
        Assert.True(vm.HasHeatColumns, "画像热力图不应为空");
        Assert.True(vm.ShowRadarCanvas, "雷达图应有轴可画");

        Assert.All(vm.Timeline, t => Assert.False(string.IsNullOrWhiteSpace(t.AccuracyText)));
        // 热力图不能只靠颜色：每格必须带数值文本
        Assert.All(vm.HeatColumns, c => Assert.All(c.Cells, cell =>
            Assert.False(string.IsNullOrWhiteSpace(cell.ValueText))));
        // 雷达图同样不能只靠图形：每根轴必须有可读数值
        Assert.All(vm.RadarAxes, a => Assert.False(string.IsNullOrWhiteSpace(a.LabelText)));
    }

    [Fact]
    public void 画像标签为空时给出空态而不是空白()
    {
        var (vm, _, _) = Create((d, n) => new ProfileViewModel(d, n));

        // 离线演示不预置画像标签，页面必须走空态分支而不是留白
        Assert.False(vm.HasTags);
        Assert.Empty(vm.Tags);
        Assert.True(vm.ShowVisualizations);
    }

    [Fact]
    public void 关闭个性化后隐藏全部画像可视化且可重新开启()
    {
        AppSettings.Current.Reset();
        var data = new OfflineDemoDataSource();
        var nav = new NavigationService();

        // 先产生作答，使重新开启后能验证可视化确实恢复
        var today = new TodayViewModel(data, nav);
        today.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));
        var task = today.Tasks[0];
        data.SubmitAttempt(new CreateAttemptRequest(
            DemoMeta.StudentAId, task.Dto.PlanItemId, task.Dto.KpId,
            task.QuestionId, Correct: true, SelfConf: 4));

        var vm = new ProfileViewModel(data, nav);
        vm.Load(PageContext.ForStudent(DemoMeta.StudentAId, "演示学生 A"));
        Assert.True(vm.ShowVisualizations);

        vm.ClosePersonalizationCommand.Execute(null);

        Assert.True(vm.OptOut);
        Assert.True(vm.ShowOptOutCard);
        Assert.False(vm.ShowVisualizations);
        Assert.False(vm.ShowRadarCanvas);
        Assert.Empty(vm.RadarAxes);
        Assert.Empty(vm.Tags);
        Assert.Empty(vm.Timeline);
        Assert.Empty(vm.HeatColumns);
        Assert.Equal(
            "已关闭个性化：你的行为数据不再用于画像，界面不展示任何画像可视化。",
            vm.OptOutNotice);

        vm.ReopenPersonalizationCommand.Execute(null);

        Assert.False(vm.OptOut);
        Assert.True(vm.ShowVisualizations);
        Assert.True(vm.HasTimeline);
    }

    // ── 知识库 ─────────────────────────────────────────────────

    [Fact]
    public void 知识库可新建文档并检索到()
    {
        var (vm, _, _) = Create((d, n) => new KnowledgeViewModel(d, n));

        // 离线演示不预置知识库文档；空库必须走空态而不是空白
        Assert.False(vm.HasDocuments);
        Assert.True(vm.IsDocumentsEmpty);

        Assert.False(vm.CanCreate);
        vm.NewTitle = "应收账款减值测试笔记";
        vm.NewText = "应收账款减值需要区分单项计提与组合计提，并按预期信用损失模型估计。";
        Assert.True(vm.CanCreate);

        vm.CreateCommand.Execute(null);

        Assert.True(vm.HasDocuments);
        Assert.Contains(vm.Documents, d => d.Title == "应收账款减值测试笔记");
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));

        vm.SearchQuery = "应收账款";
        vm.SearchCommand.Execute(null);
        Assert.True(vm.HasHits, "新建文档后应能被检索到");
        Assert.All(vm.Hits, h => Assert.False(string.IsNullOrWhiteSpace(h.ScoreText)));

        // 检索不命中时应给出明确空态原因，而不是静默空白
        vm.SearchQuery = "完全不存在的关键词XYZQ";
        vm.SearchCommand.Execute(null);
        Assert.False(vm.HasHits);
        Assert.False(string.IsNullOrWhiteSpace(vm.SearchEmptyReason));
    }

    // ── 设置 ───────────────────────────────────────────────────

    [Fact]
    public void 设置页双向绑定可访问性开关并可恢复默认()
    {
        var (vm, _, _) = Create((d, n) => new SettingsViewModel(d, n));

        vm.Settings.ColorBlindSafe = true;
        vm.Settings.HighContrast = true;
        vm.Settings.ReduceMotion = true;

        Assert.True(AppSettings.Current.ColorBlindSafe);

        vm.ResetSettingsCommand.Execute(null);

        Assert.False(AppSettings.Current.ColorBlindSafe);
        Assert.False(AppSettings.Current.HighContrast);
        Assert.False(AppSettings.Current.ReduceMotion);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
        Assert.Contains("1.3.0", vm.AppVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void 设置页展示数据源模式与版本号()
    {
        var (vm, _, _) = Create((d, n) => new SettingsViewModel(d, n));

        Assert.Equal("离线演示", vm.ModeLabel);
        Assert.True(vm.GraphVer > 0, "应显示图版本号");
        Assert.False(string.IsNullOrWhiteSpace(vm.WeightVer));
        Assert.Equal(DemoMeta.StudentAId, vm.StudentId);
        Assert.Equal(DemoMeta.FooterDisclaimer, vm.Disclaimer);
    }

    // ── 图谱 ───────────────────────────────────────────────────

    [Fact]
    public void 图谱页选中节点后给出关联债边说明()
    {
        var (vm, _, _) = Create((d, n) => new GraphViewModel(d, n));

        Assert.NotNull(vm.View);
        Assert.True(vm.HasDebts);
        Assert.False(string.IsNullOrWhiteSpace(vm.GraphMeta));

        var node = vm.View!.Nodes[0];
        vm.SelectNodeCommand.Execute(node.Id);

        Assert.Equal(node.Id, vm.SelectedNodeId);
        Assert.Contains(node.Id, vm.SelectedNodeTitle, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedNodeDetail));

        vm.ClearSelectionCommand.Execute(null);
        Assert.Null(vm.SelectedNodeId);
    }

    [Fact]
    public void 图谱页TopN变化会重新诊断()
    {
        var (vm, _, _) = Create((d, n) => new GraphViewModel(d, n));

        vm.TopN = 3;
        Assert.True(vm.Debts.Count <= 3, $"TopN=3 时不应超过 3 条，实际 {vm.Debts.Count}");

        vm.TopN = 20;
        Assert.True(vm.Debts.Count > 3);
    }

    [Fact]
    public void 图谱页叙事带安全旗标时不冒充个性化诊断()
    {
        var (vm, _, _) = Create((d, n) => new GraphViewModel(d, n));

        Assert.All(vm.Debts, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Story));
            // 降级模板必须显式标注
            Assert.Equal(d.Degraded, !string.IsNullOrWhiteSpace(d.SafetyText));
        });
    }
}
