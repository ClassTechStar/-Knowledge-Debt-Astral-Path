using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AstralPath.Core.Agent;

namespace AstralPath.Core.Quiz;

/// <summary>
/// AI 智能出题核心逻辑（P0 迁移，源：Astralpath aiGenQuestion 链路，L4401-4490）。
/// 纯函数部分独立于此，便于 golden 对拍；模型调用由调用方经 MimoClient 注入。
/// 设计要点：
/// - 命题上下文 = 知识点正文 + 所在章节全文 + 全书目录（总预算约 3600 字），保证题目有据可依；
/// - 五认知层级轮换（概念理解/因果分析/对比辨析/应用场景/错误辨析）；
/// - 答案下标与字母双形态解析 + 范围钳制；
/// - 题干归一化去重（前 60 字）后入池，上限 300；
/// - 解析失败/与池重复自动重试一次（调用方 orchestrate，本层提供判定）。
/// </summary>
public static class AiQuestionGen
{
    public static readonly string[] Levels = { "概念理解", "因果分析", "对比辨析", "应用场景", "错误辨析" };

    /// <summary>层级轮换：按池内 AI 题数模 5 取层级（源 pickQuizLevel，L4406-4409）。</summary>
    public static string PickQuizLevel(int aiQuestionCount)
        => Levels[((aiQuestionCount % Levels.Length) + Levels.Length) % Levels.Length];

    // ── 题干归一化（源 normalize + isDup，L6637/L4474）────────────────────
    public static string NormalizeStem(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var c = ch;
            // 全角 → 半角（！-～ 区间，U+FF01-U+FF5E → -0xFEE0）
            if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0);
            if (c == '　') c = ' ';
            sb.Append(c);
        }
        return Regex.Replace(sb.ToString(), "\\s+", " ").Trim().ToLowerInvariant();
    }

    /// <summary>归一化题干前 60 字（去重键）。</summary>
    public static string DedupKey(string? stem)
    {
        var n = NormalizeStem(stem);
        return n.Length <= 60 ? n : n.Substring(0, 60);
    }

    /// <summary>与题池重复判定（源 isDup）。</summary>
    public static bool IsDuplicate(string? stem, IReadOnlyList<AiQuestion> pool)
    {
        var key = DedupKey(stem);
        return pool.Any(q => DedupKey(q.Stem) == key);
    }

    // ── 模型输出解析（源 parseQuiz，L4461-4472）──────────────────────────
    /// <summary>
    /// 解析模型 JSON 输出为题目；结构不合法（缺 options/stem、选项数≠4）返回 null。
    /// 答案索引支持数字与字母 A-D 双形态，越界钳制到 0（源 L4464-4465）。
    /// </summary>
    public static AiQuestion? ParseQuiz(string? modelText, string? kp, string level, string idPrefix = "aiq")
    {
        var j = JsonLoose.Parse(modelText);
        if (j == null) return null;
        var root = j.Value;
        if (!root.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array || opts.GetArrayLength() != 4)
            return null;
        if (!root.TryGetProperty("stem", out var stemEl) || stemEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(stemEl.GetString()))
            return null;

        var ci = 0;
        if (root.TryGetProperty("answer", out var ansEl))
        {
            if (ansEl.ValueKind == JsonValueKind.Number && ansEl.TryGetInt32(out var n)) ci = n;
            else if (ansEl.ValueKind == JsonValueKind.String)
            {
                var s = ansEl.GetString()!.Trim();
                if (Regex.IsMatch(s, "^[A-Da-d]$"))
                    ci = char.ToUpperInvariant(s[0]) - 'A';
                else if (int.TryParse(s, out var parsed))
                    ci = parsed;
            }
        }
        if (ci < 0 || ci > 3) ci = 0; // 范围钳制

        var optionTexts = new List<string>(4);
        foreach (var o in opts.EnumerateArray())
            optionTexts.Add(Limit(Sanitize(o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : o.ToString()), 140));

        var explain = root.TryGetProperty("explain", out var ex) && ex.ValueKind == JsonValueKind.String
            ? Limit(Sanitize(ex.GetString()!), 240) : "";

        var stem = Limit(Sanitize(stemEl.GetString()!), 240);
        if (stem.Length == 0 || optionTexts[0].Length == 0) return null;

        return new AiQuestion(
            idPrefix + "_" + DateTime.UtcNow.Ticks.ToString("x") + Random.Shared.Next(0, 0x10000).ToString("x4"),
            kp ?? "",
            stem,
            optionTexts,
            ci,
            Layer: "ai",
            Why: "AI 出题·" + level,
            Explain: explain);
    }

    // ── 题池管理（源 L4483：push 后超 300 从头截断）────────────────────────
    public const int PoolCap = 300;

    /// <summary>入池（去重命中则不入）；超上限从头截断，返回是否入池。</summary>
    public static bool TryAddToPool(List<AiQuestion> pool, AiQuestion question)
    {
        if (IsDuplicate(question.Stem, pool)) return false;
        pool.Add(question);
        if (pool.Count > PoolCap) pool.RemoveRange(0, pool.Count - PoolCap);
        return true;
    }

    // ── 命题上下文（源 buildQuizContext，L4424-4442）──────────────────────
    public const int ContextBudget = 3600;

    public static string BuildQuizContext(QuizBook book, QuizNode? node)
    {
        var parts = new List<string>();
        var budget = ContextBudget;
        string Take(string? s)
        {
            s ??= "";
            var t = s.Length <= budget ? s : s.Substring(0, budget);
            budget -= t.Length;
            return t;
        }

        if (node != null)
        {
            var ch = ChapterOfNode(book, node);
            parts.Add("【知识点】" + node.Title + (string.IsNullOrEmpty(node.Body) ? "" : "：" + Take(node.Body)));
            if (ch != null && !string.IsNullOrEmpty(ch.Body))
                parts.Add("【所在章节全文】" + ch.Title + "：" + Take(ch.Body));
            else
                parts.Add("【教材摘录】" + Take(book.SourceText));
            parts.Add("【全书目录】" + Take(string.Join("；", book.Chapters.Select(c => c.Title))));
        }
        else
        {
            var chs = book.Chapters.Take(8).ToList();
            parts.Add("【全书目录】" + Take(string.Join("；", chs.Select(c => c.Title))));
            foreach (var c in chs)
                if (!string.IsNullOrEmpty(c.Body))
                    parts.Add("【" + c.Title + "】" + Take(c.Body.Length <= 320 ? c.Body : c.Body.Substring(0, 320)));
            parts.Add("【教材摘录】" + Take(book.SourceText));
        }
        return string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>章节归属（源 chapterOfNode，L4415-4423）。</summary>
    public static QuizChapter? ChapterOfNode(QuizBook book, QuizNode node)
    {
        if (node.ChId != null)
        {
            var hit = book.Chapters.FirstOrDefault(c => c.Id == node.ChId);
            if (hit != null) return hit;
        }
        if (node.Ch >= 0 && node.Ch < book.Chapters.Count) return book.Chapters[node.Ch];
        var byBody = book.Chapters.FirstOrDefault(c => !string.IsNullOrEmpty(c.Body) && c.Body.Contains(node.Title));
        return byBody ?? (book.Chapters.Count > 0 ? book.Chapters[0] : null);
    }

    private static string Sanitize(string t) => t.Replace("﻿", "").Trim();
    private static string Limit(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
}

/// <summary>AI 命题（与前端 question 对象同构子集）。</summary>
public sealed record AiQuestion(
    string Id,
    string Kp,
    string Stem,
    IReadOnlyList<string> Options,
    int CorrectIndex,
    string Layer,
    string Why,
    string Explain);

/// <summary>命题上下文用书模型。</summary>
public sealed record QuizBook(string Name, string SourceText, IReadOnlyList<QuizChapter> Chapters);

public sealed record QuizChapter(string Id, string Title, string Body, int Index);

public sealed record QuizNode(string Id, string Title, string Body, string? ChId, int Ch);
