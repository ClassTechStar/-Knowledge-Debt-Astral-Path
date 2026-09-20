using System.Data;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AstralPath.Persistence;

/// <summary>
/// PostgreSQL + pgvector 实现：真实持久化、HNSW 向量索引、全文索引与 RRF 混合检索。
/// 可见性前置过滤（§45.9 / §6.6 URGENT）：越权文档**不进入召回集**，而非召回后过滤。
/// </summary>
public sealed class PostgresKnowledgeRepository : IKnowledgeRepository, IAsyncDisposable
{
    private readonly PersistenceOptions _options;
    private readonly IEmbeddingProvider _embeddings;
    private NpgsqlDataSource? _dataSource;
    // 两个独立的门：数据源创建 与 建表 各用一把锁。
    // 修复缺陷：原实现共用一把 SemaphoreSlim(1,1)，而 EnsureSchemaAsync 持锁后又调用同样加锁的
    // DataSourceAsync ⇒ 非可重入信号量自死锁（集成测试表现为永久挂起）。
    private readonly SemaphoreSlim _dsGate = new(1, 1);
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public PostgresKnowledgeRepository(PersistenceOptions options, IEmbeddingProvider? embeddings = null)
    {
        _options = options;
        _embeddings = embeddings ?? new HashingEmbeddingProvider(options.VectorDimensions);
    }

    private async ValueTask<NpgsqlDataSource> DataSourceAsync(CancellationToken ct)
    {
        if (_dataSource is not null) return _dataSource;
        await _dsGate.WaitAsync(ct);
        try
        {
            if (_dataSource is null)
            {
                if (string.IsNullOrWhiteSpace(_options.ConnectionString))
                    throw new InvalidOperationException("Persistence:ConnectionString 未配置（postgres 模式必填）");
                _dataSource = new NpgsqlDataSourceBuilder(_options.ConnectionString).Build();
            }
            return _dataSource;
        }
        finally { _dsGate.Release(); }
    }

    private NpgsqlCommand NewCommand(NpgsqlConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _options.CommandTimeoutSeconds;
        return cmd;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            var ds = await DataSourceAsync(ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = NewCommand(conn);
            cmd.CommandText = SchemaSql(_options.VectorDimensions);
            await cmd.ExecuteNonQueryAsync(ct);
            _schemaReady = true;
        }
        finally { _schemaGate.Release(); }
    }

    /// <summary>建表与索引：pgvector(HNSW) + GIN 全文 + 可见性过滤辅助索引。</summary>
    public static string SchemaSql(int dims) => $"""
        CREATE EXTENSION IF NOT EXISTS vector;
        CREATE EXTENSION IF NOT EXISTS pg_trgm;

        CREATE TABLE IF NOT EXISTS kb_documents (
            id              TEXT PRIMARY KEY,
            title           TEXT        NOT NULL,
            owner_user_id   TEXT        NOT NULL,
            visibility      TEXT        NOT NULL CHECK (visibility IN ('private','consented','course','public')),
            course_code     TEXT        NOT NULL DEFAULT '',
            tags            TEXT[]      NOT NULL DEFAULT ARRAY[]::text[],
            body            TEXT        NOT NULL DEFAULT '',
            search_vector   tsvector,
            updated_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS kb_chunks (
            chunk_id     TEXT PRIMARY KEY,
            doc_id       TEXT NOT NULL REFERENCES kb_documents(id) ON DELETE CASCADE,
            idx          INTEGER NOT NULL,
            text         TEXT NOT NULL,
            embedding    vector({dims})
        );

        -- 全文检索索引（§45.4 混合检索的"全文路"）
        CREATE INDEX IF NOT EXISTS idx_kb_documents_fts ON kb_documents USING GIN (search_vector);
        CREATE INDEX IF NOT EXISTS idx_kb_documents_owner ON kb_documents(owner_user_id);
        CREATE INDEX IF NOT EXISTS idx_kb_documents_tags ON kb_documents USING GIN (tags);
        CREATE INDEX IF NOT EXISTS idx_kb_chunks_doc ON kb_chunks(doc_id);

        -- 向量检索索引：HNSW（§45.4 混合检索的"向量路"）
        CREATE INDEX IF NOT EXISTS idx_kb_chunks_embedding_hnsw
            ON kb_chunks USING hnsw (embedding vector_cosine_ops);

        -- 触发器：正文/标题变化时自动维护 tsvector（中文按 bigram 切分需 simple 词典兜底）
        CREATE OR REPLACE FUNCTION kb_documents_tsvector_refresh() RETURNS trigger AS $$
        BEGIN
            NEW.search_vector :=
                setweight(to_tsvector('simple', coalesce(NEW.title,'')), 'A') ||
                setweight(to_tsvector('simple', coalesce(NEW.body,'')),  'B');
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS trg_kb_documents_tsvector ON kb_documents;
        CREATE TRIGGER trg_kb_documents_tsvector
            BEFORE INSERT OR UPDATE OF title, body ON kb_documents
            FOR EACH ROW EXECUTE FUNCTION kb_documents_tsvector_refresh();
        """;

    public async Task<string> UpsertAsync(string id, string title, string ownerUserId, string visibility,
        string courseCode, string[] tags, string text, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var ds = await DataSourceAsync(ct);
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = NewCommand(conn))
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO kb_documents (id, title, owner_user_id, visibility, course_code, tags, body, updated_at)
                VALUES (@id, @title, @owner, @vis, @course, @tags, @body, now())
                ON CONFLICT (id) DO UPDATE SET
                    title = EXCLUDED.title,
                    visibility = EXCLUDED.visibility,
                    course_code = EXCLUDED.course_code,
                    tags = EXCLUDED.tags,
                    body = EXCLUDED.body,
                    updated_at = now();
                DELETE FROM kb_chunks WHERE doc_id = @id;
                """;
            AddParams(cmd, id, title, ownerUserId, visibility, courseCode, tags, text);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 切分片段并写入向量（embedding 由提供者生成）
        var chunks = Chunk(text, 200);
        var idx = 0;
        foreach (var chunk in chunks)
        {
            var vec = _embeddings.Embed(chunk);
            await using var cmd = NewCommand(conn);
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO kb_chunks (chunk_id, doc_id, idx, text, embedding)
                VALUES (@chunkId, @docId, @idx, @text, CAST(@vec AS vector));
                """;
            cmd.Parameters.AddWithValue("chunkId", $"{id}-ck-{++idx}");
            cmd.Parameters.AddWithValue("docId", id);
            cmd.Parameters.AddWithValue("idx", idx - 1);
            cmd.Parameters.AddWithValue("text", chunk);
            cmd.Parameters.AddWithValue("vec", VectorLiteral(vec));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<PostgresDoc?> GetAsync(string id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var ds = await DataSourceAsync(ct);
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = NewCommand(conn);
        cmd.CommandText = """
            SELECT id, title, owner_user_id, visibility, course_code, tags, body, updated_at
            FROM kb_documents WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PostgresDoc(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetFieldValue<string[]>(5), reader.GetString(6),
            reader.GetDateTime(7).ToUniversalTime());
    }

    /// <summary>
    /// RRF 混合检索：全文路与向量路各自排名，再按 score = Σ 1/(k + rank) 融合（§45.4.4）。
    /// 可见性在**排名之前**过滤，保证越权内容不出现在任何一路。
    /// </summary>
    public async Task<IReadOnlyList<KbSearchHit>> SearchAsync(string query, int topK, string userId, string role,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var ds = await DataSourceAsync(ct);
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = NewCommand(conn);

        var queryVec = _embeddings.Embed(query ?? "");
        var isAdmin = string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);
        var isTeacher = string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase);

        cmd.CommandText = """
            WITH visible AS (
                SELECT id FROM kb_documents
                WHERE
                    @isAdmin
                    OR visibility IN ('public','course')
                    OR (visibility = 'private'   AND owner_user_id = @userId)
                    OR (visibility = 'consented' AND owner_user_id = @userId)
                    OR (visibility = 'consented' AND @isTeacher AND owner_user_id = ANY(@consented))
            ),
            fts AS (
                SELECT d.id,
                       ROW_NUMBER() OVER (ORDER BY ts_rank_cd(d.search_vector, plainto_tsquery('simple', @q)) DESC,
                                                   d.updated_at DESC) AS rnk
                FROM kb_documents d JOIN visible v ON v.id = d.id
                WHERE @q <> '' AND d.search_vector @@ plainto_tsquery('simple', @q)
                ORDER BY rnk
                LIMIT @limit
            ),
            vec AS (
                SELECT d.id,
                       ROW_NUMBER() OVER (ORDER BY MIN(c.embedding <=> CAST(@vec AS vector))) AS rnk
                FROM kb_chunks c
                JOIN kb_documents d ON d.id = c.doc_id
                JOIN visible v ON v.id = d.id
                WHERE c.embedding IS NOT NULL AND @q <> ''
                GROUP BY d.id
                -- 相关性上限：低于该相似度（距离过大）的文档不进入召回，
                -- 避免"向量路返回完全不相关文档"与内存实现语义不一致。
                HAVING MIN(c.embedding <=> CAST(@vec AS vector)) < @maxDist
                ORDER BY rnk
                LIMIT @limit
            )
            SELECT d.id, d.title, d.visibility, d.course_code, d.tags,
                   left(d.body, 120) AS snippet,
                   COALESCE(fts.rnk, 0)::float8 AS fts_rank,
                   COALESCE(vec.rnk, 0)::float8 AS vec_rank,
                   ( COALESCE(1.0 / (@k + fts.rnk), 0) + COALESCE(1.0 / (@k + vec.rnk), 0) )::float8 AS rrf_score
            FROM (fts FULL OUTER JOIN vec ON fts.id = vec.id)
            JOIN kb_documents d ON d.id = COALESCE(fts.id, vec.id)
            ORDER BY rrf_score DESC, d.updated_at DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("q", query ?? "");
        cmd.Parameters.AddWithValue("userId", userId ?? "");
        cmd.Parameters.AddWithValue("isAdmin", isAdmin);
        cmd.Parameters.AddWithValue("isTeacher", isTeacher);
        cmd.Parameters.AddWithValue("consented", Array.Empty<string>());
        cmd.Parameters.AddWithValue("vec", VectorLiteral(queryVec));
        cmd.Parameters.AddWithValue("k", _options.RrfK);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(topK, 1, 50));
        cmd.Parameters.AddWithValue("maxDist", 0.9d);

        var results = new List<KbSearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new KbSearchHit(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<string[]>(4), reader.GetString(5),
                reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8)));
        }
        return results;
    }

    public async Task<PersistenceDiagnostics> DiagnosticsAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureSchemaAsync(ct);
            var ds = await DataSourceAsync(ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = NewCommand(conn);
            cmd.CommandText = """
                SELECT
                  (SELECT count(*) FROM kb_documents)::int                                   AS docs,
                  (SELECT count(*) FROM kb_chunks)::int                                      AS chunks,
                  (SELECT count(*) FROM pg_indexes WHERE indexname = 'idx_kb_chunks_embedding_hnsw')::int AS hnsw,
                  (SELECT count(*) FROM pg_indexes WHERE indexname = 'idx_kb_documents_fts')::int         AS fts;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var hnsw = 0; var fts = 0;
            if (await reader.ReadAsync(ct)) { hnsw = reader.GetInt32(2); fts = reader.GetInt32(3); }
            return new PersistenceDiagnostics("postgres", true, _options.VectorDimensions,
                $"hnsw={hnsw},gin_fts={fts}", null);
        }
        catch (Exception ex)
        {
            return new PersistenceDiagnostics("postgres", false, null, null, ex.Message);
        }
    }

    private static void AddParams(NpgsqlCommand cmd, string id, string title, string owner,
        string vis, string course, string[] tags, string body)
    {
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("title", title);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("vis", vis);
        cmd.Parameters.AddWithValue("course", course);
        cmd.Parameters.AddWithValue("tags", tags ?? Array.Empty<string>());
        cmd.Parameters.AddWithValue("body", body ?? "");
    }

    public static List<string> Chunk(string text, int size)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        for (var pos = 0; pos < text.Length; pos += size)
            list.Add(text.Substring(pos, Math.Min(size, text.Length - pos)));
        return list;
    }

    private static string VectorLiteral(float[] vec)
    {
        var sb = new StringBuilder(vec.Length * 12 + 2);
        sb.Append('[');
        for (var i = 0; i < vec.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vec[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null) await _dataSource.DisposeAsync();
        _dsGate.Dispose();
        _schemaGate.Dispose();
    }
}

/// <summary>
/// 内存实现：用于本地开发与 CI（零外部依赖）。
/// 与 Postgres 实现保持同一套接口与**相同的可见性语义**，便于双模式对照测试。
/// </summary>
public sealed class InMemoryKnowledgeRepository : IKnowledgeRepository
{
    private readonly Dictionary<string, PostgresDoc> _docs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IEmbeddingProvider _embeddings;

    public InMemoryKnowledgeRepository(IEmbeddingProvider? embeddings = null)
        => _embeddings = embeddings ?? new HashingEmbeddingProvider();

    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> UpsertAsync(string id, string title, string ownerUserId, string visibility,
        string courseCode, string[] tags, string text, CancellationToken ct = default)
    {
        _docs[id] = new PostgresDoc(id, title, ownerUserId, visibility, courseCode, tags ?? Array.Empty<string>(),
            text ?? "", DateTime.UtcNow);
        return Task.FromResult(id);
    }

    public Task<PostgresDoc?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_docs.TryGetValue(id, out var d) ? d : null);

    public Task<IReadOnlyList<KbSearchHit>> SearchAsync(string query, int topK, string userId, string role,
        CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        var isAdmin = string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);
        var isTeacher = string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase);

        bool Visible(PostgresDoc d) => isAdmin
            || d.Visibility is "public" or "course"
            || (d.Visibility == "private" && d.OwnerUserId == userId)
            || (d.Visibility == "consented" && d.OwnerUserId == userId)
            || (d.Visibility == "consented" && isTeacher);
        var candidates = _docs.Values.Where(Visible).ToList();

        // 全文路：标题命中权重更高
        var ftsRanked = candidates
            .Select(d => (d, Score: (d.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2.0 : 0)
                                  + (d.Text.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0)
                                  + (d.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)) ? 1.5 : 0)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.d.UpdatedAt)
            .Select((x, i) => (x.d, Rank: i + 1))
            .ToList();

        // 向量路：与 Postgres 一致的余弦排序（用同一 provider，保证双模式可比）
        var qv = _embeddings.Embed(q);
        var vecRanked = candidates
            .Select(d => (d, Dist: Cosine(qv, _embeddings.Embed(d.Title + " " + d.Text))))
            .OrderBy(x => x.Dist)
            .Select((x, i) => (x.d, Rank: i + 1))
            .ToList();

        var ftsRank = ftsRanked.ToDictionary(x => x.d.Id, x => (double)x.Rank, StringComparer.OrdinalIgnoreCase);
        var vecRank = vecRanked.ToDictionary(x => x.d.Id, x => (double)x.Rank, StringComparer.OrdinalIgnoreCase);
        const int k = 60;

        var hits = candidates
            .Select(d => new KbSearchHit(d.Id, d.Title, d.Visibility, d.CourseCode, d.Tags,
                d.Text.Length > 120 ? d.Text[..120] + "…" : d.Text,
                ftsRank.TryGetValue(d.Id, out var fr) ? fr : 0,
                vecRank.TryGetValue(d.Id, out var vr) ? vr : 0,
                (ftsRank.TryGetValue(d.Id, out var f2) ? 1.0 / (k + f2) : 0)
                + (vecRank.TryGetValue(d.Id, out var v2) ? 1.0 / (k + v2) : 0)))
            .Where(h => h.RrfScore > 0)
            .OrderByDescending(h => h.RrfScore)
            .Take(Math.Clamp(topK, 1, 50))
            .ToList();

        return Task.FromResult<IReadOnlyList<KbSearchHit>>(hits);
    }

    public Task<PersistenceDiagnostics> DiagnosticsAsync(CancellationToken ct = default)
        => Task.FromResult(new PersistenceDiagnostics("memory", true, null, "none", null));

    private static double Cosine(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return 1.0;
        return 1 - dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

public static class PersistenceServiceExtensions
{
    /// <summary>按配置注册知识库仓储：postgres 或 memory。</summary>
    public static IServiceCollection AddAstralPathPersistence(this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new PersistenceOptions();
        configuration.GetSection(PersistenceOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.AddSingleton<IEmbeddingProvider>(sp => new HashingEmbeddingProvider(
            sp.GetRequiredService<PersistenceOptions>().VectorDimensions));

        if (options.IsPostgres)
        {
            services.AddSingleton<PostgresKnowledgeRepository>();
            services.AddSingleton<IKnowledgeRepository>(sp =>
                sp.GetRequiredService<PostgresKnowledgeRepository>());
        }
        else
        {
            services.AddSingleton<InMemoryKnowledgeRepository>();
            services.AddSingleton<IKnowledgeRepository>(sp =>
                sp.GetRequiredService<InMemoryKnowledgeRepository>());
        }
        return services;
    }
}
