using System.Text;
using System.Text.RegularExpressions;

namespace AstralPath.Core.Agent;

/// <summary>
/// 模式集匹配（§47 OC-1）：归一化 + Contains 级联 / 正则回退。
/// 语义等价：命中任一模式即 true；用于危机词、禁词、锚点。
/// </summary>
public sealed class PatternSet
{
    private readonly string[] _patterns;
    private readonly bool[] _isRegex;
    private readonly Regex[] _regexes;
    public string Name { get; }
    public int Count => _patterns.Length;

    public PatternSet(string name, IEnumerable<string> patterns)
    {
        Name = name;
        var list = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        _patterns = list.ToArray();
        _isRegex = new bool[_patterns.Length];
        _regexes = new Regex[_patterns.Length];
        for (var i = 0; i < _patterns.Length; i++)
        {
            var p = _patterns[i];
            var looksRegex = p.Contains('(') || p.Contains('[') || p.Contains('|') || p.Contains('*') || p.Contains('+');
            _isRegex[i] = looksRegex;
            if (looksRegex)
            {
                _regexes[i] = new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
    }

    /// <summary>文本归一化：全半角、空白、常见繁简差异折叠（§47 R0/R3）。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = ch;
            // 全角 → 半角
            if (c >= 0xFF01 && c <= 0xFF5E) c = (char)(c - 0xFEE0);
            if (c == 0x3000) c = ' ';
            if (char.IsWhiteSpace(c) || c == '　') c = ' ';
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public bool ContainsAny(string? raw)
    {
        var norm = Normalize(raw);
        if (norm.Length == 0) return false;
        for (var i = 0; i < _patterns.Length; i++)
        {
            if (_isRegex[i])
            {
                if (_regexes[i].IsMatch(norm) || _regexes[i].IsMatch(raw ?? "")) return true;
            }
            else
            {
                var p = Normalize(_patterns[i]);
                if (p.Length > 0 && norm.Contains(p, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    public (bool Hit, string? Pattern, int Index) MatchFirst(string? raw)
    {
        var norm = Normalize(raw);
        for (var i = 0; i < _patterns.Length; i++)
        {
            if (_isRegex[i])
            {
                if (_regexes[i].IsMatch(norm) || _regexes[i].IsMatch(raw ?? ""))
                    return (true, _patterns[i], i);
            }
            else
            {
                var p = Normalize(_patterns[i]);
                if (p.Length > 0 && norm.Contains(p, StringComparison.Ordinal))
                    return (true, _patterns[i], i);
            }
        }
        return (false, null, -1);
    }

    /// <summary>返回命中的最长模式长度（用于判断"整句是否就是这句客套话"）。</summary>
    public int LongestMatchLength(string? raw)
    {
        var norm = Normalize(raw);
        if (norm.Length == 0) return 0;
        var best = 0;
        for (var i = 0; i < _patterns.Length; i++)
        {
            var p = Normalize(_patterns[i]);
            if (p.Length == 0) continue;
            if (_isRegex[i])
            {
                if (_regexes[i].IsMatch(norm)) best = Math.Max(best, norm.Length);
            }
            else if (norm.Contains(p, StringComparison.Ordinal))
            {
                best = Math.Max(best, p.Length);
            }
        }
        return best;
    }
}
