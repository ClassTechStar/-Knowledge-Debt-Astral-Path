using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using AstralPath.Infrastructure;

namespace AstralPath.Api.Tests;

public class AuthAndMaterialTaskTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public AuthAndMaterialTaskTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b => b.UseSetting("Security:RequireAuth", "false")).CreateClient(); // 测试明确退出鉴权（生产默认开启）
    }

    private static async Task<JsonElement> Data(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        return root.TryGetProperty("data", out var data) ? data.Clone() : root.Clone();
    }

    [Fact]
    public void AuthStore_RegisterLoginSession_Roundtrip()
    {
        var auth = new AuthStore();
        var email = $"u{Guid.NewGuid():N}@test.local";
        var user = auth.Register(email, "secret12", "测试生", "demo-student-a");
        Assert.Equal("测试生", user.DisplayName);
        var session = auth.Login(email, "secret12");
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));
        var me = auth.FindByAccessToken(session.AccessToken);
        Assert.NotNull(me);
        Assert.Equal(user.UserId, me!.UserId);
        Assert.Throws<UnauthorizedAccessException>(() => auth.Login(email, "wrong-pass"));
        Assert.True(auth.Logout(session.SessionId, user.UserId));
        Assert.Null(auth.FindByAccessToken(session.AccessToken));
    }

    [Fact]
    public async Task AuthApi_RegisterLoginMe_Profile()
    {
        var email = $"api{Guid.NewGuid():N}@astralpath.local";
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "pass123456",
            displayName = "接口测试",
            demoStudentId = "demo-student-a"
        });
        Assert.True(reg.IsSuccessStatusCode, await reg.Content.ReadAsStringAsync());
        var regData = await Data(reg);
        var token = regData.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var login = await _client.PostAsJsonAsync("/api/v1/auth/sessions", new { email, password = "pass123456" });
        Assert.True(login.IsSuccessStatusCode);
        var loginData = await Data(login);
        token = loginData.GetProperty("accessToken").GetString();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var me = await _client.SendAsync(req);
        Assert.True(me.IsSuccessStatusCode);
        var profile = await Data(me);
        Assert.Equal("接口测试", profile.GetProperty("displayName").GetString());

        // material today requires auth
        using var todayReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/material-today?maxTasks=6");
        todayReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var today = await _client.SendAsync(todayReq);
        Assert.True(today.IsSuccessStatusCode);
        var payload = await Data(today);
        Assert.True(payload.TryGetProperty("tasks", out _));
    }

    [Fact]
    public async Task MaterialSeedAndTodayFromBooks_GeneratesTasks()
    {
        var seed = await _client.PostAsync("/v1/materials/seed-samples?parse=true", null);
        Assert.True(seed.IsSuccessStatusCode);
        var seedData = await Data(seed);
        Assert.True(seedData.GetProperty("seeded").GetInt32() >= 0);

        // parse at least one known sample synchronously if present
        var materials = await Data(await _client.GetAsync("/v1/materials"));
        if (materials.ValueKind == JsonValueKind.Array && materials.GetArrayLength() > 0)
        {
            var id = materials[0].GetProperty("id").GetString();
            var name = materials[0].GetProperty("name").GetString();
            // skip heavy C#/Go; prefer Kotlin if exists
            for (var i = 0; i < materials.GetArrayLength(); i++)
            {
                var n = materials[i].GetProperty("name").GetString() ?? "";
                if (n.Contains("Kotlin", StringComparison.OrdinalIgnoreCase))
                {
                    id = materials[i].GetProperty("id").GetString();
                    name = n;
                    break;
                }
            }

            var parse = await _client.PostAsync($"/v1/materials/{id}/parse?ocr=quick", null);
            Assert.True(parse.IsSuccessStatusCode, await parse.Content.ReadAsStringAsync());
            var doc = await Data(parse);
            Assert.Equal("ready", doc.GetProperty("status").GetString());

            var tasksResp = await _client.GetAsync($"/v1/materials/{id}/tasks?maxTasks=6");
            Assert.True(tasksResp.IsSuccessStatusCode);
            var tasks = await Data(tasksResp);
            Assert.True(tasks.GetProperty("tasks").GetArrayLength() > 0, "should generate textbook tasks");
            Assert.False(string.IsNullOrWhiteSpace(tasks.GetProperty("materialName").GetString()));
            _ = name;

            var todayBooks = await _client.GetAsync("/v1/materials/today-from-books?maxTasks=8");
            Assert.True(todayBooks.IsSuccessStatusCode);
            var today = await Data(todayBooks);
            Assert.True(today.GetProperty("tasks").GetArrayLength() > 0);
        }
    }

    [Fact]
    public async Task TodayEndpoint_IncludesMaterialTasksWhenAvailable()
    {
        // ensure some material tasks exist
        var todayBooks = await _client.PostAsync("/v1/materials/parse-all?ocr=quick", null);
        Assert.True(todayBooks.IsSuccessStatusCode);
        await Task.Delay(500);
        var resp = await _client.PostAsJsonAsync("/v1/students/demo-student-a/today", new { day = 1 });
        Assert.True(resp.IsSuccessStatusCode);
        var data = await Data(resp);
        Assert.True(data.TryGetProperty("tasks", out var tasks));
        Assert.True(tasks.GetArrayLength() > 0);
    }
}

public class MaterialTaskGeneratorUnitTests
{
    [Fact]
    public void GenerateFromGraph_BuildsConceptAndDrillTasks()
    {
        var nodes = new[]
        {
            new AstralPath.Graph.KpNode("A001", "第1章 变量与类型", "Kotlin", "chapter"),
            new AstralPath.Graph.KpNode("A002", "第2章 控制流", "Kotlin", "chapter"),
            new AstralPath.Graph.KpNode("A003", "协程", "Kotlin", "关键词 freq=9")
        };
        var edges = new[]
        {
            new AstralPath.Graph.KpEdge("A001", "A002", "prerequisite", 1.2, "auto"),
            new AstralPath.Graph.KpEdge("A003", "A001", "prerequisite", 1.0, "auto")
        };
        var graph = new AutoKnowledgeGraph("auto-x", "m1", "Kotlin编程实践", 1, DateTime.UtcNow, nodes, edges, "test");
        var tasks = MaterialTaskGenerator.GenerateFromGraph(graph, "Kotlin编程实践", 6);
        Assert.NotEmpty(tasks);
        Assert.Contains(tasks, t => t.Type == "concept");
        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Why)));
        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Stem)));
        var brief = MaterialTaskGenerator.BuildDayBrief(tasks, "Kotlin编程实践");
        Assert.NotNull(brief);
    }
}
