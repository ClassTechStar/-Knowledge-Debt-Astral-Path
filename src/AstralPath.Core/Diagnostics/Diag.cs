using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AstralPath.Core.Diagnostics;

/// <summary>
/// 一条诊断记录（最近 50 条环形缓冲的元素）。
/// </summary>
/// <param name="At">发生时间（UTC）。</param>
/// <param name="Scope">调用点（成员名:行号），由编译器注入，不写死字符串。</param>
/// <param name="Kind">严重度：<c>recover</c>=已降级继续 / <c>fail</c>=不可恢复 / <c>note</c>=说明。</param>
/// <param name="Message">异常消息或备注。</param>
public sealed record DiagEntry(DateTimeOffset At, string Scope, string Kind, string Message)
{
    /// <summary>导出为单行文本（前端/日志文件可直接消费）。</summary>
    public override string ToString() =>
        $"{At:yyyy-MM-ddTHH:mm:ss.fffZ} [{Kind}] {Scope} :: {Message}";
}

/// <summary>
/// 轻量诊断器（方案 P13）：把「被吞掉的错误」变成可观测记录。
/// <para>
/// 背景：全仓曾有 40 处 <c>catch { }</c> 完全空吞——线上出问题时只能猜。本类型提供
/// 一个**零依赖、不抛异常、有界内存**的出口：
/// <list type="bullet">
/// <item>环形缓冲固定 50 条，不随运行时长增长（避免日志噪声把内存吃掉）；</item>
/// <item>调用点由 <see cref="CallerMemberNameAttribute"/> / <see cref="CallerLineNumberAttribute"/>
/// 自动注入，所以每个 catch 只需写 <c>Diag.Recover(ex)</c>，不产生「scope 字符串手抄不一致」；</item>
/// <item>纯内存实现，不写文件、不发网络——符合「纯前端+本地优先」的隐私约束。</item>
/// </list>
/// </para>
/// <para>
/// 使用约定（分类处置）：
/// 可恢复 → <see cref="Recover"/>（记录后继续，降级路径照旧）；
/// 不可恢复 → <see cref="Fail"/>（记录 + 交由上层返回 ProblemDetails / 用户提示）；
/// 纯说明 → <see cref="Note"/>。
/// </para>
/// </summary>
public static class Diag
{
    /// <summary>环形缓冲容量（与前端 error ring 保持一致）。</summary>
    public const int Capacity = 50;

    private static readonly ConcurrentQueue<DiagEntry> Ring = new();
    private static long _total;

    /// <summary>累计记录条数（含已被挤出缓冲的），用于判断「发生过多少次」而非「最近几次」。</summary>
    public static long Total => Interlocked.Read(ref _total);

    /// <summary>可恢复错误：记录后继续执行降级路径。绝不抛异常。</summary>
    public static void Recover(
        Exception? ex = null,
        string? note = null,
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => Record("recover", member, line, ex, note);

    /// <summary>不可恢复错误：记录后由上层转换为用户可见提示（ProblemDetails / toast）。</summary>
    public static void Fail(
        Exception? ex = null,
        string? note = null,
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => Record("fail", member, line, ex, note);

    /// <summary>说明性记录（非异常），例如「演示密钥信封失效，已回落内置」。</summary>
    public static void Note(
        string message,
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0)
        => Record("note", member, line, null, message);

    /// <summary>取最近 <see cref="Capacity"/> 条（新→旧）。</summary>
    public static IReadOnlyList<DiagEntry> Snapshot()
    {
        var list = Ring.ToArray();
        Array.Reverse(list); // ConcurrentQueue 是 FIFO，反转后最新在前
        return list;
    }

    /// <summary>清空缓冲（不影响 <see cref="Total"/>）。</summary>
    public static void Clear()
    {
        while (Ring.TryDequeue(out _)) { }
    }

    private static void Record(string kind, string member, int line, Exception? ex, string? note)
    {
        try
        {
            var msg = ex is null
                ? (note ?? "")
                : (note is null ? $"{ex.GetType().Name}: {ex.Message}" : $"{note} | {ex.GetType().Name}: {ex.Message}");
            var entry = new DiagEntry(DateTimeOffset.UtcNow, $"{member}:{line}", kind, Truncate(msg, 300));
            Ring.Enqueue(entry);
            while (Ring.Count > Capacity && Ring.TryDequeue(out _)) { }
            Interlocked.Increment(ref _total);
            System.Diagnostics.Debug.WriteLine("[diag] " + entry);
        }
        catch
        {
            // 诊断器自身绝不允许影响主流程：这里刻意吞掉——它已是最后一道防线。
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
