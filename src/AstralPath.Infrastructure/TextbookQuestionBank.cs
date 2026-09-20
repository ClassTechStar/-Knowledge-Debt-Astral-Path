using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AstralPath.Contracts;

namespace AstralPath.Infrastructure;

public sealed record TextbookQuestion(
    string Kp,
    string Type,
    int Difficulty,
    int EstMin,
    string Stem,
    List<string> Options,
    int CorrectIndex,
    string Why);

/// <summary>教材真题题库：支持轮换（rotate）与洗牌（shuffle）。</summary>
public static class TextbookQuestionBank
{
    private static readonly object Gate = new();
    private static Dictionary<string, List<TextbookQuestion>> _banks = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _bankBookNames = new(StringComparer.OrdinalIgnoreCase);
    // 每本书的轮换游标（进程内）
    private static readonly Dictionary<string, int> _cursor = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    private static string ResolveBankPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "eval", "textbook-questions.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "eval", "textbook-questions.json"),
            @"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\eval\textbook-questions.json"
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return "";
    }

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            var path = ResolveBankPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var banks = new Dictionary<string, List<TextbookQuestion>>(StringComparer.OrdinalIgnoreCase);
                var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (doc.RootElement.TryGetProperty("banks", out var bankRoot))
                {
                    foreach (var prop in bankRoot.EnumerateObject())
                    {
                        var list = new List<TextbookQuestion>();
                        var book = prop.Name;
                        if (prop.Value.TryGetProperty("book", out var b))
                            book = b.GetString() ?? prop.Name;
                        names[prop.Name] = book;
                        if (prop.Value.TryGetProperty("questions", out var qs))
                        {
                            foreach (var q in qs.EnumerateArray())
                            {
                                var options = new List<string>();
                                if (q.TryGetProperty("options", out var opts))
                                {
                                    foreach (var o in opts.EnumerateArray())
                                        options.Add(o.GetString() ?? "");
                                }
                                list.Add(new TextbookQuestion(
                                    q.TryGetProperty("kp", out var kp) ? kp.GetString() ?? "" : "",
                                    q.TryGetProperty("type", out var t) ? t.GetString() ?? "quiz" : "quiz",
                                    q.TryGetProperty("difficulty", out var d) ? d.GetInt32() : 2,
                                    q.TryGetProperty("estMin", out var m) ? m.GetInt32() : 6,
                                    q.TryGetProperty("stem", out var s) ? s.GetString() ?? "" : "",
                                    options,
                                    q.TryGetProperty("correctIndex", out var ci) ? ci.GetInt32() : 0,
                                    q.TryGetProperty("why", out var w) ? w.GetString() ?? "" : ""));
                            }
                        }
                        banks[prop.Name] = list;
                    }
                }
                _banks = banks;
                _bankBookNames = names;
                foreach (var key in _banks.Keys)
                    _cursor[key] = 0;
            }
            catch
            {
                _banks = new Dictionary<string, List<TextbookQuestion>>();
            }
        }
    }

    public static string? MatchBankKey(string materialName)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(materialName)) return null;
        lock (Gate)
        {
            var n = materialName;
            if (ContainsAny(n, "Kotlin")) return Has("kotlin");
            if (ContainsAny(n, "Python编程", "从入门到实践")) return Has("python");
            if (ContainsAny(n, "自然语言处理", "Natural Language Processing")) return Has("nlp");
            if (ContainsAny(n, "大模型应用", "AI Agent", "动手做 AI")) return Has("agent");
            if (ContainsAny(n, "C#从入门", "C#", "csharp")) return Has("csharp");
            if (ContainsAny(n, "Go语言", "Go语", "Go语")) return Has("go");
            if (ContainsAny(n, "Java从入门", "Java从入门到精通")) return Has("java");
            if (ContainsAny(n, "深度学习入门", "基于Python的理论与实现", "花书", "Goodfellow", "自制框架", "强化学习")) return Has("deeplearning");
            if (ContainsAny(n, "深度学习进阶")) return Has("nlp") ?? Has("deeplearning");
            foreach (var key in _banks.Keys.OrderByDescending(k => k.Length))
            {
                if (key.Length >= 4 && n.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
            return null;
        }

        static string? Has(string key) => _banks.ContainsKey(key) ? key : null;
        static bool ContainsAny(string text, params string[] keys)
            => keys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>取一轮题目；rotate=true 时按游标轮换到下一批。</summary>
    public static List<TextbookQuestion> GetQuestions(string materialName, int max = 6, bool rotate = false, int? seed = null)
    {
        EnsureLoaded();
        var key = MatchBankKey(materialName);
        if (key == null) return new List<TextbookQuestion>();
        lock (Gate)
        {
            if (!_banks.TryGetValue(key, out var list) || list.Count == 0)
                return new List<TextbookQuestion>();

            var pool = list.ToList();
            if (rotate)
            {
                var cur = _cursor.GetValueOrDefault(key);
                if (seed.HasValue)
                    cur = Math.Abs(seed.Value) % Math.Max(1, pool.Count);
                else
                    cur = (cur + Math.Max(1, max)) % Math.Max(1, pool.Count);
                _cursor[key] = cur;

                // 从游标位置循环取 max 条
                var rotated = new List<TextbookQuestion>();
                for (var i = 0; i < Math.Min(max, pool.Count); i++)
                {
                    rotated.Add(pool[(cur + i) % pool.Count]);
                }
                return rotated;
            }

            return pool.Take(max).ToList();
        }
    }

    public static List<TodayTaskDto> ToTodayTasks(string materialName, int max = 6, bool rotate = false, int? seed = null)
    {
        var qs = GetQuestions(materialName, max, rotate, seed);
        var tasks = new List<TodayTaskDto>();
        var roundTag = rotate ? "·轮换" : "";
        foreach (var q in qs)
        {
            tasks.Add(new TodayTaskDto(
                Guid.NewGuid().ToString("N"),
                "bank:" + q.Kp,
                q.Kp,
                q.Type,
                q.Difficulty,
                q.EstMin,
                $"来自《{Short(materialName)}》教材正文{roundTag}：{q.Why}",
                "q-" + Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(materialName + q.Stem + Guid.NewGuid().ToString("N")))).ToLowerInvariant()[..12],
                q.Stem,
                q.Options,
                q.CorrectIndex,
                null));
        }
        return tasks;
    }

    public static object BankStats(string materialName)
    {
        EnsureLoaded();
        var key = MatchBankKey(materialName);
        lock (Gate)
        {
            if (key == null || !_banks.TryGetValue(key, out var list))
                return new { bank = (string?)null, total = 0, cursor = 0 };
            return new
            {
                bank = key,
                book = _bankBookNames.GetValueOrDefault(key),
                total = list.Count,
                cursor = _cursor.GetValueOrDefault(key)
            };
        }
    }

    public static object ListBanks()
    {
        EnsureLoaded();
        lock (Gate)
        {
            return _banks.ToDictionary(
                kv => kv.Key,
                kv => new
                {
                    book = _bankBookNames.GetValueOrDefault(kv.Key),
                    count = kv.Value.Count,
                    cursor = _cursor.GetValueOrDefault(kv.Key)
                });
        }
    }

    private static string Short(string s)
    {
        s = s ?? "";
        return s.Length <= 18 ? s : s[..18] + "…";
    }
}
