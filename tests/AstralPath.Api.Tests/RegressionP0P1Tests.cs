using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AstralPath.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
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
        => _factory = factory.WithWebHostBuilder(_ => { });

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
}
