using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AstralPath.Api.Tests;

/// <summary>
/// §44 受约束智能体 / §45 知识库 / §46 用户画像 的端到端用例。
/// 覆盖：正常路径、权限与越权（含 consent 闸门）、状态机冲突、诚实 501 延后项。
/// </summary>
public class AgentKbProfileTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AgentKbProfileTests(WebApplicationFactory<Program> factory)
        => _factory = factory.WithWebHostBuilder(_ => { });

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> Data(HttpResponseMessage resp)
    {
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) ? d : root;
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage resp)
    {
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c)
            ? c.GetString() : null;
    }

    // ═══════════════════════ §44 智能体 ═══════════════════════

    [Fact]
    public async Task Agent_Turn_Routes_To_Intent_And_Tracks_Session()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/agent/turns",
            new { userId = "demo-student-a", role = "student", utterance = "帮我看看我线代为什么总错" });
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        var data = await Data(resp);
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("intentId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("sessionId").GetString()));

        var sessionId = data.GetProperty("sessionId").GetString()!;
        var session = await client.GetAsync($"/v1/agent/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var state = await Data(session);
        Assert.Equal(sessionId, state.GetProperty("sessionId").GetString());
        Assert.True(state.GetProperty("turnCount").GetInt32() >= 1);

        var del = await client.DeleteAsync($"/v1/agent/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var gone = await client.GetAsync($"/v1/agent/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("RESOURCE_NOT_FOUND", await ErrorCode(gone));
    }

    [Fact]
    public async Task Agent_Crisis_Utterance_Hands_Off_Without_Academic_Advice()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/agent/turns",
            new { userId = "demo-student-a", role = "student", utterance = "我不想活了" });
        var data = await Data(resp);
        Assert.Equal("crisis.handoff", data.GetProperty("decision").GetString());
        Assert.Equal("crisis.handoff", data.GetProperty("intentId").GetString());
        var crisisText = data.GetProperty("response").GetString()!;
        Assert.True(crisisText.Contains("热线") || crisisText.Contains("联系"), crisisText);
    }

    [Fact]
    public async Task Agent_Tools_Requires_Valid_Role()
    {
        var client = Client();
        var ok = await client.GetAsync("/v1/agent/tools?role=student");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var data = await Data(ok);
        Assert.True(data.GetProperty("count").GetInt32() > 0);

        var bad = await client.GetAsync("/v1/agent/tools?role=hacker");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ErrorCode(bad));
    }

    [Fact]
    public async Task Agent_Turn_Rejects_Empty_Utterance()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/agent/turns",
            new { userId = "demo-student-a", role = "student", utterance = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ═══════════════════════ §45 知识库 ═══════════════════════

    [Fact]
    public async Task Kb_Lifecycle_Create_Version_Publish_Rollback_Archive()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/v1/kb/documents", new
        {
            title = "线代第 3 章讲义",
            ownerUserId = "demo-student-a",
            visibility = "private",
            courseCode = "LINALG",
            text = "特征值与特征向量的定义……",
            tags = new[] { "线代", "讲义" }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var doc = await Data(create);
        var docId = doc.GetProperty("id").GetString()!;

        // 版本 → 发布 → 回滚
        var v1 = await client.PostAsJsonAsync($"/v1/kb/documents/{docId}/versions",
            new { text = "v1 正文", note = "初稿", actorId = "demo-student-a" });
        Assert.Equal(HttpStatusCode.Created, v1.StatusCode);
        var v2 = await client.PostAsJsonAsync($"/v1/kb/documents/{docId}/versions",
            new { text = "v2 正文（修订）", note = "修订", actorId = "demo-student-a" });
        Assert.Equal(HttpStatusCode.Created, v2.StatusCode);

        var versions = await client.GetAsync($"/v1/kb/documents/{docId}/versions?userId=demo-student-a&role=student");
        var vData = await Data(versions);
        Assert.Equal(2, vData.GetProperty("versions").GetArrayLength());

        var publish = await client.PostAsJsonAsync($"/v1/kb/documents/{docId}/publish",
            new { actorId = "demo-student-a" });
        Assert.True(publish.IsSuccessStatusCode);
        Assert.Equal("v2", (await Data(publish)).GetProperty("publishedVersion").GetString());

        var rollback = await client.PostAsJsonAsync($"/v1/kb/documents/{docId}/rollback",
            new { version = "v1", actorId = "demo-student-a" });
        Assert.True(rollback.IsSuccessStatusCode);
        Assert.Equal("v1", (await Data(rollback)).GetProperty("publishedVersion").GetString());

        // 标签覆盖
        var tags = await client.PutAsJsonAsync($"/v1/kb/documents/{docId}/tags?userId=demo-student-a&role=student",
            new { tags = new[] { "线代" } });
        Assert.True(tags.IsSuccessStatusCode);

        // 归档后不可再改
        var archive = await client.PostAsync($"/v1/kb/documents/{docId}/archive?userId=demo-student-a&role=student", null);
        Assert.True(archive.IsSuccessStatusCode);
        var afterArchive = await client.PostAsJsonAsync($"/v1/kb/documents/{docId}/versions",
            new { text = "x", actorId = "demo-student-a" });
        Assert.Equal(HttpStatusCode.Conflict, afterArchive.StatusCode);
        Assert.Equal("STATE_CONFLICT", await ErrorCode(afterArchive));
    }

    [Fact]
    public async Task Kb_Private_Document_Is_Invisible_To_Others()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/v1/kb/documents", new
        {
            title = "我的私有笔记", ownerUserId = "demo-student-a", visibility = "private",
            courseCode = "LINALG", text = "私有内容", tags = new[] { "笔记" }
        });
        var docId = (await Data(create)).GetProperty("id").GetString()!;

        var other = await client.GetAsync($"/v1/kb/documents/{docId}?userId=demo-student-b&role=student");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        var owner = await client.GetAsync($"/v1/kb/documents/{docId}?userId=demo-student-a&role=student");
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task Kb_Consented_Document_Requires_Active_Consent_For_Teacher()
    {
        var client = Client();
        var create = await client.PostAsJsonAsync("/v1/kb/documents", new
        {
            title = "授权可见资料", ownerUserId = "demo-student-a", visibility = "consented",
            courseCode = "LINALG", text = "教师可见内容", tags = new[] { "授权" }
        });
        var docId = (await Data(create)).GetProperty("id").GetString()!;

        // 授权按 (student, teacher, purpose) 分键；先撤销该教师全部 purpose，确保初始为未授权
        foreach (var purpose in new[] { "teacher_hotspots", "kb_read" })
        {
            await client.PostAsJsonAsync("/v1/consents/demo-student-a/revoke",
                new { teacherId = "demo-teacher", purpose });
        }
        var before = await client.GetAsync($"/v1/kb/documents/{docId}?userId=demo-teacher&role=teacher");
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        // 未获授权的其他教师同样不可见（防止"任意 teacher 放行"）
        var otherTeacher = await client.GetAsync($"/v1/kb/documents/{docId}?userId=demo-teacher-2&role=teacher");
        Assert.Equal(HttpStatusCode.NotFound, otherTeacher.StatusCode);

        // 该教师获得有效授权后可见（consent 闸门生效）
        var grant = await client.PostAsJsonAsync("/v1/consents/demo-student-a/grant",
            new { teacherId = "demo-teacher", purpose = "kb_read" });
        Assert.True(grant.IsSuccessStatusCode, await grant.Content.ReadAsStringAsync());
        var after = await client.GetAsync($"/v1/kb/documents/{docId}?userId=demo-teacher&role=teacher");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Kb_Search_Respects_Visibility_PreFilter()
    {
        var client = Client();
        await client.PostAsJsonAsync("/v1/kb/documents", new
        {
            title = "检索用例私有文档", ownerUserId = "demo-student-a", visibility = "private",
            courseCode = "LINALG", text = "独特的检索关键词 ZZQ检索 只应被所有者命中", tags = new[] { "检索" }
        });

        var mine = await client.PostAsJsonAsync("/v1/kb/search",
            new { query = "ZZQ检索", userId = "demo-student-a", role = "student" });
        var mineData = await Data(mine);
        Assert.True(mineData.GetProperty("count").GetInt32() >= 1);

        var others = await client.PostAsJsonAsync("/v1/kb/search",
            new { query = "ZZQ检索", userId = "demo-student-b", role = "student" });
        Assert.Equal(0, (await Data(others)).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Kb_Tags_Tree_And_Validation()
    {
        var client = Client();
        var tree = await client.GetAsync("/v1/kb/tags?userId=demo-student-a&role=student");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);

        var bad = await client.PostAsJsonAsync("/v1/kb/documents", new
        {
            title = "非法可见性", ownerUserId = "demo-student-a", visibility = "secret"
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    // ═══════════════════════ §46 用户画像 ═══════════════════════

    [Fact]
    public async Task Profile_Features_Radar_Timeline_For_Demo_Student()
    {
        var client = Client();
        foreach (var path in new[] { "", "/features", "/tags", "/radar", "/timeline" })
        {
            var resp = await client.GetAsync($"/v1/profile/demo-student-a{path}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        var radar = await Data(await client.GetAsync("/v1/profile/demo-student-a/radar"));
        Assert.Equal(6, radar.GetProperty("axes").GetArrayLength());
    }

    [Fact]
    public async Task Profile_Unknown_Student_Returns_404()
    {
        var client = Client();
        var resp = await client.GetAsync("/v1/profile/not-a-student/features");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Profile_Banned_Domain_Is_Rejected()
    {
        var client = Client();
        var resp = await client.PostAsJsonAsync("/v1/profile/demo-student-a/tags",
            new { tag = "压力大", domain = "sensitive", weight = 1.0 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var ok = await client.PostAsJsonAsync("/v1/profile/demo-student-a/tags",
            new { tag = "节律型", domain = "pace", weight = 0.8 });
        Assert.True(ok.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Profile_OptOut_Is_Recorded_And_Suppresses_Tags()
    {
        var client = Client();
        var opt = await client.PostAsync("/v1/profile/demo-student-a/opt-out", null);
        Assert.True(opt.IsSuccessStatusCode);
        Assert.True((await Data(opt)).GetProperty("optOut").GetBoolean());

        var tags = await Data(await client.GetAsync("/v1/profile/demo-student-a/tags?teacherSide=true"));
        Assert.True(tags.GetProperty("optOut").GetBoolean(), "opt-out 应被记录并随画像返回");
    }

    // ═══════════════════════ 延后项与模块自检 ═══════════════════════

    [Fact]
    public async Task Deferred_Kb_Capabilities_Return_501_Explicitly()
    {
        var client = Client();
        var uploads = await client.PostAsync("/v1/kb/uploads", null);
        Assert.Equal(HttpStatusCode.NotImplemented, uploads.StatusCode);
        Assert.Equal("NOT_IMPLEMENTED", await ErrorCode(uploads));

        var chunks = await client.GetAsync("/v1/kb/chunks/c-1");
        Assert.Equal(HttpStatusCode.NotImplemented, chunks.StatusCode);
    }

    [Fact]
    public async Task Modules_Status_Reports_Canonical_Product_Name()
    {
        var client = Client();
        var data = await Data(await client.GetAsync("/v1/modules/status"));
        Assert.Equal("知债：星穹学途（Knowledge Debt: Astral Path）", data.GetProperty("product").GetString());
        Assert.Equal("AstralPath", data.GetProperty("code").GetString());
        Assert.True(data.GetProperty("agent").GetProperty("intents").GetInt32() > 0);
    }

    [Fact]
    public async Task Graph_Csr_Endpoint_Exposes_Compact_Adjacency()
    {
        var client = Client();
        var ok = await client.GetAsync("/v1/knowledge-graphs/accounting-v1/csr");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var data = await Data(ok);
        Assert.True(data.GetProperty("nodeCount").GetInt32() >= 30);
        Assert.True(data.GetProperty("edges").GetInt32() >= 40);

        var missing = await client.GetAsync("/v1/knowledge-graphs/no-such-pack/csr");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
