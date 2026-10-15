using System.Text;
using System.Text.Json;
using AstralPath.Core.Agent;
using AstralPath.Core.Quiz;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// P0 迁移验证：AI 智能出题 golden 契约（eval/golden/quiz.json）+ 编排重试语义。
/// 覆盖层级轮换、题干归一化/去重键、模型输出解析（双形态答案/钳制/围栏）、
/// 题池去重与上限截断、命题上下文预算、编排重试与"重复不入池交付"语义。
/// </summary>
public class GoldenQuizTests
{
    private static JsonDocument Load()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_GOLDEN_DIR");
        var dir = !string.IsNullOrWhiteSpace(env) && Directory.Exists(env)
            ? env
            : FindGoldenDir();
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "quiz.json")));
    }

    private static string FindGoldenDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var full = Path.Combine(dir, "eval", "golden");
            if (Directory.Exists(full)) return Path.GetFullPath(full);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
        }
        throw new DirectoryNotFoundException("eval/golden not found");
    }

    private static readonly MimoConfig Cfg = new("test-key", "https://mock.local/v1", "mimo-v2.6-pro");
    private static readonly QuizBook Book = new("计算机基础", "教材摘录内容。",
        new List<QuizChapter>
        {
            new("c1", "第一章 概述", "概述章节正文内容。", 0),
            new("c2", "第二章 存储", "存储章节正文：缓存映射是重点。", 1)
        });
    private static readonly QuizNode Node = new("n1", "缓存映射", "缓存映射决定主存块与缓存行的对应关系。", "c2", 1);

    [Fact]
    public void Golden_Quiz_LevelRotation_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("levelRotation").EnumerateArray())
        {
            var count = c.GetProperty("count").GetInt32();
            var expect = c.GetProperty("expect").GetString();
            Assert.Equal(expect, AiQuestionGen.PickQuizLevel(count));
        }
    }

    [Fact]
    public void Golden_Quiz_NormalizeStem_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("normalizeCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var actual = AiQuestionGen.NormalizeStem(c.GetProperty("input").GetString());
            Assert.True(actual == c.GetProperty("expect").GetString(),
                $"{id}: expect='{c.GetProperty("expect").GetString()}' actual='{actual}'");
        }
    }

    [Fact]
    public void Golden_Quiz_DedupKey_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("dedupKeyCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var actual = AiQuestionGen.DedupKey(c.GetProperty("input").GetString());
            Assert.True(actual == c.GetProperty("expect").GetString(),
                $"{id}: expect='{c.GetProperty("expect").GetString()}' actual='{actual}'");
        }
    }

    [Fact]
    public void Golden_Quiz_ParseQuiz_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("parseQuizCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var expect = c.GetProperty("expect");
            var q = AiQuestionGen.ParseQuiz(c.GetProperty("input").GetString(), "kp1", "概念理解", "aiq");
            if (!expect.GetProperty("ok").GetBoolean())
            {
                Assert.True(q == null, $"{id}: expected null but got question");
                continue;
            }
            Assert.True(q != null, $"{id}: expected question but got null");
            Assert.Equal(expect.GetProperty("ci").GetInt32(), q!.CorrectIndex);
            Assert.Equal("ai", q.Layer);
            if (expect.TryGetProperty("optionCount", out var oc))
                Assert.Equal(oc.GetInt32(), q.Options.Count);
            if (expect.TryGetProperty("explainPrefix", out var ep))
                Assert.StartsWith(ep.GetString(), q.Explain);
        }
    }

    [Fact]
    public void Golden_Quiz_PoolDedupAndCap_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("poolCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var pool = new List<AiQuestion>();
            if (c.TryGetProperty("adds", out var adds))
            {
                foreach (var a in adds.EnumerateArray())
                {
                    var q = MakeQuestion(a.GetProperty("stem").GetString()!);
                    var added = AiQuestionGen.TryAddToPool(pool, q);
                    Assert.True(added == a.GetProperty("expectAdded").GetBoolean(),
                        $"{id}: stem='{q.Stem}' expectAdded={a.GetProperty("expectAdded").GetBoolean()}");
                }
            }
            if (c.TryGetProperty("capTest", out var cap))
            {
                var n = cap.GetProperty("addCount").GetInt32();
                for (var i = 0; i < n; i++)
                    AiQuestionGen.TryAddToPool(pool, MakeQuestion("题目" + i));
                Assert.Equal(cap.GetProperty("expectCount").GetInt32(), pool.Count);
                // 从头截断：保留最新题（索引 addCount - expectCount 起）
                Assert.Equal("题目" + cap.GetProperty("expectFirstIndex").GetInt32(), pool[0].Stem);
            }
        }
    }

    [Fact]
    public void Golden_Quiz_BuildContext_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("contextCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var b = c.GetProperty("book");
            var chapters = new List<QuizChapter>();
            foreach (var ch in b.GetProperty("chapters").EnumerateArray())
                chapters.Add(new QuizChapter(ch.GetProperty("id").GetString()!, ch.GetProperty("title").GetString()!,
                    ch.GetProperty("body").GetString()!, ch.GetProperty("index").GetInt32()));
            var book = new QuizBook(b.GetProperty("name").GetString()!, b.GetProperty("sourceText").GetString()!, chapters);

            QuizNode? node = null;
            if (c.GetProperty("node").ValueKind == JsonValueKind.Object)
            {
                var n = c.GetProperty("node");
                node = new QuizNode(n.GetProperty("id").GetString()!, n.GetProperty("title").GetString()!,
                    n.GetProperty("body").GetString()!, n.GetProperty("chId").GetString()!, n.GetProperty("ch").GetInt32());
            }

            var ctx = AiQuestionGen.BuildQuizContext(book, node);
            var expect = c.GetProperty("expect");
            foreach (var needle in expect.GetProperty("containsAll").EnumerateArray())
                Assert.Contains(needle.GetString()!, ctx);
            Assert.True(ctx.Length <= expect.GetProperty("maxChars").GetInt32(),
                $"{id}: context length {ctx.Length} exceeds budget");
        }
    }

    [Fact]
    public async Task QuizOrchestrator_RetriesOnce_OnDuplicate_ThenDeliversWithoutPooling()
    {
        // 重试语义（源 L4475-4486）：重复 → 重试一次 → 仍重复 → 交付但不入池（DuplicateInPool=true）
        var pool = new List<AiQuestion> { MakeQuestion("关于缓存映射的说法") };
        var calls = 0;
        var client = new MimoClient((req, ct) =>
        {
            calls++;
            // 两次都返回与池内重复的题
            return Task.FromResult(MimoRawResponse.Json(200,
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"stem\\\":\\\"关于缓存映射的说法\\\",\\\"options\\\":[\\\"a\\\",\\\"b\\\",\\\"c\\\",\\\"d\\\"],\\\"answer\\\":0}\"}}]}"));
        });
        var orch = new AiQuestionOrchestrator(client);
        var result = await orch.GenerateAsync(Cfg, Book, Node, pool);
        Assert.NotNull(result);
        Assert.True(result!.DuplicateInPool);
        Assert.Equal(2, calls);
        Assert.Single(pool); // 未入池
    }

    [Fact]
    public async Task QuizOrchestrator_RetriesOnce_OnParseFailure_ThenReturnsNull()
    {
        // 解析失败 → 重试一次 → 仍失败 → null（调用方回退规则法）
        var calls = 0;
        var client = new MimoClient((req, ct) =>
        {
            calls++;
            return Task.FromResult(MimoRawResponse.Json(200,
                "{\"choices\":[{\"message\":{\"content\":\"不是 JSON\"}}]}"));
        });
        var orch = new AiQuestionOrchestrator(client);
        var result = await orch.GenerateAsync(Cfg, Book, Node, new List<AiQuestion>());
        Assert.Null(result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task QuizOrchestrator_Success_AddsToPool()
    {
        var pool = new List<AiQuestion>();
        var client = new MimoClient((req, ct) => Task.FromResult(MimoRawResponse.Json(200,
            "{\"choices\":[{\"message\":{\"content\":\"{\\\"stem\\\":\\\"CPU 的作用是什么？\\\",\\\"options\\\":[\\\"执行指令\\\",\\\"存储数据\\\",\\\"传输信号\\\",\\\"显示图像\\\"],\\\"answer\\\":0,\\\"explain\\\":\\\"解析：CPU 负责执行指令。\\\"}\"}}]}")));
        var orch = new AiQuestionOrchestrator(client);
        var result = await orch.GenerateAsync(Cfg, Book, Node, pool, level: "概念理解");
        Assert.NotNull(result);
        Assert.False(result!.DuplicateInPool);
        Assert.Single(pool);
        Assert.Equal("ai", pool[0].Layer);
        Assert.Equal("AI 出题·概念理解", pool[0].Why);
    }

    [Fact]
    public void QuizOrchestrator_Prompts_ContainLevelAndContext()
    {
        var level = "因果分析";
        var sys = AiQuestionOrchestrator.BuildSystemPrompt(level);
        Assert.Contains(level, sys);
        Assert.Contains("【拓展】", sys);
        Assert.Contains("禁止空泛", sys);
        Assert.Contains("严格输出 JSON", sys);

        var user = AiQuestionOrchestrator.BuildUserPrompt(Book, Node, level);
        Assert.Contains("教材《计算机基础》", user);
        Assert.Contains("命题知识点：缓存映射", user);
        Assert.Contains("考查层级：" + level, user);
        Assert.Contains("【知识点】缓存映射", user);
        Assert.Contains("【所在章节全文】第二章 存储", user);
        Assert.Contains("【全书目录】", user);
    }

    private static AiQuestion MakeQuestion(string stem)
        => new("q_" + Guid.NewGuid().ToString("N").Substring(0, 8), "kp1", stem,
            new[] { "a", "b", "c", "d" }, 0, "ai", "AI 出题·概念理解", "");
}
