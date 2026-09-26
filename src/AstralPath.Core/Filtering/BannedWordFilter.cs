namespace AstralPath.Core.Filtering;

/// <summary>禁词过滤：展示层二次过滤；命中必须重写或降级为模板。</summary>
public sealed class BannedWordFilter
{
    private readonly string[] _words;

    public BannedWordFilter(IEnumerable<string> words)
    {
        _words = words
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> Words => _words;

    public bool ContainsBanned(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return _words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>命中禁词则降级为模板文案，否则原样返回。</summary>
    public string Sanitize(string? text, string fallbackTemplate)
        => ContainsBanned(text) ? fallbackTemplate : (text ?? string.Empty);
}
