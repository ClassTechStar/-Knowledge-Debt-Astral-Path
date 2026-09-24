using System.Text.Json;
using AstralPath.Core.Algorithms;
using AstralPath.Core.Narrative;
using AstralPath.Core.Planner;
using Xunit;

namespace AstralPath.Core.Tests;

public class GoldenSampleTests
{
    private static string GoldenDir
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "eval", "golden"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "eval", "golden"),
                Path.Combine(Directory.GetCurrentDirectory(), "eval", "golden"),
                @"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\eval\golden"
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (Directory.Exists(full)) return full;
            }
            throw new DirectoryNotFoundException("eval/golden not found");
        }
    }

    private static JsonDocument Load(string name)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDir, name)));

    [Fact]
    public void Golden_Score_AllCases()
    {
        using var doc = Load("score.json");
        var tol = doc.RootElement.GetProperty("tolerance").GetDouble();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var expect = c.GetProperty("expect").GetDouble();
            var actual = ScoreCalculator.ComputeScore(
                input.GetProperty("recent_acc").GetDouble(),
                input.GetProperty("sev").GetDouble(),
                input.GetProperty("self_conf").GetDouble());
            Assert.True(Math.Abs(actual - expect) < tol,
                $"{c.GetProperty("id").GetString()}: expect={expect} actual={actual}");
        }
    }

    [Fact]
    public void Golden_Impact_AllCases()
    {
        using var doc = Load("impact.json");
        var tol = doc.RootElement.GetProperty("tolerance").GetDouble();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var expect = c.GetProperty("expect");
            var actual = DebtScannerCompat.ComputeImpactV1(new ImpactInput(
                input.GetProperty("score_p").GetDouble(),
                input.GetProperty("score_c").GetDouble(),
                input.GetProperty("freq").GetInt32(),
                input.GetProperty("days_since_last_error").GetInt32(),
                input.GetProperty("weight").GetDouble()));

            var id = c.GetProperty("id").GetString();
            Assert.Equal(expect.GetProperty("detected").GetBoolean(), actual.Detected);
            Assert.True(Math.Abs(actual.Impact - expect.GetProperty("impact").GetDouble()) < tol,
                $"{id}: impact expect={expect.GetProperty("impact").GetDouble()} actual={actual.Impact}");
            Assert.True(Math.Abs(actual.Recency - expect.GetProperty("recency").GetDouble()) < tol,
                $"{id}: recency expect={expect.GetProperty("recency").GetDouble()} actual={actual.Recency}");
        }
    }

    [Fact]
    public void Golden_PlanK_AllCases()
    {
        using var doc = Load("plan_k.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = c.GetProperty("input");
            var budget = input.GetProperty("day_budget_min").GetInt32();
            var days = new List<PlanDayInput>();
            foreach (var d in input.GetProperty("days").EnumerateArray())
            {
                var items = new List<PlanItemInput>();
                foreach (var item in d.GetProperty("items").EnumerateArray())
                {
                    var why = item.GetProperty("why").GetString() ?? "";
                    var fromName = item.TryGetProperty("from_kp_name", out var fn) ? fn.GetString() ?? "" : "";
                    items.Add(new PlanItemInput(
                        Guid.NewGuid().ToString(),
                        "K12",
                        fromName,
                        item.GetProperty("type").GetString() ?? "concept",
                        item.GetProperty("difficulty").GetInt32(),
                        item.GetProperty("est_min").GetInt32(),
                        why,
                        new[] { "K12", "K88" },
                        fromName));
                }
                days.Add(new PlanDayInput(d.GetProperty("day").GetInt32(), items));
            }

            var topDebts = new List<(string, string)>();
            foreach (var td in input.GetProperty("top_debts").EnumerateArray())
            {
                topDebts.Add((td.GetProperty("from_kp").GetString()!, td.GetProperty("to_kp").GetString()!));
            }

            // For K3 case, why doesn't contain from_kp_name - checker uses FromKpName
            var result = PlannerConstraintChecker.Check(days, budget, topDebts);
            var expectChecked = c.GetProperty("expect").GetProperty("constraints_checked").GetBoolean();
            var id = c.GetProperty("id").GetString();

            // K5 may fire for uncovered top debts on single-day golden inputs; compare expected violation codes primarily
            if (expectChecked)
            {
                Assert.True(result.ConstraintsChecked, $"{id}: expected pass but got {string.Join(",", result.Violations.Select(v => v.Code + ":" + v.Message))}");
            }
            else
            {
                Assert.False(result.ConstraintsChecked, $"{id}: expected fail");
                var expectedViolations = c.GetProperty("expect").GetProperty("violations").EnumerateArray().ToList();
                foreach (var ev in expectedViolations)
                {
                    var code = ev.GetProperty("code").GetString();
                    Assert.Contains(result.Violations, v => v.Code == code);
                }
            }
        }
    }

    [Fact]
    public void Golden_Sale_AllCases()
    {
        using var doc = Load("sale.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var history = new List<SaleProbe>();
            foreach (var h in c.GetProperty("history").EnumerateArray())
            {
                history.Add(new SaleProbe(h.GetProperty("acc").GetDouble(), h.GetProperty("conf").GetInt32()));
            }
            var actual = SaleCompat.IsSaleable(history);
            var expect = c.GetProperty("expect");
            var id = c.GetProperty("id").GetString();
            Assert.Equal(expect.GetProperty("saleable").GetBoolean(), actual.Saleable);
            Assert.Equal(expect.GetProperty("streak").GetInt32(), actual.Streak);
            if (expect.GetProperty("need").ValueKind == JsonValueKind.Null)
                Assert.Null(actual.Need);
            else
                Assert.Equal(expect.GetProperty("need").GetString(), actual.Need);
        }
    }

    [Fact]
    public void Golden_Narrative_AllCases()
    {
        using var doc = Load("narrative.json");
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var draft = c.GetProperty("draft").GetString()!;
            var s = c.GetProperty("slots");
            var slots = new NarrativeSlots(
                s.GetProperty("from_kp").GetString()!,
                s.GetProperty("to_kp").GetString()!,
                s.GetProperty("impact").GetDouble(),
                s.GetProperty("score_from").GetDouble(),
                s.GetProperty("score_to").GetDouble());
            var result = NarrativeGuard.Build(slots, draft);
            var expect = c.GetProperty("expect");
            var id = c.GetProperty("id").GetString();
            Assert.Equal(expect.GetProperty("bannedHit").GetBoolean(), result.BannedHit);
            Assert.Equal(expect.GetProperty("slotFailed").GetBoolean(), result.SlotFailed);
            Assert.Equal(expect.GetProperty("degradedTemplate").GetBoolean(), result.DegradedTemplate);
        }
    }

    [Fact]
    public void SaleStateMachine_OnlyProgressCanWriteCleared()
    {
        Assert.True(SaleCompat.CanWriteCleared("progress-svc"));
        Assert.False(SaleCompat.CanWriteCleared("coach-svc"));
        Assert.False(SaleCompat.CanWriteCleared("assessment-svc"));
        Assert.False(SaleCompat.CanWriteCleared("narrative-svc"));
    }
}
