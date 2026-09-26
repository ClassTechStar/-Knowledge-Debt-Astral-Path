using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AstralPath.Api.Tests;

/// <summary>
/// §19 十个扩展服务 + §45 补齐能力（分片直传/片段/内部导入）的端到端用例。
/// 重点验证：算法确定性、伦理闸门（k-匿名/互补不排名/外推抑制）、以及"不写生产权威数据"的边界。
/// </summary>
public class ExtensionServicesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ExtensionServicesTests(WebApplicationFactory<Program> factory)
        => _factory = factory.WithWebHostBuilder(b => b.UseSetting("Security:RequireAuth", "false")); // 测试明确退出鉴权（生产默认开启）

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> Data(HttpResponseMessage resp)
    {
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : root;
    }

    // ═══════════ §19.1 传播模拟 ═══════════

    [Fact]
    public async Task Diffusion_Returns_Bound_And_Ranking()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/diffusion/simulate",
            new { studentId = "demo-student-a", intervention = new Dictionary<string, double> { ["K02"] = 20 }, alpha = 0.55, depth = 6 });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var data = await Data(resp);
        Assert.Equal("diff-v1", data.GetProperty("algoVersion").GetString());
        Assert.True(data.GetProperty("nodeCount").GetInt32() >= 30);
        // 截断误差界必须给出（生产结果可解释性要求）
        Assert.True(data.GetProperty("truncationBound").GetDouble() >= 0);
        Assert.True(data.GetProperty("repairRanking").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Diffusion_Is_Deterministic()
    {
        var client = Client();
        var body = new { studentId = "demo-student-a", intervention = new Dictionary<string, double> { ["K02"] = 15 } };
        var a = await Data(await client.PostAsJsonAsync("/v1/diffusion/simulate", body));
        var b = await Data(await client.PostAsJsonAsync("/v1/diffusion/simulate", body));
        Assert.Equal(a.GetProperty("truncationBound").GetDouble(), b.GetProperty("truncationBound").GetDouble(), 9);
        Assert.Equal(a.GetProperty("gains")[0].GetProperty("scoreHat").GetDouble(),
                     b.GetProperty("gains")[0].GetProperty("scoreHat").GetDouble(), 9);
    }

    [Fact]
    public async Task Diffusion_Unknown_Student_Is_404()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/diffusion/simulate", new { studentId = "no-such-student" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ═══════════ §19.2 考试影响 ═══════════

    [Fact]
    public async Task ExamImpact_And_PreExamPlan_Respect_TimeBox()
    {
        var client = Client();
        var impact = await client.PostAsJsonAsync("/v1/exams/midterm-1/impact",
            new { studentId = "demo-student-a", daysToExam = 7, dayBudgetMin = 30 });
        Assert.True(impact.IsSuccessStatusCode);
        var data = await Data(impact);
        Assert.Contains(data.GetProperty("riskBand").GetString(), new[] { "low", "medium", "high" });

        var plan = await client.PostAsJsonAsync("/v1/exams/midterm-1/preexam-plan",
            new { studentId = "demo-student-a", daysToExam = 7, dayBudgetMin = 30 });
        Assert.True(plan.IsSuccessStatusCode);
        var planData = await Data(plan);
        foreach (var day in planData.GetProperty("days").EnumerateArray())
            Assert.True(day.GetProperty("minutes").GetInt32() <= 35, "任一天不得超过 35 分钟");
    }

    // ═══════════ §19.3 同辈对照（强伦理）══════════

    [Fact]
    public async Task Cohort_Suppresses_When_Sample_Below_K()
    {
        var client = Client();
        var few = await client.PostAsJsonAsync("/v1/cohorts/stats", new { k = 5, scores = new[] { 60, 70 } });
        var fewData = await Data(few);
        Assert.True(fewData.GetProperty("suppressed").GetBoolean(), "样本 < k 必须抑制");
        Assert.True(fewData.GetProperty("p50").ValueKind == JsonValueKind.Null);

        var more = await client.PostAsJsonAsync("/v1/cohorts/stats", new { k = 5, scores = new[] { 60, 70, 55, 82, 66, 71 } });
        var moreData = await Data(more);
        Assert.False(moreData.GetProperty("suppressed").GetBoolean());
        Assert.True(moreData.GetProperty("p50").GetDouble() > 0);
        // 不得回传个体明细
        Assert.False(moreData.TryGetProperty("members", out _));
    }

    // ═══════════ §19.4 学习小组（强伦理）══════════

    [Fact]
    public async Task StudyGroup_Matches_Complementary_And_No_Ranking()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/study-groups/match", new
        {
            size = 3,
            members = new[]
            {
                new { studentId = "s1", tags = new[] { "线代", "矩阵" } },
                new { studentId = "s2", tags = new[] { "概率", "统计" } },
                new { studentId = "s3", tags = new[] { "线代", "概率" } },
                new { studentId = "s4", tags = new[] { "编程", "算法" } }
            }
        });
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        Assert.True(data.GetProperty("groupCount").GetInt32() >= 1);
        foreach (var g in data.GetProperty("groups").EnumerateArray())
            Assert.True(g.GetArrayLength() >= 2 && g.GetArrayLength() <= 5);
    }

    [Fact]
    public async Task StudyGroup_Rejects_TooFewMembers()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/study-groups/match",
            new { members = new[] { new { studentId = "s1", tags = new[] { "线代" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ═══════════ §19.5 微课 ═══════════

    [Fact]
    public async Task MicroLesson_Assemble_Has_No_Numeric_Claim()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/micro-lessons/assemble",
            new { kpId = "K02", minutes = 10, resources = new[] { "教材 P32 例题 3" } });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var data = await Data(resp);
        Assert.Equal(10, data.GetProperty("minutes").GetInt32());
        Assert.False(data.GetProperty("hasNumericClaim").GetBoolean(), "装配结果不得含数字结论");
    }

    [Fact]
    public async Task MicroLesson_Requires_KpId()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/micro-lessons/assemble", new { minutes = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ═══════════ §19.6–§19.8 速度 / 间隔 / 先修推演 ═══════════

    [Fact]
    public async Task Velocity_Fit_Returns_Explained_Slope()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/velocity/fit", new
        {
            series = new[]
            {
                new { at = "2026-09-01", score = 50 },
                new { at = "2026-09-05", score = 62 },
                new { at = "2026-09-09", score = 71 }
            }
        });
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        Assert.True(data.GetProperty("slopePerDay").GetDouble() > 0, "上升趋势斜率应为正");
        Assert.Equal("up", data.GetProperty("trend").GetString());
    }

    [Fact]
    public async Task SpacedReview_Schedules_By_Ladder()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/spaced-review/schedule", new
        {
            items = new[]
            {
                new { kpId = "K02", lapses = 0, lastScore = 90 },
                new { kpId = "K03", lapses = 2, lastScore = 45 }
            }
        });
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        Assert.Equal(2, data.GetProperty("count").GetInt32());
        // 低分多次遗忘的应排在更前
        var first = data.GetProperty("plan")[0].GetProperty("kpId").GetString();
        Assert.Equal("K03", first);
    }

    [Fact]
    public async Task PrereqSimulator_Is_ReadOnly_Prediction()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/prereq-simulator/simulate",
            new { studentId = "demo-student-a", targetKp = "K02", targetScore = 75 });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var data = await Data(resp);
        Assert.Equal("K02", data.GetProperty("targetKp").GetString());
        Assert.True(data.GetProperty("delta").GetDouble() >= 0);
        Assert.Contains("只读推演", data.GetProperty("note").GetString());
    }

    // ═══════════ §19.9 预警（强伦理）══════════

    [Fact]
    public async Task Forecast_Suppresses_When_Extrapolating_TooFar()
    {
        var client = Client();
        var ok = await client.PostAsJsonAsync("/v1/forecast/student",
            new { studentId = "demo-student-a", daysAhead = 14 });
        var okData = await Data(ok);
        Assert.False(okData.GetProperty("suppressed").GetBoolean());

        var far = await client.PostAsJsonAsync("/v1/forecast/student",
            new { studentId = "demo-student-a", daysAhead = 60 });
        var farData = await Data(far);
        Assert.True(farData.GetProperty("suppressed").GetBoolean(), "外推 >30 天必须抑制");
        // 区间必须存在且下限 <= 上限
        Assert.True(farData.GetProperty("lowerBound").GetDouble() <= farData.GetProperty("upperBound").GetDouble());
    }

    // ═══════════ §19.10 实验台 ═══════════

    [Fact]
    public async Task LabBench_Promote_Requires_TwoDistinctPeople()
    {
        var client = Client();
        var same = await client.PostAsJsonAsync("/internal/v1/lab/formula-versions/score-v2/promote",
            new { proposer = "P2", approver = "P2" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, same.StatusCode);

        var ok = await client.PostAsJsonAsync("/internal/v1/lab/formula-versions/score-v2/promote",
            new { proposer = "P2", approver = "P1" });
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LabBench_Run_Detects_Regression()
    {
        var client = Client();
        var pass = await Data(await client.PostAsJsonAsync("/internal/v1/lab/experiments/lab-1/run",
            new { cases = new[] { new { expected = 1.0, actual = 1.0000001 } } }));
        Assert.True(pass.GetProperty("regressionOk").GetBoolean());

        var fail = await Data(await client.PostAsJsonAsync("/internal/v1/lab/experiments/lab-1/run",
            new { cases = new[] { new { expected = 1.0, actual = 1.5 } } }));
        Assert.False(fail.GetProperty("regressionOk").GetBoolean());
    }

    // ═══════════ §45 补齐：分片直传 / 片段 / 内部导入 ═══════════

    [Fact]
    public async Task Kb_Upload_Ticket_Commit_And_Chunk_Read()
    {
        var client = Client();
        var ticket = await client.PostAsJsonAsync("/v1/kb/uploads", new
        {
            title = "分片上传文档",
            ownerUserId = "demo-student-a",
            visibility = "private",
            courseCode = "LINALG",
            partCount = 2
        });
        Assert.Equal(HttpStatusCode.Created, ticket.StatusCode);
        var uploadId = (await Data(ticket)).GetProperty("uploadId").GetString()!;

        var commit = await client.PostAsJsonAsync($"/v1/kb/uploads/{uploadId}/commit", new
        {
            ownerUserId = "demo-student-a",
            parts = new[] { "第一部分正文。", "第二部分正文。" }
        });
        Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
        var commitData = await Data(commit);
        var docId = commitData.GetProperty("doc").GetProperty("id").GetString()!;
        Assert.True(commitData.GetProperty("chunkCount").GetInt32() >= 1);

        // 分片数不符应冲突
        var mismatch = await client.PostAsJsonAsync($"/v1/kb/uploads/{uploadId}/commit",
            new { ownerUserId = "demo-student-a", parts = new[] { "x" } });
        Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);

        // 片段读取（含上下文）
        var chunk = await client.GetAsync($"/v1/kb/chunks/ck-1?docId={docId}&userId=demo-student-a&role=student&context=1");
        Assert.Equal(HttpStatusCode.OK, chunk.StatusCode);
        var chunkData = await Data(chunk);
        Assert.True(chunkData.GetProperty("context").GetArrayLength() >= 1);

        // 他人不可读私有文档片段
        var forbidden = await client.GetAsync($"/v1/kb/chunks/ck-1?docId={docId}&userId=demo-student-b&role=student");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [Fact]
    public async Task Kb_InternalImport_Creates_Documents()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/internal/v1/kb/import", new
        {
            ownerUserId = "demo-student-a",
            items = new[]
            {
                new { title = "导入文档 A", visibility = "public", courseCode = "LINALG", text = "导入正文 A" },
                new { title = "导入文档 B", visibility = "private", courseCode = "LINALG", text = "导入正文 B" }
            }
        });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var data = await Data(resp);
        Assert.Equal(2, data.GetProperty("imported").GetInt32());
    }
}
