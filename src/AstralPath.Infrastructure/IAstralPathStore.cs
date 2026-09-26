using AstralPath.Core.Algorithms;
using AstralPath.Core.Models;
using AstralPath.Graph;

namespace AstralPath.Infrastructure;

/// <summary>
/// 业务账本仓储契约（重构评估 2.3-② / 2.4-L1）：控制器与宿主只面向本接口编程，
/// 实现可在「内存权威（当前唯一实现，测试可直接 new）」与未来离线 SQLite /
/// 服务端 Postgres 形态之间替换，原生端亦可注入 mock。
///
/// 语义约定（与既有实现一致，禁止破坏）：
/// · 单权威写入路径 = <see cref="Lock(Action)"/> 内变更，锁外触发 <see cref="AfterLock"/>；
/// · score / impact / 销账状态只由 Core 纯函数产出；
/// · Graph 来自图包（只读），PackId 变更即换版本。
/// </summary>
public interface IAstralPathStore
{
    KnowledgeGraph Graph { get; }
    string PackId { get; }

    Dictionary<string, StudentState> Students { get; }
    Dictionary<string, Consent> Consents { get; }
    List<ConsentAudit> ConsentAudits { get; }
    Dictionary<string, List<HotspotDtoHolder>> TeacherCache { get; }
    Dictionary<string, QuestionBankItem> Questions { get; }

    /// <summary>每次 Lock 退出后（锁外）回调；宿主用它挂「状态已变更 → 安排持久化」。</summary>
    Action? AfterLock { get; set; }

    void Lock(Action action);
    T Lock<T>(Func<T> fn);

    /// <summary>导出可变业务状态（学生 / 授权 / 授权审计），供快照层落盘。</summary>
    StoreStateDto ExportState();

    /// <summary>合并导入快照状态（upsert，不删除 seed 数据）。</summary>
    void ImportState(StoreStateDto? state);

    StudentState EnsureStudent(string studentId);
    void RecomputeMastery(string studentId);
    IReadOnlyList<ScannedDebtEdge> ScanDebts(string studentId, int topN = 5);
    void RebuildTeacherCache(string teacherId);
    void PurgeTeacherCacheForStudent(string studentId);
}
