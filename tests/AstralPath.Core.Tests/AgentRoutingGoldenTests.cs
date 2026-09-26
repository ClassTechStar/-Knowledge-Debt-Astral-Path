using System.Text.Json;
using AstralPath.Core.Agent;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// 意图路由金样（P2：JS/C# 双实现消费同一张 tools/agent-intents.json 锚点表）。
/// 表一致性由 scripts/verify_agent_intents.py 三方对拍；本测试锁 **C# 路由行为**：
/// 危机词最高优先、负例占比 ≥60% 才落（A4 回归）、多锚点加分（A34）、
/// 15 个意图的归属与文档（P1 提示词 §4.3）一致。
/// </summary>
public class AgentRoutingGoldenTests
{
    private static (AgentRouter Router, JsonElement Cases) Build()
    {
        var dir = GoldenDir;
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "agent", "routing.json")));
        var root = doc.RootElement;
        var crisis = root.GetProperty("crisis").EnumerateArray().Select(v => v.GetString()!).ToList();
        var banned = root.GetProperty("banned").EnumerateArray().Select(v => v.GetString()!).ToList();
        var negative = root.GetProperty("negative").EnumerateArray().Select(v => v.GetString()!).ToList();
        return (new AgentRouter(crisis, banned, negative, DefaultAgentIntents.Table), root.GetProperty("cases"));
    }

    private static string GoldenDir
    {
        get
        {
            // 环境变量 → 常规候选 → 从测试输出目录逐级向上回溯（不硬编码机器特定路径）
            var env = Environment.GetEnvironmentVariable("ASTRALPATH_GOLDEN_DIR");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "eval", "golden"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "eval", "golden"),
                Path.Combine(Directory.GetCurrentDirectory(), "eval", "golden")
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (Directory.Exists(full)) return full;
            }
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                var full = Path.Combine(dir, "eval", "golden");
                if (Directory.Exists(full)) return Path.GetFullPath(full);
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            throw new DirectoryNotFoundException("eval/golden not found");
        }
    }

    public static TheoryData<string> CaseIds()
    {
        var (_, cases) = Build();
        var data = new TheoryData<string>();
        foreach (var c in cases.EnumerateArray())
            data.Add(c.GetProperty("id").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Routes_As_Specified(string caseId)
    {
        var (router, cases) = Build();
        var caseEl = cases.EnumerateArray().First(c => c.GetProperty("id").GetString() == caseId);
        var text = caseEl.GetProperty("text").GetString();
        var result = router.Classify(text, AgentRoles.Student);

        var message = $"{caseId} \"{text}\" → {result.IntentId}/{result.Decision} ({result.Trace})";
        if (caseEl.TryGetProperty("intent", out var intent))
            Assert.True(string.Equals(result.IntentId, intent.GetString(), StringComparison.Ordinal), message);
        if (caseEl.TryGetProperty("decision", out var decision))
            Assert.True(string.Equals(result.Decision, decision.GetString(), StringComparison.Ordinal), message);
        else
            Assert.True(result.Decision is "execute" or "clarify", message + "（不得落兜底）");
    }

    [Fact]
    public void DefaultTable_Covers_All_15_Documented_Intents()
    {
        var expected = new[]
        {
            "debt.diagnose", "debt.explain", "plan.create", "plan.rebalance", "today.tasks",
            "practice.start", "progress.check", "graph.view", "material.parse", "kb.search",
            "profile.view", "profile.optout", "whatif.simulate", "sale.check", "meta.help"
        };
        Assert.Equal(expected, DefaultAgentIntents.Table.Select(i => i.IntentId).ToArray());
    }
}
