using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// 这里用一份 JSON 快照补齐这一层，语义与无服务单体版的 localStorage 对齐
/// （本机优先、无外部依赖、失败不阻断启动）。
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

    /// <summary>写入防抖（毫秒）：一次请求风暴只落一次盘。</summary>
    public int DebounceMs { get; set; } = 400;

    /// <summary>保留的历史快照份数（.bak 轮转），便于误操作后回退。</summary>
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
/// 快照读写器：防抖保存 + 内容哈希去重 + 原子写 + 优雅关闭落盘。
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
    private readonly string _file;
    private readonly object _gate = new();
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
        _file = options.Enabled ? Path.Combine(options.DataDir!, "runtime-state.json") : "";
    }

    /// <summary>快照文件绝对路径；未启用时为空串。</summary>
    public string FilePath => _file;

    /// <summary>启动时调用：读取快照并合并进内存状态。任何异常都被吞掉并返回错误描述。</summary>
    public SnapshotLoadResult Load()
    {
        if (!_options.Enabled) return new SnapshotLoadResult(false, false, 0, 0, 0, "快照未启用");
        try
        {
            if (!File.Exists(_file))
                return new SnapshotLoadResult(false, false, 0, 0, 0, null);

            var json = File.ReadAllText(_file, Encoding.UTF8);
            var snapshot = JsonSerializer.Deserialize<RuntimeSnapshot>(json, Json);
            if (snapshot is null)
                return new SnapshotLoadResult(true, false, 0, 0, 0, "快照内容为空");

            if (snapshot.SchemaVersion != RuntimeSnapshot.CurrentSchemaVersion)
                return new SnapshotLoadResult(true, false, 0, 0, 0,
                    $"快照结构版本 {snapshot.SchemaVersion} 与当前 {RuntimeSnapshot.CurrentSchemaVersion} 不符，已忽略");

            // 图包换了就丢弃旧状态，避免 KpId 对不上造成脏数据
            if (!string.IsNullOrWhiteSpace(snapshot.PackId)
                && !string.Equals(snapshot.PackId, _store.PackId, StringComparison.Ordinal))
            {
                return new SnapshotLoadResult(true, false, 0, 0, 0,
                    $"快照图包 {snapshot.PackId} 与当前 {_store.PackId} 不一致，已忽略");
            }

            _store.ImportState(snapshot.Store);
            _modules.ImportState(snapshot.Modules);
            _lastHash = HashOf(json);

            return new SnapshotLoadResult(true, true,
                snapshot.Store.Students.Count,
                snapshot.Modules.KbDocs.Count,
                snapshot.Modules.AgentTurns.Count,
                null);
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
                Directory.CreateDirectory(_options.DataDir!);
                WriteAtomic(json);
                _lastHash = hash;
            }
            return true;
        }
        catch
        {
            return false; // 盘满/权限问题不得影响服务
        }
    }

    private void WriteAtomic(string json)
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        if (_options.KeepBackups > 0 && File.Exists(_file))
        {
            try { File.Copy(_file, _file + ".bak", overwrite: true); } catch { /* 备份失败不致命 */ }
        }
        File.Move(tmp, _file, overwrite: true);
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
        }
    }
}
