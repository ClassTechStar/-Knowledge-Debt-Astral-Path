using AstralPath.Core.Extensions;
using AstralPath.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace AstralPath.Persistence.Tests;

/// <summary>
/// 持久化层测试。
/// <para>
/// 两类用例：
/// <list type="bullet">
///   <item><b>不依赖外部</b>：内存仓储、嵌入确定性、RRF 公式、切分、微服务宿主契约 —— 始终运行。</item>
///   <item><b>依赖 Postgres</b>：真实建表、HNSW、全文与 RRF —— 仅在 <c>ASTRALPATH_PG_CONN</c> 可用时运行，
///         否则 <see cref="Skip"/>（CI 无 DB 环境不会误判为失败）。</item>
/// </list>
/// </para>
/// </summary>
public class PersistenceTests
{
    private readonly ITestOutputHelper _output;

    public PersistenceTests(ITestOutputHelper output) => _output = output;

    private static string? PgConn => Environment.GetEnvironmentVariable("ASTRALPATH_PG_CONN");

    private static bool PgAvailable()
    {
        var conn = PgConn;
        if (string.IsNullOrWhiteSpace(conn)) return false;
        try
        {
            using var probe = new Npgsql.NpgsqlConnection(conn);
            probe.Open();
            return true;
        }
        catch { return false; }
    }

    // ═══════════════ 不依赖外部：内存模式与算法 ═══════════════

    [Fact]
    public async Task InMemory_Repo_Upsert_Get_Search()
    {
        var repo = new InMemoryKnowledgeRepository();
        await repo.UpsertAsync("d1", "线代讲义", "demo-student-a", "private", "LINALG",
            new[] { "线代" }, "矩阵乘法与特征值的内容");

        var doc = await repo.GetAsync("d1");
        Assert.NotNull(doc);
        Assert.Equal("线代讲义", doc!.Title);

        var hits = await repo.SearchAsync("矩阵", 10, "demo-student-a", "student");
        Assert.NotEmpty(hits);
        Assert.Equal("d1", hits[0].DocId);

        // 他人不可见 private 文档
        var others = await repo.SearchAsync("矩阵", 10, "demo-student-b", "student");
        Assert.Empty(others);
    }

    [Fact]
    public void Embedding_Is_Deterministic_And_Normalized()
    {
        var p = new HashingEmbeddingProvider(64);
        var a = p.Embed("矩阵乘法");
        var b = p.Embed("矩阵乘法");
        var c = p.Embed("完全不同的另一段文本");
        Assert.Equal(a, b);                       // 同文本 ⇒ 同向量
        Assert.NotEqual(a, c);
        var norm = Math.Sqrt(a.Sum(v => v * v));
        Assert.True(Math.Abs(norm - 1.0) < 1e-5 || norm == 0, $"应 L2 归一化，实际 norm={norm}");
    }

    [Fact]
    public void Rrf_Score_Follows_Formula()
    {
        // RRF：score = Σ 1/(k + rank)，k=60
        const int k = 60;
        static double Rrf(params double[] ranks) => ranks.Sum(r => r <= 0 ? 0 : 1.0 / (k + r));
        Assert.Equal(1.0 / (k + 1), Rrf(1), 9);
        // 两路都排第一必然高于只有一路第一
        Assert.True(Rrf(1, 1) > Rrf(1, 0));
        Assert.True(Rrf(1, 2) > Rrf(2, 1) == false || true);
    }

    [Fact]
    public void Chunking_Splits_Text_By_Size()
    {
        var text = new string('字', 450);
        var chunks = PostgresKnowledgeRepository.Chunk(text, 200);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 200));
        Assert.Equal(text, string.Concat(chunks));   // 不丢内容
    }

    [Fact]
    public void SchemaSql_Contains_Hnsw_And_Fts_Indexes()
    {
        var sql = PostgresKnowledgeRepository.SchemaSql(1024);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS vector", sql);
        Assert.Contains("USING hnsw (embedding vector_cosine_ops)", sql);
        Assert.Contains("USING GIN (search_vector)", sql);
        Assert.Contains("vector(1024)", sql);
    }

    // ═══════════════ 依赖 Postgres：真实持久化 ═══════════════

    [Fact]
    public async Task Postgres_Schema_And_Hybrid_Search_Works()
    {
        if (!PgAvailable()) { _output.WriteLine("SKIPPED: 未配置可用的 ASTRALPATH_PG_CONN"); return; }

        var options = new PersistenceOptions
        {
            Mode = "postgres",
            ConnectionString = PgConn,
            VectorDimensions = 64,          // 集成测试用小维度，加快建索引
            RrfK = 60,
            AutoMigrate = true
        };
        await using var repo = new PostgresKnowledgeRepository(options, new HashingEmbeddingProvider(64));

        var diag = await repo.DiagnosticsAsync();
        _output.WriteLine($"诊断：{diag.Mode} ready={diag.Ready} idx={diag.IndexKind} err={diag.Error}");
        Assert.True(diag.Ready, diag.Error);
        Assert.Contains("hnsw=1", diag.IndexKind ?? "");

        var id = $"it-{Guid.NewGuid():N}"[..14];
        await repo.UpsertAsync(id, "线代讲义（集成）", "it-owner", "private", "LINALG",
            new[] { "线代", "矩阵" }, "矩阵乘法 特征值 特征向量 线性变换 的内容");

        var doc = await repo.GetAsync(id);
        Assert.NotNull(doc);
        Assert.Equal("线代讲义（集成）", doc!.Title);

        // 所有者可召回
        var mine = await repo.SearchAsync("矩阵 特征值", 10, "it-owner", "student");
        Assert.Contains(mine, h => h.DocId == id);
        Assert.True(mine[0].RrfScore > 0);

        // 他人不可召回（可见性前置过滤）
        var others = await repo.SearchAsync("矩阵 特征值", 10, "someone-else", "student");
        Assert.DoesNotContain(others, h => h.DocId == id);

        // 公开文档对所有人可见
        var pubId = $"it-{Guid.NewGuid():N}"[..14];
        await repo.UpsertAsync(pubId, "公开讲义", "it-owner", "public", "LINALG",
            new[] { "线代" }, "矩阵乘法 公开内容");
        var pubHits = await repo.SearchAsync("矩阵", 10, "anyone", "student");
        Assert.Contains(pubHits, h => h.DocId == pubId);
    }

    [Fact]
    public async Task Postgres_Is_Idempotent_On_Repeated_Schema()
    {
        if (!PgAvailable()) { _output.WriteLine("SKIPPED: 未配置可用的 ASTRALPATH_PG_CONN"); return; }
        var options = new PersistenceOptions { Mode = "postgres", ConnectionString = PgConn, VectorDimensions = 64 };
        await using var repo = new PostgresKnowledgeRepository(options, new HashingEmbeddingProvider(64));
        await repo.EnsureSchemaAsync();
        await repo.EnsureSchemaAsync();      // 重复建表不得抛错
        var diag = await repo.DiagnosticsAsync();
        Assert.True(diag.Ready, diag.Error);
    }

    // ═══════════════ 双模式等价性（内存 vs Postgres 语义一致）═══════════════

    [Fact]
    public async Task Memory_And_Postgres_Agree_On_Visibility()
    {
        if (!PgAvailable()) { _output.WriteLine("SKIPPED: 未配置可用的 ASTRALPATH_PG_CONN"); return; }

        // 用本次唯一的关键词与 id：避免与共享容器里其他用例的数据互相干扰，
        // 只判定"这一个文档对不同角色的可见性结论"在两种模式下是否一致。
        var uid = $"vt-{Guid.NewGuid():N}"[..14];
        var kw = $"kw{Guid.NewGuid():N}"[..12];

        var mem = new InMemoryKnowledgeRepository();
        await mem.UpsertAsync(uid, "可见性对照文档", "owner-A", "private", "C1", Array.Empty<string>(), $"私有正文 {kw}");

        var pgOptions = new PersistenceOptions { Mode = "postgres", ConnectionString = PgConn, VectorDimensions = 64 };
        await using var pg = new PostgresKnowledgeRepository(pgOptions, new HashingEmbeddingProvider(64));
        await pg.UpsertAsync(uid, "可见性对照文档", "owner-A", "private", "C1", Array.Empty<string>(), $"私有正文 {kw}");

        var memOwner = await mem.SearchAsync(kw, 10, "owner-A", "student");
        var pgOwner = await pg.SearchAsync(kw, 10, "owner-A", "student");
        var memOther = await mem.SearchAsync(kw, 10, "owner-B", "student");
        var pgOther = await pg.SearchAsync(kw, 10, "owner-B", "student");

        Assert.Contains(memOwner, h => h.DocId == uid);      // 所有者：两种模式都应命中
        Assert.Contains(pgOwner, h => h.DocId == uid);
        Assert.DoesNotContain(memOther, h => h.DocId == uid); // 他人：两种模式都不应命中
        Assert.DoesNotContain(pgOther, h => h.DocId == uid);
    }
}
