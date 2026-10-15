using System.Text.Json;
using AstralPath.Core.Graph;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// P1 迁移验证：AI 增强构图 golden 契约（eval/golden/graph_merge.json）。
/// 覆盖标题归一化、构图 JSON 解析、合并去重/自环跳过/清环、题池保留（T15）。
/// </summary>
public class GoldenGraphMergeTests
{
    private static JsonDocument Load()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_GOLDEN_DIR");
        var dir = !string.IsNullOrWhiteSpace(env) && Directory.Exists(env) ? env : FindGoldenDir();
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "graph_merge.json")));
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

    private static (List<GNode> nodes, List<GEdge> edges) ReadGraph(JsonElement c)
    {
        var nodes = new List<GNode>();
        if (c.TryGetProperty("nodes", out var ns))
            foreach (var n in ns.EnumerateArray())
                nodes.Add(new GNode(
                    n.GetProperty("id").GetString()!,
                    n.GetProperty("title").GetString()!,
                    n.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    n.GetProperty("ch").GetInt32(),
                    n.TryGetProperty("kind", out var k) ? k.GetString() ?? "term" : "term",
                    n.TryGetProperty("src", out var s) ? s.GetString() ?? "rule" : "rule"));
        var edges = new List<GEdge>();
        if (c.TryGetProperty("edges", out var es))
            foreach (var e in es.EnumerateArray())
                edges.Add(new GEdge(
                    e.GetProperty("from").GetString()!,
                    e.GetProperty("to").GetString()!,
                    e.TryGetProperty("w", out var w) ? w.GetDouble() : 1.0,
                    e.TryGetProperty("etype", out var t) ? t.GetString() ?? "prerequisite" : "prerequisite",
                    e.TryGetProperty("conf", out var cf) && cf.ValueKind != JsonValueKind.Null ? cf.GetDouble() : null,
                    e.TryGetProperty("src", out var sc) ? sc.GetString() ?? "rule" : "rule"));
        return (nodes, edges);
    }

    private static AiGraphExtract ReadExtract(JsonElement ai)
    {
        var concepts = new List<AiConcept>();
        foreach (var c in ai.GetProperty("concepts").EnumerateArray())
            concepts.Add(new AiConcept(c.GetProperty("title").GetString()!, c.GetProperty("ch").GetInt32(), c.GetProperty("def").GetString()!));
        var prereqs = new List<AiPrereq>();
        foreach (var p in ai.GetProperty("prereqs").EnumerateArray())
            prereqs.Add(new AiPrereq(p.GetProperty("from").GetString()!, p.GetProperty("to").GetString()!, p.GetProperty("w").GetDouble()));
        return new AiGraphExtract(concepts, prereqs);
    }

    private static int _idCounter;
    private static string MakeId(string _) => "gen" + (++_idCounter);

    [Fact]
    public void Golden_GraphMerge_NormalizeTitle_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("normalizeCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var actual = AiGraphMerger.NormalizeTitle(c.GetProperty("input").GetString());
            Assert.True(actual == c.GetProperty("expect").GetString(),
                $"{id}: expect='{c.GetProperty("expect").GetString()}' actual='{actual}'");
        }
    }

    [Fact]
    public void Golden_GraphMerge_ExtractParse_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("extractCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var expect = c.GetProperty("expect");
            var parsed = AiGraphExtractParser.Parse(c.GetProperty("input").GetString());
            if (!expect.GetProperty("ok").GetBoolean())
            {
                Assert.True(parsed == null, $"{id}: expected null");
                continue;
            }
            Assert.True(parsed != null, $"{id}: expected extract but got null");
            Assert.Equal(expect.GetProperty("concepts").GetInt32(), parsed!.Concepts.Count);
            Assert.Equal(expect.GetProperty("prereqs").GetInt32(), parsed.Prereqs.Count);
        }
    }

    [Fact]
    public void Golden_GraphMerge_Merge_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("mergeCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var (nodes, edges) = ReadGraph(c);
            var extract = ReadExtract(c.GetProperty("ai"));
            var chMax = c.GetProperty("chMax").GetInt32();
            var result = AiGraphMerger.Merge(nodes, edges, extract, chMax, MakeId);

            if (c.GetProperty("expect").TryGetProperty("null", out var nullFlag) && nullFlag.GetBoolean())
            {
                Assert.True(result == null, $"{id}: expected null merge result");
                continue;
            }

            Assert.True(result != null, $"{id}: expected merge result but got null");
            var expect = c.GetProperty("expect");
            Assert.Equal(expect.GetProperty("addedN").GetInt32(), result!.AddedN);
            Assert.Equal(expect.GetProperty("addedE").GetInt32(), result.AddedE);
            Assert.Equal(expect.GetProperty("cut").GetInt32(), result.Cut);
            Assert.Equal(expect.GetProperty("nodeCount").GetInt32(), result.Nodes.Count);
            Assert.Equal(expect.GetProperty("edgeCount").GetInt32(), result.Edges.Count);

            if (expect.TryGetProperty("acyclic", out var ac) && ac.GetBoolean())
            {
                // 清环后应无环：拓扑排序不抛异常
                var nodeIds = result.Nodes.Select(n => n.Id).ToList();
                var edgeTuples = result.Edges.Select(e => (e.From, e.To)).ToList();
                var order = AstralPath.Core.Algorithms.GraphTopology.TopologicalOrder(nodeIds, edgeTuples);
                Assert.Equal(nodeIds.Count, order.Count);
            }
        }
    }

    [Fact]
    public void Golden_GraphMerge_PoolPreserve_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("poolPreserveCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var pool = new List<QStub>();
            foreach (var q in c.GetProperty("pool").EnumerateArray())
                pool.Add(new QStub(q.GetProperty("id").GetString()!, q.GetProperty("layer").GetString()!));
            var rebuilt = new List<QStub>();
            foreach (var q in c.GetProperty("rebuilt").EnumerateArray())
                rebuilt.Add(new QStub(q.GetProperty("id").GetString()!, q.GetProperty("layer").GetString()!));

            var merged = AiGraphMerger.KeepAiQuestions(pool, rebuilt, q => q.Layer, c.GetProperty("cap").GetInt32());
            var expect = c.GetProperty("expect");
            var ids = merged.Select(q => q.Id).ToList();
            Assert.Equal(expect.GetProperty("count").GetInt32(), merged.Count);
            var expectedOrder = expect.GetProperty("order").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.Equal(expectedOrder, ids);
        }
    }

    [Fact]
    public void DemoGraphAi_BuildsChainFromChapters()
    {
        var chapters = new[] { ("第一章 概述", 0), ("第二章 存储", 1), ("第三章 输入输出", 2) };
        var extract = DemoGraphAi.Build(chapters);
        Assert.Equal(3, extract.Concepts.Count);
        Assert.Equal(2, extract.Prereqs.Count);
        Assert.All(extract.Concepts, c => Assert.Contains("·核心", c.Title));
        Assert.Contains("演示", extract.Concepts[0].Def);
    }

    private sealed record QStub(string Id, string Layer);
}
