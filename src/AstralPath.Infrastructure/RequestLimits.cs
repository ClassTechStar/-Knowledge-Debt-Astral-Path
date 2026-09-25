namespace AstralPath.Infrastructure;

/// <summary>
/// 入参长度/规模上限（P3-4）。
///
/// 背景：原实现没有任何长度校验——实测 10 万字符的 kpId、12MB 的请求体打到一个只该接收
/// 小对象的小接口，都返回 201 并被原样存下。这类输入不会立刻报错，但会污染统计口径、
/// 放大内存占用，并让"知识图谱节点名"这类展示位出现不可读的长串。
///
/// 用法保持最简：给一组 (字段名, 值, 上限)，返回第一条违规描述；全部通过返回 null。
/// </summary>
public static class RequestLimits
{
    /// <summary>各字段的推荐上限。</summary>
    public const int Id = 64;
    public const int ShortText = 128;
    public const int Title = 200;
    public const int Body = 20000;
    public const int BulkRows = 5000;

    /// <summary>返回第一条违规描述；无违规则 null。</summary>
    public static string? FirstViolation(params (string Field, string? Value, int Max)[] checks)
    {
        foreach (var (field, value, max) in checks)
        {
            if (value is null) continue;
            if (value.Length > max)
                return $"{field} 长度 {value.Length} 超出上限 {max}";
        }
        return null;
    }

    /// <summary>集合规模校验。</summary>
    public static string? FirstOverflow(string field, int actual, int max)
        => actual > max ? $"{field} 数量 {actual} 超出上限 {max}" : null;
}
