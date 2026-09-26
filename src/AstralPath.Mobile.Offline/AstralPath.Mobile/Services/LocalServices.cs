using AstralPath.Core.Algorithms;
using AstralPath.Core.Filtering;
using AstralPath.Core.Models;
using AstralPath.Core.Planner;

namespace AstralPath.Mobile.Services;

/// <summary>App 本地计划行（local_plan_item 表的映射；天数由 Core 生成器分配）。</summary>
public sealed record PlanItem(int Id, int Day, string KpId, string Layer, int Minutes, string Status);

/// <summary>本地图包：只使用内置边，禁止发明先修边。</summary>
public sealed class LocalGraphService
{
    private readonly List<KpNode> _nodes = new();
    private readonly List<KpEdge> _edges = new();

    public IReadOnlyList<KpNode> Nodes => _nodes;
    public IReadOnlyList<KpEdge> Edges => _edges;

    public void Load(IEnumerable<KpNode> nodes, IEnumerable<KpEdge> edges)
    {
        _nodes.Clear(); _edges.Clear();
        _nodes.AddRange(nodes);
        _edges.AddRange(edges);
    }

    public IReadOnlyList<(string From, string To)> EdgePairs()
        => _edges.Select(e => (e.FromKp, e.ToKp)).ToList();

    public IReadOnlyList<string> TopoOrder()
        => GraphTopology.TopologicalOrder(_nodes.Select(n => n.KpId).ToList(), EdgePairs());

    public IReadOnlyList<string> Prereqs(string kpId)
        => GraphTopology.DirectPrereqs(kpId, EdgePairs());
}

public sealed class LocalQuestionService
{
    private readonly List<QuestionRow> _bank = new();
    public void Load(IEnumerable<QuestionRow> q) { _bank.Clear(); _bank.AddRange(q); }
    public QuestionRow? Next(string kpId, int afterIndex = 0)
    {
        var list = _bank.Where(q => q.KpId == kpId).ToList();
        if (list.Count == 0) return null;
        return list[afterIndex % list.Count];
    }
}

/// <summary>计划生成 v2（间隔重复 + 影响优先）+ K1–K5 校验（对齐主 Core Planner API）。</summary>
public sealed class LocalPlanService
{
    /// <summary>生成 + 自修复（TryGeneratePlan 按天分组）→ 展开为 App 本地计划行。</summary>
    public IReadOnlyList<PlanItem> Build(
        IReadOnlyList<(string FromKp, string ToKp, double Impact)> openDebts,
        IReadOnlyDictionary<string, string> titles,
        int horizonDays = 14,
        int dayBudget = 40)
    {
        var debts = openDebts
            .Select(d => new ScannedDebtInput(d.FromKp, d.ToKp, d.Impact, 10))
            .ToList();
        PlannerConstraintChecker.TryGeneratePlan(debts, titles, dayBudget, horizonDays, out var schedule, out _);
        var result = new List<PlanItem>();
        var id = 1;
        foreach (var day in schedule.Days)
        {
            foreach (var item in day.Items)
            {
                var layer = item.Type is "drill" or "quiz" ? "challenge" : "core";
                result.Add(new PlanItem(id++, day.Day, item.KpId, layer, item.EstMin, "todo"));
            }
        }
        return result;
    }

    public ConstraintCheckResult Validate(IReadOnlyList<PlanItem> items,
        IReadOnlyList<(string From, string To)> openRefs, int dayBudget = 40)
    {
        var days = items.GroupBy(i => i.Day).OrderBy(g => g.Key)
            .Select(g => new PlanDayInput(g.Key, g.Select(i => new PlanItemInput(
                i.Id.ToString(), i.KpId, i.KpId, "drill", 2, i.Minutes,
                $"巩固「{i.KpId}」", Array.Empty<string>())).ToList()))
            .ToList();
        return PlannerConstraintChecker.Check(days, dayBudget, openRefs);
    }
}

/// <summary>作答 + 掌握度 + 销账推进（状态迁移只走 SaleStateMachine）。</summary>
public sealed class LocalAttemptService
{
    public (MasteryRow mastery, SaleState sale) Apply(
        MasteryRow mastery,
        SaleState sale,
        IReadOnlyList<double> prereqScores,
        bool correct,
        int selfConf,
        DateTime now)
    {
        var acc = correct ? 1.0 : 0.0;
        var streak = correct ? mastery.Streak + 1 : 0;
        var raw = correct ? Math.Min(100, mastery.Raw + 6) : Math.Max(0, mastery.Raw - 4);
        var age = 0.0;
        var score = MasteryCalculator.ComputeScore(new MasteryInput(10, correct ? 8 : 3, selfConf, age, streak, prereqScores));
        var next = mastery with { Raw = raw, AgeDays = age, Streak = streak, Score = score };
        var nextSale = SaleStateMachine.Transition(sale, new SaleAttempt(correct, acc, selfConf));
        return (next, nextSale);
    }
}

/// <summary>叙事槽位装配；禁词命中降级模板。LLM 可选，禁止改数字。</summary>
public sealed class LocalNarrativeService
{
    private readonly BannedWordFilter _filter;

    public LocalNarrativeService(IEnumerable<string> bannedWords)
        => _filter = new BannedWordFilter(bannedWords);

    public string BuildDebtStory(string fromTitle, string toTitle, double scoreFrom, double scoreTo, double impact)
    {
        var text = $"你在{toTitle}相关练习上多次出错，回溯显示{fromTitle}掌握度为 {scoreFrom:0.######}，" +
                   $"当前知识点掌握度为 {scoreTo:0.######}，影响分 {impact:0.######}。建议先巩固{fromTitle}，再回到{toTitle}。";
        return _filter.Sanitize(text, "这段内容暂时无法展示，请查看教材原文或联系老师。");
    }
}
