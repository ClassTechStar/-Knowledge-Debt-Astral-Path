using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AstralPath.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AstralPath.Api.Tests;

/// <summary>
/// P0 / P1 修复的回归测试（2026-09-25 验收报告）。
///
/// 锁定三件事，防止将来被改回去：
///   P1-1 畸形请求体一律 400 + 统一错误形状，**不得**再出现 500 NullReferenceException；
///   P1-2 运行时状态可落盘并恢复（学生 / 作答 / 知识库 / 智能体轮次）；
///   P1-2 /health/ready 如实暴露持久化模式与快照状态。
/// </summary>
public class RegressionP0P1Tests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RegressionP0P1Tests(WebApplicationFactory<Program> factory)
        => _factory = factory.WithWebHostBuilder(b => b.UseSetting("Security:RequireAuth", "false")); // 测试明确退出鉴权（生产默认开启）

    private static async Task<string> ErrorCode(HttpResponseMessage resp)
    {
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "";
    }

    // ── P1-1：畸形 JSON ─────────────────────────────────
    [Theory]
    [InlineData("/v1/attempts")]
    [InlineData("/v1/debt-edges/sale-check")]
    [InlineData("/api/v1/auth/register")]
    [InlineData("/api/v1/auth/sessions")]
    public async Task P11_Malformed_Json_Returns_400_Not_500(string path)
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync(path,
            new StringContent("{bad", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode); // 曾为 500（NullReferenceException）
        Assert.Equal("VALIDATION_ERROR", await ErrorCode(resp));
    }

    [Theory]
    [InlineData("/v1/attempts")]
    [InlineData("/api/v1/auth/sessions")]
    public async Task P11_Json_Null_Body_Returns_400(string path)
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync(path,
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ErrorCode(resp));
    }

    [Fact]
    public async Task P11_Empty_Body_Returns_400_Unified_Shape()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/v1/attempts", new StringContent(""));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("data", out _));
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
        Assert.True(doc.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task P11_Valid_Body_Still_Works()
    {
        // 修复不能把正常请求也拦掉
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/v1/attempts", new
        {
            studentId = "demo-student-a",
            kpId = "K12",
            questionId = "Q1",
            correct = true,
            selfConf = 4
        });
        Assert.True(resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"正常请求应成功，实际 {(int)resp.StatusCode}");
    }

    // ── P1-2：健康检查如实暴露持久化 ─────────────────────
    [Fact]
    public async Task P12_Health_Ready_Exposes_Persistence_And_Snapshot()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("persistence", out var persistence),
            "/health/ready 必须报告持久化模式（原先完全没有该字段）");

        Assert.True(persistence.TryGetProperty("knowledgeBase", out var kb));
        Assert.True(kb.TryGetProperty("mode", out var mode));
        Assert.False(string.IsNullOrWhiteSpace(mode.GetString()));

        Assert.True(persistence.TryGetProperty("runtimeSnapshot", out var snapshot));
        Assert.True(snapshot.TryGetProperty("enabled", out _));
        Assert.True(snapshot.TryGetProperty("note", out var note));
        Assert.False(string.IsNullOrWhiteSpace(note.GetString()));
    }

    // ── P1-2：状态导出/导入往返 ─────────────────────────
    [Fact]
    public void P12_Store_ExportImport_RoundTrips()
    {
        var src = new AstralPathStore(AstralPathStore.FindGraphPack());
        src.ImportState(new StoreStateDto()); // no-op，仅确保可调用
        src.Lock(() =>
        {
            var s = src.EnsureStudent("roundtrip-student");
            s.CurrentDay = 7;
            s.SaleStreak["K12"] = 2;
        });

        var exported = src.ExportState();
        Assert.Contains(exported.Students, x => x.StudentId == "roundtrip-student");

        var dst = new AstralPathStore(AstralPathStore.FindGraphPack());
        dst.ImportState(exported);

        var restored = dst.Students["roundtrip-student"];
        Assert.Equal(7, restored.CurrentDay);
        Assert.Equal(2, restored.SaleStreak["K12"]);
    }

    [Fact]
    public void P12_Snapshot_File_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astralpath-snap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new RuntimeSnapshotOptions { DataDir = dir, DebounceMs = 50 };
            var store = new AstralPathStore(AstralPathStore.FindGraphPack());
            var modules = new AstralPathModules();

            store.Lock(() =>
            {
                var s = store.EnsureStudent("snap-student");
                s.CurrentDay = 3;
            });
            modules.ImportState(new ModuleStateDto { KbTexts = { ["d1"] = "正文" } });

            var writer = new RuntimeSnapshotStore(options, store, modules);
            Assert.True(writer.Save(), "首次保存应真正写盘");

            // 全新实例（模拟进程重启）
            var store2 = new AstralPathStore(AstralPathStore.FindGraphPack());
            var modules2 = new AstralPathModules();
            var reader = new RuntimeSnapshotStore(options, store2, modules2);
            var load = reader.Load();

            Assert.True(load.Loaded, load.Error);
            Assert.Contains(store2.Students.Keys, k => k == "snap-student");
            Assert.Equal(3, store2.Students["snap-student"].CurrentDay);
            Assert.Equal("正文", modules2.ExportState().KbTexts["d1"]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响结论 */ }
        }
    }

    [Fact]
    public void P12_Snapshot_Disabled_Does_Not_Throw_And_Does_Not_Write()
    {
        // 测试宿主走的就是这条路径：关闭状态构造不得抛异常（曾导致 52 项测试失败）
        var store = new AstralPathStore(AstralPathStore.FindGraphPack());
        var modules = new AstralPathModules();
        var options = new RuntimeSnapshotOptions { DataDir = null };

        var snapshot = new RuntimeSnapshotStore(options, store, modules);
        Assert.False(options.Enabled);
        Assert.False(snapshot.Save());
        Assert.False(snapshot.Load().Loaded);
        snapshot.MarkDirty();
        snapshot.Dispose();
        Assert.Equal("", snapshot.FilePath);
    }

    [Fact]
    public void P12_Snapshot_Rejects_Mismatched_SchemaVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astralpath-snapv-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var payload = new RuntimeSnapshot
            {
                SchemaVersion = RuntimeSnapshot.CurrentSchemaVersion + 99,
                PackId = "accounting-v1"
            };
            File.WriteAllText(Path.Combine(dir, "runtime-state.json"),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var store = new AstralPathStore(AstralPathStore.FindGraphPack());
            var snapshot = new RuntimeSnapshotStore(new RuntimeSnapshotOptions { DataDir = dir }, store, new AstralPathModules());
            var load = snapshot.Load();

            Assert.False(load.Loaded);          // 版本不符 → 安全忽略
            Assert.NotNull(load.Error);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    // ── P3-10：seed-samples 不得重复复制 ─────────────────
    [Fact]
    public async Task P310_SeedSamples_Is_Idempotent()
    {
        // 背景：去重原先只比对内存注册表，重启后注册表为空 → 反复调用把上传目录撑到 17GB
        //（同一本书多 GUID 副本）。修复后以「磁盘同名 + 同字节数」判定。
        //
        // 幂等不变量（与目录初始状态无关）：
        //   ① 连续两次调用，第二次必须零新增；
        //   ② 两次调用之间，落盘目录的文件数不得增长。
        // 注意不能断言 skipped == 第一次的 seeded：目录里可能早有历史副本。
        var client = _factory.CreateClient();
        var dir = AstralPath.Api.Controllers.MaterialsController.CurrentMaterialsDir;
        var countBefore = Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;

        var resp1 = await client.PostAsync("/v1/materials/seed-samples", null);
        resp1.EnsureSuccessStatusCode();
        var doc1 = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync()).RootElement.GetProperty("data");

        var resp2 = await client.PostAsync("/v1/materials/seed-samples", null);
        resp2.EnsureSuccessStatusCode();
        var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        var seeded2 = doc2.GetProperty("seeded").GetInt32();

        var countAfter = Directory.GetFiles(dir).Length;
        Assert.Equal(0, seeded2);            // 第二次调用不得再复制任何文件
        Assert.Equal(countBefore, countAfter); // 目录文件数不得增长
    }

    // ── 2.3-②：IAstralPathStore 契约解析（控制器已面向仓储接口）──
    [Fact]
    public void P15_Controllers_Resolve_IAstralPathStore()
    {
        var store = _factory.Services.GetRequiredService<AstralPath.Infrastructure.IAstralPathStore>();
        Assert.NotNull(store);
        Assert.NotEmpty(store.ScanDebts("demo-student-a")); // 种子学生可走完整诊断路径
    }

    // ── P1-M3：快照介质 SQLite（WAL）──────────────────────
    [Fact]
    public void P13_Snapshot_Legacy_Json_Is_Migrated_Into_Sqlite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astralpath-snapmig-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var payload = new RuntimeSnapshot
            {
                SavedAt = DateTime.UtcNow,
                PackId = "accounting-v1",
                Store = new StoreStateDto()
            };
            File.WriteAllText(Path.Combine(dir, "runtime-state.json"),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var store = new AstralPathStore(AstralPathStore.FindGraphPack());
            var snapshot = new RuntimeSnapshotStore(new RuntimeSnapshotOptions { DataDir = dir }, store, new AstralPathModules());

            var load = snapshot.Load();
            Assert.True(load.Loaded, load.Error);                 // 旧 JSON 被导入
            Assert.True(File.Exists(Path.Combine(dir, "runtime-state.db"))); // 介质已换 SQLite
            Assert.True(File.Exists(Path.Combine(dir, "runtime-state.json.migrated"))); // 旧文件留作备份

            // 第二次加载走 SQLite 路径，仍能恢复
            var store2 = new AstralPathStore(AstralPathStore.FindGraphPack());
            var modules2 = new AstralPathModules();
            var again = new RuntimeSnapshotStore(new RuntimeSnapshotOptions { DataDir = dir }, store2, modules2).Load();
            Assert.True(again.Loaded, again.Error);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    // ── P1-M1：鉴权默认开启 ───────────────────────────────
    [Fact]
    public async Task P14_RequireAuth_True_Gate_Works()
    {
        var secureFactory = _factory.WithWebHostBuilder(b => b.UseSetting("Security:RequireAuth", "true"));
        var client = secureFactory.CreateClient();

        // ① 无令牌访问学生数据 → 401（原先全站裸奔）
        var anon = await client.GetAsync("/v1/students/demo-student-a/mastery");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // ② 注册（默认 student 角色）→ 登录 → 携带令牌访问**本人**数据 → 200
        // 注意：register 会按邮箱自动绑定演示学生，必须用响应里的 demoStudentId，
        // 否则会被「归属校验」以 403 拦下（那是另一道正在工作的门禁）。
        var email = $"gate-{Guid.NewGuid():N}@test.local";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "pass123456" });
        reg.EnsureSuccessStatusCode();
        var regData = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        var token = regData.GetProperty("accessToken").GetString()!;
        var demoId = regData.GetProperty("profile").GetProperty("demoStudentId").GetString()!;

        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var own = await client.GetAsync($"/v1/students/{demoId}/mastery");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // ③ student 令牌读教师端聚合 → 403（角色门禁）
        var teacher = await client.GetAsync("/v1/teachers/demo-teacher/hotspots");
        Assert.Equal(HttpStatusCode.Forbidden, teacher.StatusCode);
    }
}
