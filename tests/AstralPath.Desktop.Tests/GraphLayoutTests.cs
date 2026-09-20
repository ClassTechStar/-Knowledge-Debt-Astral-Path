using System.Diagnostics;
using AstralPath.Contracts;
using AstralPath.Shared.Services;
using Xunit;

namespace AstralPath.Desktop.Tests;

/// <summary>
/// 图谱布局（方案 §12.2 / §14.7）：确定性、分层正确性、400 节点帧预算。
/// </summary>
public sealed class GraphLayoutTests
{
    [Fact]
    public void 布局是确定性的_同输入必得同输出()
    {
        var dto = SyntheticGraph(60, 90);

        var a = GraphLayout.Build(dto, 800, 600);
        var b = GraphLayout.Build(dto, 800, 600);

        Assert.Equal(a.Nodes.Count, b.Nodes.Count);
        Assert.Equal(
            a.Nodes.Select(n => (n.Id, n.X, n.Y, n.Layer)),
            b.Nodes.Select(n => (n.Id, n.X, n.Y, n.Layer)));
        Assert.Equal(
            a.Edges.Select(e => (e.From, e.To, e.X1, e.Y1, e.X2, e.Y2)),
            b.Edges.Select(e => (e.From, e.To, e.X1, e.Y1, e.X2, e.Y2)));
    }

    [Fact]
    public void 分层满足前置依赖方向()
    {
        // K00 → K01 → K02 链，外加 K00 → K02 的跨层边
        var nodes = new[] { Node("K00"), Node("K01"), Node("K02") };
        var edges = new[] { Edge("K00", "K01"), Edge("K01", "K02"), Edge("K00", "K02") };
        var dto = new GraphViewDto("s", 1, "w", nodes.ToList(), edges.ToList(), 3, DateTime.UtcNow);

        var scene = GraphLayout.Build(dto, 900, 600);
        var layers = scene.Nodes.ToDictionary(n => n.Id, n => n.Layer, StringComparer.Ordinal);

        Assert.Equal(0, layers["K00"]);
        Assert.Equal(1, layers["K01"]);
        Assert.Equal(2, layers["K02"]);
    }

    [Fact]
    public void 宽画布沿X分层_高画布沿Y分层()
    {
        var dto = SyntheticGraph(4, 4);

        var wide = GraphLayout.Build(dto, 1200, 400);
        var tall = GraphLayout.Build(dto, 400, 1200);

        // 宽画布：层序号越大 X 越大
        var wideLayers = wide.Nodes.GroupBy(n => n.Layer).OrderBy(g => g.Key).ToList();
        Assert.True(wideLayers.First().Average(n => n.X) < wideLayers.Last().Average(n => n.X));

        // 高画布：层序号越大 Y 越大
        var tallLayers = tall.Nodes.GroupBy(n => n.Layer).OrderBy(g => g.Key).ToList();
        Assert.True(tallLayers.First().Average(n => n.Y) < tallLayers.Last().Average(n => n.Y));
    }

    [Fact]
    public void 节点全部落在画布内()
    {
        var dto = SyntheticGraph(120, 200);
        var scene = GraphLayout.Build(dto, 1000, 700);

        Assert.All(scene.Nodes, n =>
        {
            Assert.InRange(n.X, 0, 1000);
            Assert.InRange(n.Y, 0, 700);
        });
    }

    [Fact]
    public void 四百节点布局在五十毫秒预算内完成()
    {
        var dto = SyntheticGraph(400, 700);

        // 预热一次（JIT + 字典分配），再计时，避免把首次编译成本算进预算
        GraphLayout.Build(dto, 1200, 800);

        var sw = Stopwatch.StartNew();
        var scene = GraphLayout.Build(dto, 1200, 800);
        sw.Stop();

        Assert.Equal(400, scene.Nodes.Count);
        Assert.True(
            sw.ElapsedMilliseconds < 50,
            $"400 节点布局耗时 {sw.ElapsedMilliseconds}ms，超出 50ms 预算（方案 §14.7）");
    }

    [Fact]
    public void 空图与退化尺寸不抛异常()
    {
        var empty = new GraphViewDto("s", 1, "w", new List<GraphViewNodeDto>(),
            new List<GraphViewEdgeDto>(), 0, DateTime.UtcNow);

        Assert.Empty(GraphLayout.Build(empty, 800, 600).Nodes);
        Assert.Empty(GraphLayout.Build(SyntheticGraph(5, 5), 0, 0).Nodes);
    }

    // ── 合成图（确定性 DAG） ────────────────────────────────────

    private static GraphViewNodeDto Node(string id)
        => new(id, $"知识点 {id}", "ACC-101", 60, "yellow");

    private static GraphViewEdgeDto Edge(string from, string to)
        => new(from, to, 40, "open", 1.0, $"知识点 {from}", $"知识点 {to}", 30, 60, 4);

    /// <summary>生成 <paramref name="nodeCount"/> 个节点、<paramref name="edgeCount"/> 条边的无环图。</summary>
    private static GraphViewDto SyntheticGraph(int nodeCount, int edgeCount)
    {
        var nodes = Enumerable.Range(0, nodeCount)
            .Select(i => Node($"K{i:D4}"))
            .ToList();

        var edges = new List<GraphViewEdgeDto>(edgeCount);
        for (var i = 0; i < edgeCount; i++)
        {
            // 只连 i → i+k（k ≥ 1），保证无环
            var from = i % nodeCount;
            var to = (from + 1 + i % 7) % nodeCount;
            if (to <= from) to = (from + 1) % nodeCount;
            if (to == from) continue;
            edges.Add(Edge($"K{from:D4}", $"K{to:D4}"));
        }

        return new GraphViewDto("synthetic", 1, "w1", nodes, edges, edges.Count, DateTime.UtcNow);
    }
}
