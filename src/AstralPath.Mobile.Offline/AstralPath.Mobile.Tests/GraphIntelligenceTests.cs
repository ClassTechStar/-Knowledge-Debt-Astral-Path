using AstralPath.Core.Algorithms;
using AstralPath.Core.Graph;
using Xunit;

namespace AstralPath.Mobile.Tests;

/// <summary>知识图谱 v2：构图 / 分层 / 关键路径 / 瓶颈 / 度量 / 布局 / 学习路径。</summary>
public class GraphIntelligenceTests
{
    // A → B → C → E
    // A → D → E
    private static readonly string[] Nodes = { "A", "B", "C", "D", "E" };
    private static readonly (string, string)[] Edges =
    {
        ("A", "B"), ("B", "C"), ("C", "E"), ("A", "D"), ("D", "E")
    };

    [Fact]
    public void KG01_Layers_LongestPathDepth()
    {
        var layer = GraphTopology.Layers(Nodes, Edges);
        Assert.Equal(0, layer["A"]);
        Assert.Equal(1, layer["B"]);
        Assert.Equal(1, layer["D"]);
        Assert.Equal(2, layer["C"]);
        Assert.Equal(3, layer["E"]);
    }

    [Fact]
    public void KG02_CriticalPath_LongestChain()
    {
        var path = GraphTopology.CriticalPath(Nodes, Edges);
        Assert.Equal(new[] { "A", "B", "C", "E" }, path);
    }

    [Fact]
    public void KG03_Bottleneck_ABlocksMost()
    {
        var b = GraphTopology.Bottlenecks(Nodes, Edges);
        Assert.Equal("A", b[0].Id);
        Assert.True(b[0].Blocked >= 4); // 删 A 后其余全堵
    }

    [Fact]
    public void KG04_DownstreamMass_Transitive()
    {
        var m = GraphTopology.DownstreamMass(Nodes, Edges);
        Assert.Equal(4, m["A"]); // B,C,D,E
        Assert.Equal(2, m["B"]); // C,E
        Assert.Equal(1, m["C"]); // E
    }

    [Fact]
    public void KG05_PageRank_RootHighestOrNear()
    {
        var r = GraphMetrics.PageRank(Nodes, Edges);
        Assert.True(r["E"] >= r["A"]); // DAG 上质量流向汇点
        Assert.True(Math.Abs(r.Values.Sum() - 1) < 1e-9);
    }

    [Fact]
    public void KG06_Layout_OrdersByLayer()
    {
        var placed = GraphLayout.Layered(Nodes, Edges);
        var map = placed.ToDictionary(p => p.Id);
        Assert.True(map["A"].X < map["B"].X);
        Assert.True(map["B"].X < map["C"].X);
        Assert.True(map["C"].X < map["E"].X);
    }

    [Fact]
    public void KG07_Layout_CrossingsBounded()
    {
        var placed = GraphLayout.Layered(Nodes, Edges);
        var map = placed.ToDictionary(p => p.Id);
        var x = GraphLayout.Crossings(Edges, map);
        Assert.True(x <= 2); // 重心排序后不应爆炸
    }

    [Fact]
    public void KG08_LearningPath_TopologySafe()
    {
        var path = LearningPath.Order(Nodes, Edges);
        var idx = path.Select((s, i) => (s.KpId, i)).ToDictionary(x => x.KpId, x => x.i);
        Assert.True(idx["A"] < idx["B"]);
        Assert.True(idx["B"] < idx["C"]);
        Assert.True(idx["C"] < idx["E"]);
    }

    [Fact]
    public void KG09_LearningPath_MarksCritical()
    {
        var path = LearningPath.Order(Nodes, Edges);
        Assert.Contains(path, s => s.KpId == "A" && s.Reason.Contains("关键"));
    }

    [Fact]
    public void KG10_Pmi_And_TextRank()
    {
        Assert.Equal(0, GraphInference.Pmi(0, 5, 5, 100));
        Assert.True(GraphInference.Pmi(20, 10, 10, 100) > 1);
        var tokens = new[] { "甲", "乙", "甲", "乙", "丙", "甲", "乙" };
        var rank = GraphInference.TextRank(tokens, window: 2);
        Assert.True(rank["甲"] > 0);
        Assert.True(rank["甲"] >= rank["丙"]);
    }

    [Fact]
    public void KG11_BuildFromText_HasChaptersAndEdges()
    {
        var text = "第1章 会计要素 基于会计要素之后才能理解会计等式。会计等式和借贷记账法 常一起出现。";
        var chapters = new[]
        {
            new GraphInference.Concept("C1", "会计要素", "chapter"),
            new GraphInference.Concept("C2", "会计等式", "chapter"),
            new GraphInference.Concept("C3", "借贷记账法", "chapter")
        };
        var kg = GraphInference.BuildFromText(text, chapters, maxConcepts: 8, pmiThreshold: 0.5);
        Assert.True(kg.Nodes.Count >= 3);
        Assert.True(kg.Edges.Count >= 2);
        // 不允许自环
        Assert.DoesNotContain(kg.Edges, e => e.From == e.To);
    }

    [Fact]
    public void KG12_HubScore_PrefersMiddle()
    {
        Assert.True(GraphMetrics.HubScore("C", Edges) > GraphMetrics.HubScore("E", Edges)); // 枢纽高于纯汇点
    }
}
