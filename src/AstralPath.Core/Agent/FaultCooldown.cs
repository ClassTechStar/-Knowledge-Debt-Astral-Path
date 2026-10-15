namespace AstralPath.Core.Agent;

/// <summary>
/// 故障冷却分类（P0 迁移，源：Astralpath __t13TxErr，L7052）。
/// 仅传输/网络类错误（HTTP 状态、网络失败、超时、中断）触发冷却；
/// 模型内容类问题（JSON 解析失败等）不冷却，下次照常重试。冷却期默认 8 秒（T14：30→8）。
/// </summary>
public static class FaultCooldown
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(8);

    private static readonly string[] TransportMarkers =
    {
        "HTTP ", "Failed to fetch", "NetworkError", "network error",
        "abort", "timeout", "超时", "load failed", "connection", "socket",
        "Name or service not known", "No such host", "Connection refused"
    };

    /// <summary>判定异常是否属于传输类故障（应触发冷却）。</summary>
    public static bool IsTransportError(Exception e)
        => IsTransportMessage(e?.Message ?? "") || e is HttpRequestException or TaskCanceledException or TimeoutException;

    public static bool IsTransportMessage(string message)
    {
        var m = message ?? "";
        return TransportMarkers.Any(k => m.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
