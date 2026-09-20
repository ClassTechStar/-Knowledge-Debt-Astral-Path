using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AstralPath.Contracts;
using AstralPath.Core.Formula;
using AstralPath.Graph;

namespace AstralPath.Infrastructure;

/// <summary>根据教材解析结果生成「今日任务」内容（概念卡 + 桥接练习 + 小测）。</summary>
public static class MaterialTaskGenerator
{
    private static readonly string[] ActionTemplates =
    {
        "8 分钟概念卡：先读定义与例子，再口述一遍",
        "3 道桥接题：把前置概念迁到当前章节",
        "5 分钟自测：合上书复述关键结论",
        "对照例题：标出题干考查的知识点"
    };

    public static List<TodayTaskDto> GenerateFromGraph(
        AutoKnowledgeGraph graph,
        string materialName,
        int maxTasks = 6)
    {
        // 优先使用按教材正文生成的真题题库
        var bankTasks = TextbookQuestionBank.ToTodayTasks(materialName, maxTasks);
        if (bankTasks.Count > 0)
            return bankTasks;

        var tasks = new List<TodayTaskDto>();
        var nodes = graph.Nodes.ToList();
        if (nodes.Count == 0) return tasks;

        // 章节节点优先，其次关键词
        var chapters = nodes
            .Where(n => n.Description is "chapter" or "section" or "title"
                        || n.Description.StartsWith("ocr:")
                        || n.Name.StartsWith("第")
                        || n.Name.StartsWith("Chapter", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var terms = nodes
            .Where(n => !chapters.Contains(n))
            .OrderByDescending(n => ParseFreq(n.Description))
            .ToList();

        var plan = new List<(KpNode node, string type, int difficulty, int estMin, string why)>();
        var seq = 0;
        foreach (var ch in chapters.Take(3))
        {
            plan.Add((ch, "concept", 2, 8,
                $"来自《{Truncate(materialName, 18)}》：先建立「{Truncate(ch.Name, 16)}」整体框架"));
            seq++;
        }
        foreach (var term in terms.Take(Math.Max(0, maxTasks - plan.Count)))
        {
            var edge = graph.Edges.FirstOrDefault(e => e.From == term.Id || e.To == term.Id);
            var pair = edge == null ? term.Name : $"{NameOf(graph, edge.From)} → {NameOf(graph, edge.To)}";
            plan.Add((term, seq % 3 == 2 ? "quiz" : "drill", 2 + (seq % 3), 6,
                $"为还 {Truncate(pair, 24)} 的知识债：巩固关键词「{Truncate(term.Name, 12)}」"));
            seq++;
        }

        foreach (var item in plan.Take(maxTasks))
        {
            var q = BuildQuestion(graph, item.node, materialName);
            tasks.Add(new TodayTaskDto(
                Guid.NewGuid().ToString("N"),
                item.node.Id,
                Truncate(item.node.Name, 24),
                item.type,
                item.difficulty,
                item.estMin,
                item.why,
                q.QuestionId,
                q.Stem,
                q.Options,
                q.CorrectIndex,
                null));
        }

        return tasks;
    }

    public static object BuildDayBrief(IReadOnlyList<TodayTaskDto> tasks, string materialName)
    {
        var minutes = tasks.Sum(t => t.EstMin);
        var focus = tasks.FirstOrDefault()?.KpName ?? "综合复习";
        return new
        {
            material = materialName,
            focus,
            totalMinutes = minutes,
            budget = FormulaWeights.DefaultDayBudgetMin,
            coachMessage = tasks.Count == 0
                ? "先在藏书阁解析教材，系统会为你生成今日任务。"
                : $"今天围绕《{Truncate(materialName, 16)}》：先掌握「{Truncate(focus, 14)}」，再完成桥接练习。合计约 {minutes} 分钟。",
            actions = ActionTemplates.Take(Math.Min(3, Math.Max(1, tasks.Count))).ToArray()
        };
    }

    private static (string QuestionId, string Stem, List<string> Options, int CorrectIndex) BuildQuestion(
        AutoKnowledgeGraph graph, KpNode node, string materialName)
    {
        var name = Truncate(node.Name, 28);
        var neighbors = graph.Edges
            .Where(e => e.From == node.Id || e.To == node.Id)
            .Take(2)
            .Select(e => NameOf(graph, e.From == node.Id ? e.To : e.From))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

        string stem;
        List<string> options;
        int correct;

        if (node.Name.StartsWith("第") || node.Description is "chapter" or "title" || (node.Description ?? "").StartsWith("ocr:"))
        {
            stem = $"在《{Truncate(materialName, 16)}》中，关于「{name}」下列哪项更符合章节定位？";
            options = new List<string>
            {
                "这是本章要先建立的主干概念/章节",
                "可以完全跳过，不影响后续",
                "与本书其他章节没有任何关系",
                "只需要背结论，不必理解定义"
            };
            correct = 0;
        }
        else if (neighbors.Count >= 1)
        {
            stem = $"知识点「{name}」在教材中更常与下列哪一概念一起出现？";
            options = new List<string>
            {
                neighbors[0],
                neighbors.Count > 1 ? neighbors[1] : "与本主题无关的操作步骤",
                "完全独立、无任何关联",
                "仅出现在附录索引"
            };
            // 若 neighbors[1] 更接近则仍以 neighbors[0] 为正确项（启发式教学题）
            correct = 0;
        }
        else
        {
            stem = $"复习「{name}」时，更有效的做法是？";
            options = new List<string>
            {
                "先明确定义，再用一个教材例题验证理解",
                "只刷题不看定义",
                "跳过例子直接背公式编号",
                "等到考前再集中看"
            };
            correct = 0;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stem))).ToLowerInvariant();
        return ($"q-{hash[..12]}", stem, options, correct);
    }

    private static string NameOf(AutoKnowledgeGraph graph, string id)
        => graph.Nodes.FirstOrDefault(n => n.Id == id)?.Name ?? id;

    private static int ParseFreq(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return 0;
        var m = Regex.Match(description, @"freq=(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static string Truncate(string s, int n)
    {
        s = (s ?? "").Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }

    public static string SerializeTasks(IEnumerable<TodayTaskDto> tasks)
        => JsonSerializer.Serialize(tasks, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
