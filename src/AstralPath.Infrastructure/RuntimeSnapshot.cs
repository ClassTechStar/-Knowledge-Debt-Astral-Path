using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using AstralPath.Core.Graph;
using AstralPath.Core.Models;
using AstralPath.Core.Algorithms;

namespace AstralPath.Infrastructure;

/// <summary>
/// 运行时状态快照（P1 修复）：把 <see cref="AstralPathStore"/> 与 <see cref="AstralPathModules"/>
/// 的**可变状态**落盘，使进程重启后学生、作答、掌握度、计划、consent、知识库文档、
/// 智能体会话与用户画像都不再丢失。
///
/// 为什么需要它：`Persistence` 配置节管的是**知识库 / 向量检索**那一层；
/// 而「学生学到了哪、做过哪些题、教师是否被授权」是另一层运行时状态，
/// 原先只活在内存里，进程一重启就回到 seed 数据（实测 students 4→2、kbDocs 3→0、turns 42→0）。
/// 介质选型（P1 审计 M3）：SQLite（WAL）单行 upsert——每次保存都是**一个原子事务**，
/// 应用崩溃后 WAL 自动恢复，不再有「防抖窗口内崩溃丢数据」与「半写文件」；
/// 相同内容哈希去重，重复保存天然幂等。旧版 runtime-state.json 首次加载时自动迁移。
/// </summary>
public sealed class RuntimeSnapshot
{
    /// <summary>快照结构版本；结构变更时递增，旧快照会被安全忽略。</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTime SavedAt { get; set; }
    public string PackId { get; set; } = "";
    public StoreStateDto Store { get; set; } = new();
    public ModuleStateDto Modules { get; set; } = new();
}

/// <summary>业务账本状态（学生 / 授权 / 授权审计）。</summary>
public sealed class StoreStateDto
{
    public List<StudentStateDto> Students { get; set; } = new();
    public List<Consent> Consents { get; set; } = new();
    public List<ConsentAudit> ConsentAudits { get; set; } = new();
}

/// <summary>单个学生的全部学习状态。</summary>
public sealed class StudentStateDto
{
    public string StudentId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DemoGroup { get; set; } = "A";
    public Dictionary<string, IngestRow> MasteryInputs { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, MasteryRecord> Mastery { get; set; } = new(StringComparer.Ordinal);
    public List<DebtEdge> DebtEdges { get; set; } = new();
    public List<Attempt> Attempts { get; set; } = new();
    public PlanDtoHolder? ActivePlan { get; set; }
    public int CurrentDay { get; set; } = 1;
    public Dictionary<string, int> SaleStreak { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<SaleProbe>> SaleHistory { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 模块状态（知识库 / 画像 / 智能体）。
/// 说明：分片（chunks）、上传票据与会话句柄属**瞬态**数据，不落盘——
/// 它们可由文档正文重建或在下次请求时重新建立。
/// </summary>
public sealed class ModuleStateDto
{
    public Dictionary<string, KbDocument> KbDocs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> KbTexts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, LearningProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<KbVersion>> KbVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> KbPublished { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> KbArchived { get; set; } = new();
    public List<Dictionary<string, object?>> AgentTurns { get; set; } = new();
}

/// <summary>快照配置（配置节 <c>Persistence</c> 下的 <c>Snapshot*</c> 键）。</summary>
public sealed class RuntimeSnapshotOptions
{
    /// <summary>快照目录；为空表示关闭快照（测试宿主默认关闭）。</summary>
    public string? DataDir { get; set; }

    /// <summary>
    /// 写入防抖（毫秒）：一次请求风暴只落一次盘。SQLite WAL 提交成本低，
    /// 默认从 400 收紧到 150 以缩小崩溃丢失窗口。
    /// </summary>
    public int DebounceMs { get; set; } = 150;

    /// <summary>保留的历史快照份数。SQLite 单行 upsert 自身原子且幂等，属性仅为配置兼容保留。</summary>
    public int KeepBackups { get; set; } = 1;

    public bool Enabled => !string.IsNullOrWhiteSpace(DataDir);
}

/// <summary>加载结果，供启动日志如实汇报。</summary>
public sealed record SnapshotLoadResult(
    bool Found,
    bool Loaded,
    int Students,
    int KbDocuments,
    int AgentTurns,
    string? Error);

/// <summary>
/// 快照读写器：SQLite（WAL）单行 upsert + 内容哈希去重 + 旧 JSON 自动迁移 + 优雅关闭落盘。
/// 所有失败都只记日志，**绝不阻断启动**（演示/离线可用性优先）。
/// </summary>
public sealed class RuntimeSnapshotStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly RuntimeSnapshotOptions _options;
    private readonly AstralPathStore _store;
    private readonly AstralPathModules _modules;
    private readonly string _dbFile;
    private readonly string _legacyFile;
    private readonly object _gate = new();
    private SqliteConnection? _conn;
    private Timer? _timer;
    private Timer? _sweep;
    private string _lastHash = "";
    private bool _disposed;

    public RuntimeSnapshotStore(RuntimeSnapshotOptions options, AstralPathStore store, AstralPathModules modules)
    {
        _options = options;
        _store = store;
        _modules = modules;
        // 关闭状态下 DataDir 为 null，不能直接 Path.Combine，否则构造即抛
        // （测试宿主走这条路径，曾导致 Api.Tests 52 项失败）。
        _dbFile = options.Enabled ? Path.Combine(options.DataDir!, "runtime-state.db") : "";
        _legacyFile = options.Enabled ? Path.Combine(options.DataDir!, "runtime-state.json") : "";
    }

    /// <summary>快照数据库绝对路径；未启用时为空串。</summary>
    public string FilePath => _dbFile;

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbFile,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        conn.Open();
        // WAL：写不阻塞读，应用崩溃后由 -wal 自动恢复；NORMAL 在应用崩溃时保证已提交事务不丢。
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
        }
        EnsureSchema(conn);
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshot(
                id            INTEGER PRIMARY KEY CHECK(id = 1),
                schema_version INTEGER NOT NULL,
                saved_at      TEXT    NOT NULL,
                pack_id       TEXT    NOT NULL,
                payload       TEXT    NOT NULL,
                payload_hash  TEXT    NOT NULL)
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>启动时调用：读取快照并合并进内存状态。任何异常都被吞掉并返回错误描述。</summary>
    public SnapshotLoadResult Load()
    {
        if (!_options.Enabled) return new SnapshotLoadResult(false, false, 0, 0, 0, "快照未启用");
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_dbFile))
                {
                    // 旧版 JSON 快照迁移：校验通过则导入 SQLite 并把旧文件改名留作一次性备份。
                    if (!File.Exists(_legacyFile))
                        return new SnapshotLoadResult(false, false, 0, 0, 0, null);

                    var legacyJson = File.ReadAllText(_legacyFile, Encoding.UTF8);
                    var legacy = JsonSerializer.Deserialize<RuntimeSnapshot>(legacyJson, Json);
                    if (legacy is null)
                        return new SnapshotLoadResult(true, false, 0, 0, 0, "快照内容为空");
                    if (legacy.SchemaVersion != RuntimeSnapshot.CurrentSchemaVersion)
                        return new SnapshotLoadResult(true, false, 0, 0, 0,
                            $"快照结构版本 {legacy.SchemaVersion} 与当前 {RuntimeSnapshot.CurrentSchemaVersion} 不符，已忽略");
                    // 图包换了就丢弃旧状态，避免 KpId 对不上造成脏数据
                    if (!string.IsNullOrWhiteSpace(legacy.PackId)
                        && !string.Equals(legacy.PackId, _store.PackId, StringComparison.Ordinal))
                    {
                        return new SnapshotLoadResult(true, false, 0, 0, 0,
                            $"快照图包 {legacy.PackId} 与当前 {_store.PackId} 不一致，已忽略");
                    }

                    _store.ImportState(legacy.Store);
                    _modules.ImportState(legacy.Modules);
                    Directory.CreateDirectory(_options.DataDir!);
                    _conn ??= OpenConnection();
                    WriteSnapshotLocked(legacy.SavedAt, legacy.PackId, legacyJson);
                    _lastHash = HashOf(legacyJson);
                    try { File.Move(_legacyFile, _legacyFile + ".migrated", overwrite: true); } catch { /* 改名失败不致命 */ }
                    return new SnapshotLoadResult(true, true,
                        legacy.Store.Students.Count,
                        legacy.Modules.KbDocs.Count,
                        legacy.Modules.AgentTurns.Count,
                        null);
                }

                _conn ??= OpenConnection();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT schema_version, pack_id, payload, payload_hash FROM snapshot WHERE id = 1";
                    using var r = cmd.ExecuteReader();
                    if (!r.Read())
                        return new SnapshotLoadResult(true, false, 0, 0, 0, "快照行为空");

                    var version = r.GetInt64(0);
                    if (version != RuntimeSnapshot.CurrentSchemaVersion)
                        return new SnapshotLoadResult(true, false, 0, 0, 0,
                            $"快照结构版本 {version} 与当前 {RuntimeSnapshot.CurrentSchemaVersion} 不符，已忽略");

                    var payload = r.GetString(2);
                    var snapshot = JsonSerializer.Deserialize<RuntimeSnapshot>(payload, Json);
                    if (snapshot is null)
                        return new SnapshotLoadResult(true, false, 0, 0, 0, "快照内容为空");
                    if (!string.IsNullOrWhiteSpace(snapshot.PackId)
                        && !string.Equals(snapshot.PackId, _store.PackId, StringComparison.Ordinal))
                    {
                        return new SnapshotLoadResult(true, false, 0, 0, 0,
                            $"快照图包 {snapshot.PackId} 与当前 {_store.PackId} 不一致，已忽略");
                    }

                    _store.ImportState(snapshot.Store);
                    _modules.ImportState(snapshot.Modules);
                    _lastHash = r.GetString(3);
                    return new SnapshotLoadResult(true, true,
                        snapshot.Store.Students.Count,
                        snapshot.Modules.KbDocs.Count,
                        snapshot.Modules.AgentTurns.Count,
                        null);
                }
            }
        }
        catch (Exception ex)
        {
            return new SnapshotLoadResult(true, false, 0, 0, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>标记状态已变更：安排一次防抖写入。可由 store/modules 的写路径高频调用。</summary>
    public void MarkDirty()
    {
        if (!_options.Enabled || _disposed) return;
        lock (_gate)
        {
            _timer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(Math.Max(50, _options.DebounceMs), Timeout.Infinite);
        }
    }

    /// <summary>
    /// 启动周期兜底扫描：<see cref="AstralPathModules"/> 的写路径在内部 lock 里，
    /// 没有写后钩子，靠这个定时器 + 内容哈希去重来兜底（内容没变则不写盘）。
    /// </summary>
    public void StartPeriodicSweep(int intervalMs = 2000)
    {
        if (!_options.Enabled || _disposed) return;
        lock (_gate)
        {
            _sweep ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _sweep.Change(intervalMs, intervalMs);
        }
    }

    /// <summary>立即保存（内容未变化则跳过）。返回是否真正写盘。</summary>
    public bool Save()
    {
        if (!_options.Enabled || _disposed) return false;
        try
        {
            var snapshot = new RuntimeSnapshot
            {
                SavedAt = DateTime.UtcNow,
                PackId = _store.PackId,
                Store = _store.ExportState(),
                Modules = _modules.ExportState()
            };
            var json = JsonSerializer.Serialize(snapshot, Json);
            var hash = HashOf(json);
            lock (_gate)
            {
                if (hash == _lastHash) return false; // 无变化，省一次磁盘写
                Directory.CreateDirectory(_options.DataDir!); // 首次保存时目录可能尚不存在
                _conn ??= OpenConnection();
                WriteSnapshotLocked(snapshot.SavedAt, snapshot.PackId, json, hash);
                _lastHash = hash;
            }
            return true;
        }
        catch
        {
            return false; // 盘满/权限问题不得影响服务
        }
    }

    /// <summary>调用方必须已持有 <see cref="_gate"/>：单行 upsert，天然原子且幂等。</summary>
    private void WriteSnapshotLocked(DateTime savedAt, string packId, string json, string? hash = null)
    {
        hash ??= HashOf(json);
        using var cmd = _conn!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshot(id, schema_version, saved_at, pack_id, payload, payload_hash)
            VALUES(1, $v, $t, $p, $j, $h)
            ON CONFLICT(id) DO UPDATE SET
                schema_version = $v, saved_at = $t, pack_id = $p, payload = $j, payload_hash = $h
            """;
        cmd.Parameters.AddWithValue("$v", RuntimeSnapshot.CurrentSchemaVersion);
        cmd.Parameters.AddWithValue("$t", savedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$p", packId);
        cmd.Parameters.AddWithValue("$j", json);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.ExecuteNonQuery();
    }

    private static string HashOf(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>优雅关闭时同步落盘。</summary>
    public void Flush() => Save();

    public void Dispose()
    {
        if (_disposed) return;
        Save();
        _disposed = true;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _sweep?.Dispose();
            _sweep = null;
            try { _conn?.Dispose(); } catch { /* ignore */ }
            _conn = null;
        }
    }
}
