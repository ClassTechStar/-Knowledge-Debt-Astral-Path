using System.Text.Json;

namespace AstralPath.Core.Agent;

/// <summary>
/// 结构化输出容错解析（P0 迁移，源：Astralpath index.html parseJsonLoose，L6378-6387）。
/// 依次尝试：剥代码围栏后直接解析 → 截取首尾大括号解析 → 截取首尾方括号解析；均失败返回 null。
/// 构图/出题/计划等结构化任务共用，抗模型输出格式漂移。
/// </summary>
public static class JsonLoose
{
    public static JsonElement? Parse(string? text)
    {
        var t = (text ?? "").Trim();
        // 剥代码围栏（```json … ``` 或 ``` … ```）
        t = System.Text.RegularExpressions.Regex.Replace(t, "^\\s*[\\s\\S]*?```(?:json)?\\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, "```[\\s\\S]*$", "").Trim();
        var direct = TryParse(t);
        if (direct != null) return direct;
        var i = t.IndexOf('{'); var j = t.LastIndexOf('}');
        if (i >= 0 && j > i)
        {
            var obj = TryParse(t.Substring(i, j - i + 1));
            if (obj != null) return obj;
        }
        var i2 = t.IndexOf('['); var j2 = t.LastIndexOf(']');
        if (i2 >= 0 && j2 > i2)
        {
            var arr = TryParse(t.Substring(i2, j2 - i2 + 1));
            if (arr != null) return arr;
        }
        return null;
    }

    private static JsonElement? TryParse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
