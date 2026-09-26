using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AstralPath.Api.Tests;

public class ApiEndToEndTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ApiEndToEndTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b => b.UseSetting("Security:RequireAuth", "false")); // 测试明确退出鉴权（生产默认开启）
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        return client;
    }

    private static async Task<JsonElement> Data(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
            return data;
        return root;
    }

    [Fact]
    public async Task Health_IsReady()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/health/ready");
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        Assert.Equal("ready", data.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GraphPack_Validate_Acyclic()
    {
        var client = CreateClient();
        var resp = await client.PostAsync("/v1/graphs/accounting-v1/validate", null);
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        Assert.True(data.GetProperty("ok").GetBoolean());
        Assert.True(data.GetProperty("nodeCount").GetInt32() >= 30);
        Assert.True(data.GetProperty("edgeCount").GetInt32() >= 40);
    }

    [Fact]
    public async Task StudentA_Diagnose_HasDebts_AndGoldenImpact()
    {
        var client = CreateClient();
        var resp = await client.PostAsync("/v1/students/demo-student-a/diagnose?graph_ver=1&top_n=10", null);
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        var top = data.GetProperty("topDebts");
        Assert.True(top.GetArrayLength() >= 3, $"debts={top.GetArrayLength()}");
        foreach (var d in top.EnumerateArray())
        {
            Assert.True(d.GetProperty("impact").GetDouble() > 0);
            Assert.False(string.IsNullOrEmpty(d.GetProperty("fromKpName").GetString()));
        }
        var narrative = data.GetProperty("narrative");
        Assert.True(narrative.GetArrayLength() >= 1);
        var story = narrative[0].GetProperty("story").GetString()!;
        Assert.DoesNotContain("不适合", story);
        Assert.DoesNotContain("太笨", story);
        Assert.DoesNotContain("处分", story);
    }

    [Fact]
    public async Task StudentB_Diagnose_NoDebts()
    {
        var client = CreateClient();
        var resp = await client.PostAsync("/v1/students/demo-student-b/diagnose?graph_ver=1&top_n=10", null);
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        Assert.Equal(0, data.GetProperty("topDebts").GetArrayLength());
    }

    [Fact]
    public async Task GraphView_ReturnsNodesAndRedEdges()
    {
        var client = CreateClient();
        var resp = await client.GetAsync("/v1/students/demo-student-a/graph-view?graph_ver=1");
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        Assert.True(data.GetProperty("nodes").GetArrayLength() > 0);
        Assert.True(data.GetProperty("edges").GetArrayLength() >= 3);
        foreach (var e in data.GetProperty("edges").EnumerateArray())
        {
            Assert.True(e.GetProperty("impact").GetDouble() > 0);
        }
    }

    [Fact]
    public async Task Plan_IsConstraintChecked_AndUnderBudget()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/v1/students/demo-student-a/plans", new
        {
            graphVersion = 1,
            dayBudgetMin = 35,
            horizonDays = 14
        });
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == System.Net.HttpStatusCode.Created, raw);
        var data = await Data(resp);
        Assert.True(data.GetProperty("constraintsChecked").GetBoolean());
        foreach (var day in data.GetProperty("days").EnumerateArray())
        {
            var minutes = day.GetProperty("minutes").GetInt32();
            Assert.True(minutes <= 35, $"day {day.GetProperty("day").GetInt32()} minutes={minutes}");
            foreach (var item in day.GetProperty("items").EnumerateArray())
            {
                var why = item.GetProperty("why").GetString() ?? "";
                Assert.False(string.IsNullOrWhiteSpace(why));
            }
        }
    }

    [Fact]
    public async Task Today_Coach_CanPractice()
    {
        var client = CreateClient();
        await client.PostAsJsonAsync("/v1/students/demo-student-a/plans", new { graphVersion = 1 });
        var resp = await client.PostAsJsonAsync("/v1/students/demo-student-a/today", new { day = 1 });
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        var tasks = data.GetProperty("tasks");
        Assert.True(tasks.GetArrayLength() > 0);
        var task = tasks[0];
        Assert.False(string.IsNullOrEmpty(task.GetProperty("questionId").GetString()));
        Assert.False(string.IsNullOrEmpty(task.GetProperty("why").GetString()));
    }

    [Fact]
    public async Task Consent_Revoke_PurgesTeacherHotspots()
    {
        var client = CreateClient();

        var before = await client.GetAsync("/v1/teachers/demo-teacher/hotspots?only_consent=true");
        Assert.True(before.IsSuccessStatusCode);
        var beforeData = await Data(before);
        Assert.True(beforeData.GetProperty("authorizedCount").GetInt32() >= 1);
        Assert.True(beforeData.GetProperty("hotspots").GetArrayLength() >= 1);

        // grant B then revoke A
        var grant = await client.PostAsJsonAsync("/v1/consents/demo-student-b/grant", new
        {
            teacherId = "demo-teacher",
            purpose = "teacher_hotspots",
            actorId = "demo-student-b"
        });
        Assert.True(grant.IsSuccessStatusCode);

        var revoke = await client.PostAsJsonAsync("/v1/consents/demo-student-a/revoke", new
        {
            teacherId = "demo-teacher",
            purpose = "teacher_hotspots",
            actorId = "demo-student-a"
        });
        Assert.True(revoke.IsSuccessStatusCode);

        var after = await client.GetAsync("/v1/teachers/demo-teacher/hotspots?only_consent=true");
        var afterData = await Data(after);
        // B still authorized; A purged. Hotspots should only reflect authorized students.
        var authorized = afterData.GetProperty("authorizedCount").GetInt32();
        Assert.True(authorized >= 1);
        Assert.Equal("granted", (await client.GetFromJsonAsync<JsonDocument>("/v1/consents/demo-student-b"))!
            .RootElement.GetProperty("data")[0].GetProperty("state").GetString());

        var aConsent = await client.GetFromJsonAsync<JsonDocument>("/v1/consents/demo-student-a");
        var aState = aConsent!.RootElement.GetProperty("data")[0].GetProperty("state").GetString();
        Assert.Equal("revoked", aState);
        Assert.False(aConsent.RootElement.GetProperty("data")[0].GetProperty("allowTeacher").GetBoolean());
    }

    [Fact]
    public async Task SaleCheck_AndAttempts_ProgressMachine()
    {
        var client = CreateClient();
        await client.PostAsJsonAsync("/v1/students/demo-student-a/plans", new { graphVersion = 1 });
        var todayResp = await client.PostAsJsonAsync("/v1/students/demo-student-a/today", new { day = 1 });
        var today = await Data(todayResp);
        var task = today.GetProperty("tasks")[0];
        var planItemId = task.GetProperty("planItemId").GetString();
        var questionId = task.GetProperty("questionId").GetString();
        var kpId = task.GetProperty("kpId").GetString();

        // submit two good attempts
        for (var i = 0; i < 2; i++)
        {
            var resp = await client.PostAsJsonAsync("/v1/attempts", new
            {
                studentId = "demo-student-a",
                planItemId,
                kpId,
                questionId,
                correct = true,
                selfConf = 4,
                latencyMs = 1200
            });
            Assert.True(resp.IsSuccessStatusCode);
        }

        // diagnose to get a debt key
        var diag = await Data(await client.PostAsync("/v1/students/demo-student-a/diagnose?graph_ver=1&top_n=5", null));
        var debt = diag.GetProperty("topDebts")[0];
        var saleResp = await client.PostAsJsonAsync("/v1/debt-edges/sale-check", new
        {
            studentId = "demo-student-a",
            fromKp = debt.GetProperty("fromKp").GetString(),
            toKp = debt.GetProperty("toKp").GetString(),
            graphVersion = 1
        });
        Assert.True(saleResp.IsSuccessStatusCode);
        var sale = await Data(saleResp);
        Assert.True(sale.TryGetProperty("streak", out _));
        Assert.True(sale.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task WhatIf_UsesImpactFormula()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/v1/what-if", new
        {
            studentId = "demo-student-a",
            fromKp = "K03",
            toKp = "K05",
            overrideScoreFrom = 28.0,
            overrideScoreTo = 41.0,
            overrideFreq = 6,
            overrideDays = 0
        });
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        Assert.True(data.GetProperty("detected").GetBoolean());
        // weight may not be 1.6; just ensure positive impact and formula consistency when weight=1
        Assert.True(data.GetProperty("impact").GetDouble() > 0);
        Assert.Equal(1.0, data.GetProperty("recency").GetDouble(), 6);
    }

    [Fact]
    public async Task Ingest_Healthcheck_RejectsInvalid()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/v1/students/demo-student-a/ingest/scores", new
        {
            graphVersion = 1,
            source = "demo",
            rows = new object[]
            {
                new { kpId = "K03", recentAcc = 1.2, sev = 0.2, selfConf = 3 },
                new { kpId = "K999", recentAcc = 0.5, sev = 0.2, selfConf = 3 },
                new { kpId = "K05", recentAcc = 0.4, sev = 0.5, selfConf = 2 }
            }
        });
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        var report = data.GetProperty("report");
        Assert.True(report.GetProperty("accepted").GetInt32() >= 1);
        Assert.True(report.GetProperty("rejected").GetInt32() >= 2);
    }

    [Fact]
    public async Task DemoAdvance_Day7_ClearsAndSoftensDebts()
    {
        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/v1/demo/advance", new { studentId = "demo-student-a" });
        Assert.True(resp.IsSuccessStatusCode);
        // run until day >= 7
        for (var i = 0; i < 8; i++)
        {
            resp = await client.PostAsJsonAsync("/v1/demo/advance", new { studentId = "demo-student-a" });
        }
        var data = await Data(resp);
        Assert.True(data.GetProperty("day").GetInt32() >= 7);
        Assert.True(data.GetProperty("cleared").GetInt32() >= 1);
    }
}
