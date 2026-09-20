using System.Text.Json;

namespace AstralPath.Shared.Json;

/// <summary>
/// 容错的 JSON 形状读取器。
///
/// 背景：<c>AstralPathModules</c> 的 §44/§45/§46 方法返回匿名类型（<c>object</c>），
/// 直接绑定到 XAML 既不可编译也不稳定。本类把 <c>object</c> 规范化为
/// <see cref="JsonElement"/> 后按属性名读取，视图模型再映射为自己的强类型行记录。
///
/// 设计：全部扩展方法以**非空** <see cref="JsonElement"/> 为接收器（避免 C# 扩展方法
/// 不接受隐式转换的问题）；缺失属性统一返回 <see cref="JsonValueKind.Undefined"/>，
/// 因此链式读取天然容错。
///
/// 取舍：<c>SerializeToElement</c> 走反射，启用 NativeAOT 时需改为
/// source-generated <c>JsonSerializerContext</c>（方案 §14.10 已列为 AOT 前置项）。
/// </summary>
public static class JsonShape
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>把任意对象（含匿名类型）规范化为 <see cref="JsonElement"/>；失败返回 Undefined。</summary>
    public static JsonElement Normalize(object? value)
    {
        switch (value)
        {
            case null:
                return default;
            case JsonElement je:
                return je;
            case JsonDocument jd:
                return jd.RootElement.Clone();
            case string s when s.TrimStart().StartsWith('{') || s.TrimStart().StartsWith('['):
                try { using var doc = JsonDocument.Parse(s); return doc.RootElement.Clone(); }
                catch (JsonException) { return default; }
            default:
                try { return JsonSerializer.SerializeToElement(value, Options); }
                catch (NotSupportedException) { return default; }
                catch (JsonException) { return default; }
        }
    }

    /// <summary>对象属性缺失时返回 Undefined，便于链式容错读取。</summary>
    public static JsonElement Prop(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        return element.TryGetProperty(name, out var v) ? v : default;
    }

    /// <summary>是否为缺失/空值（Undefined 或 Null）。</summary>
    public static bool IsMissing(this JsonElement element)
        => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    public static string? Str(this JsonElement element, string name)
    {
        var v = element.Prop(name);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            _ => v.ToString()
        };
    }

    /// <summary>按候选名依次尝试（用于兼容字段改名，如 Id/id、Tag/tag）。</summary>
    public static string? StrAny(this JsonElement element, params string[] names)
    {
        foreach (var n in names)
        {
            var v = element.Str(n);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    /// <summary>
    /// 按候选名依次尝试读取布尔值。
    ///
    /// 必要性：<see cref="System.Text.Json.JsonElement.TryGetProperty(string, out JsonElement)"/> 是
    /// **大小写敏感**的，而模块里匿名类型的属性名有 PascalCase（来自 record 属性）与
    /// camelCase（来自匿名对象初始化器）两种风格。只写一种大小写会导致「读不到 → 静默取默认值」
    /// 这类最难发现的缺陷（如 opt-out 永远读成 false）。
    /// </summary>
    public static bool BoolAny(this JsonElement element, params string[] names)
    {
        foreach (var n in names)
        {
            if (element.Prop(n).ValueKind is JsonValueKind.True or JsonValueKind.False)
                return element.Bool(n);
        }
        return false;
    }

    public static double Num(this JsonElement element, string name, double fallback = 0)
    {
        var v = element.Prop(name);
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), out var d) => d,
            _ => fallback
        };
    }

    public static int Int(this JsonElement element, string name, int fallback = 0)
    {
        var v = element.Prop(name);
        return v.ValueKind switch
        {
            JsonValueKind.Number => (int)Math.Round(v.GetDouble()),
            JsonValueKind.String when double.TryParse(v.GetString(), out var d) => (int)Math.Round(d),
            _ => fallback
        };
    }

    public static bool Bool(this JsonElement element, string name, bool fallback = false)
    {
        var v = element.Prop(name);
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => fallback
        };
    }

    /// <summary>读取数组属性；缺失或非数组返回空数组。</summary>
    public static JsonElement[] Arr(this JsonElement element, string name)
    {
        var v = element.Prop(name);
        if (v.ValueKind != JsonValueKind.Array) return Array.Empty<JsonElement>();
        return v.EnumerateArray().ToArray();
    }

    public static string[] StrArr(this JsonElement element, string name)
        => element.Arr(name)
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .ToArray();
}
