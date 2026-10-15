using System.Text;
using System.Text.Json;
using AstralPath.Core.Agent;
using AstralPath.Core.Planner;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// P1 迁移验证：AI 计划官 golden 契约（eval/golden/plan_ai.json）。
/// 覆盖草案解析（类型/难度/时长钳制）、编排四态（合法草案/幻觉剔除/全幻觉回退/传输失败回退）、
/// K3 说明回填、K1 自动压缩。
/// </summary>
public class GoldenPlanAiTests
{
    private static JsonDocument Load()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_GOLDEN_DIR");
        var dir = !string.IsNullOrWhiteSpace(env) && Directory.Exists(env) ? env : FindGoldenDir();
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "plan_ai.json")));
    }

    private static string FindGoldenDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var full = Path.Combine(dir, "eval", "golden");
            if (Directory.Exists(full)) return Path.GetFullPath(full);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        }
        throw new DirectoryNotFoundException("eval/golden not found");
    }

    private static readonly MimoConfig Cfg = new("test-key", "https://mock.local/v1", "mimo-v2.6-pro");

    [Fact]
    public void Golden_PlanAi_DraftParse_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("draftParseCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var expect = c.GetProperty("expect");
            var draft = AiPlanOrchestrator.ParseDraft(c.GetProperty("input").GetString());
            if (!expect.GetProperty("ok").GetBoolean())
            {
                Assert.True(draft == null, $"{id}: expected null draft");
                continue;
            }
            Assert.True(draft != null, $"{id}: expected draft but got null");
            Assert.Equal(expect.GetProperty("items").GetInt32(), draft!.Items.Count);
            if (expect.TryGetProperty("strategyContains", out var sc))
                Assert.Contains(sc.GetString()!, draft.Strategy);
            if (expect.TryGetProperty("type", out var t))
                Assert.Equal(t.GetString(), draft.Items[0].Type);
            if (expect.TryGetProperty("difficulty", out var d))
                Assert.Equal(d.GetInt32(), draft.Items[0].Difficulty);
            if (expect.TryGetProperty("estMin", out var em))
                Assert.Equal(em.GetInt32(), draft.Items[0].EstMin);
            if (expect.TryGetProperty("day", out var day))
                Assert.Equal(day.GetInt32(), draft.Items[0].Day);
        }
    }

    [Fact]
    public async Task Golden_PlanAi_Orchestration_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("orchestrationCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var expect = c.GetProperty("expect");

            MimoClient client;
            if (c.TryGetProperty("draftError", out var err))
            {
                var msg = err.GetString()!;
                client = new MimoClient((req, ct) => throw new HttpRequestException(msg));
            }
            else
            {
                var body = c.GetProperty("draftResponse").GetString()!;
                client = new MimoClient((req, ct) => Task.FromResult(MimoRawResponse.Json(200,
                    "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(body) + "}}]}")));
            }
            var orch = new AiPlanOrchestrator(client);

            var whitelist = c.GetProperty("kpWhitelist").EnumerateArray().Select(x => x.GetString()!).ToList();
            var debts = new List<PlanDebtFact>();
            foreach (var d in c.GetProperty("debts").EnumerateArray())
                debts.Add(new PlanDebtFact(
                    d.GetProperty("fromKp").GetString()!, d.GetProperty("fromName").GetString()!,
                    d.GetProperty("toKp").GetString()!, d.GetProperty("toName").GetString()!,
                    d.GetProperty("impact").GetDouble(), d.GetProperty("estMin").GetInt32()));

            var input = new PlanOrchestrationInput(
                "计算机基础", new[] { "第一章 概述", "第二章 存储" },
                "薄弱点：缓存映射；错题集中度：中", "今日已答 5 题，正确率 60%",
                debts, whitelist, "");

            var result = await orch.OrchestrateAsync(Cfg, input,
                c.GetProperty("dayBudgetMin").GetInt32(), c.GetProperty("horizonDays").GetInt32());

            Assert.Equal(expect.GetProperty("fromAi").GetBoolean(), result.FromAi);
            Assert.Equal(expect.GetProperty("constraintChecked").GetBoolean(), result.ConstraintChecked);
            if (expect.TryGetProperty("dayCount", out var dc))
                Assert.Equal(dc.GetInt32(), result.Days.Count);
            if (expect.TryGetProperty("k3Backfill", out var k3) && k3.GetBoolean())
            {
                // K3 回填：所有含 from_kp 的条目 why 须含先修名
                foreach (var day in result.Days)
                    foreach (var item in day.Items)
                        if (item.FromKpName != null)
                            Assert.Contains(item.FromKpName, item.Why);
            }
            if (expect.TryGetProperty("dayMinutesWithin", out var within))
            {
                foreach (var day in result.Days)
                {
                    var minutes = day.Items.Where(x => x.Status != "dropped").Sum(x => x.EstMin);
                    Assert.True(minutes <= within.GetInt32(),
                        $"{id}: day={day.Day} minutes={minutes} exceeds {within.GetInt32()}");
                }
            }
        }
    }

    [Fact]
    public void AiPlanOrchestrator_Prompts_ContainFourSourcesAndWhitelist()
    {
        var input = new PlanOrchestrationInput(
            "计算机基础", new[] { "第一章 概述" }, "画像摘要文本", "今日快照文本",
            new[] { new PlanDebtFact("k1", "内存寻址", "k2", "缓存映射", 0.8, 10) },
            new[] { "内存寻址", "缓存映射" }, "每天不超过 30 分钟");
        var sys = AiPlanOrchestrator.SystemPrompt;
        Assert.Contains("严格输出 JSON", sys);
        Assert.Contains("白名单", sys);
        Assert.Contains("不要输出 JSON 以外", sys);

        var user = AiPlanOrchestrator.BuildUserPrompt(input, 40, 14);
        Assert.Contains("教材《计算机基础》", user);
        Assert.Contains("章节目录：第一章 概述", user);
        Assert.Contains("学习者画像：画像摘要文本", user);
        Assert.Contains("今日快照：今日快照文本", user);
        Assert.Contains("内存寻址 → 缓存映射", user);
        Assert.Contains("知识点白名单：内存寻址、缓存映射", user);
        Assert.Contains("用户补充要求：每天不超过 30 分钟", user);
    }
}
