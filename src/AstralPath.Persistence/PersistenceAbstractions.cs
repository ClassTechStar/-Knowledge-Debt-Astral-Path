using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AstralPath.Persistence;

/// <summary>
/// 持久化配置（配置节：<c>Persistence</c>）。
/// <para>
/// 两种模式：
/// <list type="bullet">
///   <item><c>memory</c>：内存实现，零依赖，用于本地开发与 CI 快速跑通（默认）。</item>
///   <item><c>postgres</c>：真实 PostgreSQL + pgvector，用于生产与集成测试。</item>
/// </list>
/// </para>
/// </summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>memory | postgres（生产默认 postgres）</summary>
    public string Mode { get; set; } = "postgres";

    /// <summary>
    /// PostgreSQL 连接串。默认本机库；可用环境变量 Persistence__ConnectionString 或
    /// ASTRLPATH_CONN 覆盖。未配置时工厂自动回退 memory，保证演示/CI 可跑。
    /// </summary>
    public string ConnectionString { get; set; } =
        Environment.GetEnvironmentVariable("ASTRLPATH_CONN")
        ?? Environment.GetEnvironmentVariable("Persistence__ConnectionString")
        ?? "Host=localhost;Port=5432;Database=astralpath;Username=astralpath;Password=astralpath";

    /// <summary>向量维度（须与 pgvector 列定义一致）。</summary>
    public int VectorDimensions { get; set; } = 1024;

    /// <summary>RRF 融合常数 k（默认 60）。</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>启动时自动建表/建索引（生产建议关掉，改由迁移流程执行）。</summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>命令超时（秒）。</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    public bool IsPostgres => string.Equals(Mode, "postgres", StringComparison.OrdinalIgnoreCase);

    /// <summary>允许在无法连接时回退 memory（默认 true，演示友好；生产可关）。</summary>
    public bool AllowMemoryFallback { get; set; } = true;
}

/// <summary>
/// 向量化提供者。生产应接入真实嵌入模型；此处提供**确定性**的本地实现，
/// 保证：同文本 ⇒ 同向量，可在无模型环境下跑通向量检索与回归测试。
/// </summary>
public interface IEmbeddingProvider
{
    int Dimensions { get; }
    float[] Embed(string text);
}

/// <summary>
/// 演示用确定性嵌入：字符 n-gram 哈希投影 + L2 归一化。
/// 说明：它**不表达语义相似度**，只用于验证向量通路（索引/召回/RRF 融合）是否正确；
/// 生产请替换为实现同一接口的真实模型调用。
/// </summary>
public sealed class HashingEmbeddingProvider : IEmbeddingProvider
{
    private readonly int _dimensions;

    public HashingEmbeddingProvider(int dimensions = 1024)
    {
        _dimensions = dimensions is >= 8 and <= 4096 ? dimensions : 1024;
    }

    public int Dimensions => _dimensions;

    public float[] Embed(string text)
    {
        var vec = new float[_dimensions];
        var src = text ?? "";
        // 以 2-gram 与 3-gram 混合投影，降低偶然碰撞影响
        for (var n = 2; n <= 3; n++)
        {
            for (var i = 0; i + n <= src.Length; i++)
            {
                var gram = src.Substring(i, n);
                var idx = (int)(StableHash(gram) % (uint)_dimensions);
                vec[idx] += 1.0f;
            }
        }
        // L2 归一化，便于余弦距离
        var norm = MathF.Sqrt(vec.Sum(v => v * v));
        if (norm > 0)
            for (var i = 0; i < vec.Length; i++) vec[i] /= norm;
        return vec;
    }

    private static uint StableHash(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return (uint)((hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3]);
    }
}

/// <summary>检索命中项。</summary>
public sealed record KbSearchHit(
    string DocId,
    string Title,
    string Visibility,
    string CourseCode,
    string[] Tags,
    string Snippet,
    double FtsRank,
    double VectorRank,
    double RrfScore);

/// <summary>知识库仓储：写入、读取与**混合检索（RRF）**。</summary>
public interface IKnowledgeRepository
{
    /// <summary>建表/建索引（pgvector + HNSW + GIN 全文）。</summary>
    Task EnsureSchemaAsync(CancellationToken ct = default);

    Task<string> UpsertAsync(string id, string title, string ownerUserId, string visibility,
        string courseCode, string[] tags, string text, CancellationToken ct = default);

    Task<PostgresDoc?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>混合检索：全文路与向量路各自排名后按 RRF 融合；可见性前置过滤。</summary>
    Task<IReadOnlyList<KbSearchHit>> SearchAsync(string query, int topK, string userId, string role,
        CancellationToken ct = default);

    /// <summary>诊断信息（模式、索引、向量维度），用于 /health 与排障。</summary>
    Task<PersistenceDiagnostics> DiagnosticsAsync(CancellationToken ct = default);
}

public sealed record PostgresDoc(
    string Id, string Title, string OwnerUserId, string Visibility,
    string CourseCode, string[] Tags, string Text, DateTime UpdatedAt);

public sealed record PersistenceDiagnostics(
    string Mode, bool Ready, int? VectorDimensions, string? IndexKind, string? Error);
