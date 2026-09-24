using System.Diagnostics;
using AstralPath.Core.Graph;
using AstralPath.Core.Ocr;
using Xunit;

namespace AstralPath.Mobile.Tests;

/// <summary>
/// v3 优化回归：知识图谱（稀疏 TextRank / NPMI / 章节锚定 / DAG 收尾 / 多趟布局）
/// 与 OCR（标识符保护 / 页眉页脚 / 断字还原 / 合并保护 / 间隙聚类 / 模糊投票）。
/// 每条都对应 v2 的一个具体缺陷。
/// </summary>
public class GraphOcrV3Tests
{
    // ── 图谱 · TextRank 稀疏化 ──────────────────────────
    [Fact]
    public void G31_TextRank_LargeInput_IsFastAndNormalized()
    {
        var rnd = new Random(42);
        var tokens = new string[40000];
        for (var i = 0; i < tokens.Length; i++)
            tokens[i] = "w" + rnd.Next(1500); // 1500 词表：v2 的稠密矩阵是 1500²=225 万 double ≈ 18MB

        var sw = Stopwatch.StartNew();
        var rank = GraphInference.TextRank(tokens, window: 5);
        sw.Stop();

        Assert.True(rank.Count > 0);
        Assert.True(Math.Abs(rank.Values.Sum() - 1.0) < 1e-6, "v3 输出归一化");
        Assert.True(sw.ElapsedMilliseconds < 20000, $"4 万 token 应可控，实测 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void G32_TextRank_Semantics_Kept()
    {
        // 与 v2 同语义：高频共现词得分更高
        var tokens = new[] { "甲", "乙", "甲", "乙", "丙", "甲", "乙" };
        var rank = GraphInference.TextRank(tokens, window: 2);
        Assert.True(rank["甲"] > 0);
        Assert.True(rank["甲"] >= rank["丙"]);
        Assert.True(Math.Abs(rank["甲"] - rank["乙"]) < 1e-9, "甲乙对称");
    }

    // ── 图谱 · NPMI 抑制低频偏置 ────────────────────────
    [Fact]
    public void G33_Npmi_Bounded_And_Smoothed_LessThan_Raw()
    {
        // 低频共现：裸 PMI 虚高，平滑后应明显更低
        var raw = GraphInference.Pmi(2, 3, 3, 100);
        var smoothed = GraphInference.PmiSmoothed(2, 3, 3, 100);
        Assert.True(raw > 0);
        Assert.True(smoothed < raw, "平滑应压低低频词对得分");

        var npmi = GraphInference.NormalizedPmi(20, 30, 30, 100);
        Assert.True(npmi >= -1.0 && npmi <= 1.0, "NPMI 必须落在 [-1,1]");
    }

    // ── 图谱 · 术语章节锚定（v2 最大 bug）────────────────
    [Fact]
    public void G34_TermAnchoring_DistributesAcrossChapters()
    {
        var text = "第1章 会计要素。资产 负债 所有者权益 收入 费用 利润 成本。\n"
                 + "第2章 会计等式。借贷 余额 试算 平衡 调整 分录 账簿。";
        var chapters = new[]
        {
            new GraphInference.Concept("C1", "会计要素", "chapter"),
            new GraphInference.Concept("C2", "会计等式", "chapter")
        };
        var kg = GraphInference.BuildFromText(text, chapters, maxConcepts: 24, pmiThreshold: 0.2, minTermFreq: 1);

        var termIds = kg.Nodes.Where(n => n.Kind == "term").Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.True(termIds.Count >= 4, $"术语数={termIds.Count}");

        var anchors = kg.Edges
            .Where(e => e.EdgeType == "prerequisite" && termIds.Contains(e.To))
            .Select(e => e.From)
            .Distinct()
            .ToList();

        // v2 会把所有术语挂到同一章（FirstOrDefault 恒定）；v3 应至少分布到两章
        Assert.True(anchors.Count >= 2, $"锚定章节数={anchors.Count}（v2 恒为 1）");
    }

    // ── 图谱 · DAG 收尾 ─────────────────────────────────
    [Fact]
    public void G35_EnsureDag_BreaksCycle()
    {
        var kg = new WeightedKg();
        foreach (var n in new[] { "A", "B", "C" }) kg.AddNode(n, n, "term");
        kg.AddEdge("A", "B", "prerequisite", 1.0, 0.9);
        kg.AddEdge("B", "C", "prerequisite", 1.0, 0.5);
        kg.AddEdge("C", "A", "prerequisite", 1.0, 0.1); // 成环，置信度最低
        Assert.Equal(3, kg.Edges.Count);

        var removed = GraphInference.EnsureDag(kg);
        Assert.True(removed >= 1);
        Assert.DoesNotContain(kg.Edges, e => e.From == "C" && e.To == "A"); // 丢掉最弱那条

        var order = AstralPath.Core.Algorithms.GraphTopology.TopologicalOrder(
            kg.Nodes.Select(n => n.Id).ToList(), kg.Pairs());
        Assert.Equal(kg.Nodes.Count, order.Count); // 现在能完整拓扑排序
    }

    // ── 图谱 · 多趟布局减少交叉 ─────────────────────────
    [Fact]
    public void G36_Layout_ResolvesInvertedOrdering()
    {
        // A→F, B→E, C→D：初始按字典序会把 D,E,F 排成 D,E,F，产生交叉；
        // 重心法应重排为 F,E,D → 0 交叉
        var nodes = new[] { "A", "B", "C", "D", "E", "F" };
        var edges = new[] { ("A", "F"), ("B", "E"), ("C", "D") };

        var placed = GraphLayout.Layered(nodes, edges);
        var map = placed.ToDictionary(p => p.Id, StringComparer.Ordinal);
        Assert.Equal(0, GraphLayout.Crossings(edges, map));

        // 菱形图同样应无交叉
        var dNodes = new[] { "A", "B", "C", "D", "E" };
        var dEdges = new[] { ("A", "B"), ("B", "C"), ("C", "E"), ("A", "D"), ("D", "E") };
        var dPlaced = GraphLayout.Layered(dNodes, dEdges);
        var dMap = dPlaced.ToDictionary(p => p.Id, StringComparer.Ordinal);
        Assert.True(GraphLayout.Crossings(dEdges, dMap) <= 1);
    }

    // ── 图谱 · 学习路径 PageRank 归一化 ──────────────────
    [Fact]
    public void G37_LearningPath_RankNormalized_And_TopologySafe()
    {
        var nodes = new[] { "A", "B", "C", "D", "E" };
        var edges = new[] { ("A", "B"), ("B", "C"), ("C", "E"), ("A", "D"), ("D", "E") };
        var path = LearningPath.Order(nodes, edges);
        var idx = path.Select((s, i) => (s.KpId, i)).ToDictionary(x => x.KpId, x => x.i);

        Assert.True(idx["A"] < idx["B"]);
        Assert.True(idx["B"] < idx["C"]);
        Assert.True(idx["C"] < idx["E"]);
        // 优先级应落在有限区间（PageRank 归一化后不会趋近于 0 而失效）
        Assert.True(path.All(s => s.Priority >= 0 && s.Priority <= 5));
        // 根节点的下游阻塞质量最大
        Assert.Equal(4, path.First(s => s.KpId == "A").Blocked);
    }

    // ── OCR · 标识符保护（v2 会腐蚀）────────────────────
    [Theory]
    [InlineData("Win10")]
    [InlineData("ISO9001")]
    [InlineData("C02")]
    [InlineData("GPT4")]
    [InlineData("H2O")]
    [InlineData("my_var1")]
    public void O31_FixConfusions_ProtectsIdentifiers(string token)
    {
        var s = OcrTextEngine.FixConfusions(token);
        Assert.Contains(token, s); // v2 会把它们改成 WinlO / ISO9OOl 之类
    }

    [Fact]
    public void O32_FixConfusions_StillFixesRealConfusions()
    {
        // 普通英文单词里的 0 仍应修（l0ve → lOve 类场景）
        var s = OcrTextEngine.FixConfusions("l0ve");
        Assert.Contains("O", s);
        // 全角归一保持
        var t = OcrTextEngine.FixConfusions("会计等式：资产（Assets）＝负债");
        Assert.Contains("(Assets)", t);
        Assert.DoesNotContain("＝", t);
    }

    // ── OCR · 页眉页脚去除 ──────────────────────────────
    [Fact]
    public void O33_RemoveRepeatedBoilerplate_DropsRunningHeader()
    {
        var header = "第3章 会计等式";
        var footer = "—— 42 ——";
        var pages = new List<string>();
        for (var p = 0; p < 6; p++)
            pages.Add($"{header}\n这是第 {p} 页的正文内容。\n{footer}");
        var text = string.Join("\f", pages);

        var cleaned = OcrTextEngine.RemoveRepeatedBoilerplate(text);
        Assert.DoesNotContain(header, cleaned);
        Assert.Contains("这是第 0 页的正文内容。", cleaned);
    }

    [Fact]
    public void O34_RemoveRepeatedBoilerplate_KeepsOneOffLines()
    {
        // 只出现一次的短行不应被误删（页数不足 3 时整体跳过）
        var text = "一次性的短句\n另一行正文内容在这里";
        Assert.Equal(text, OcrTextEngine.RemoveRepeatedBoilerplate(text));
    }

    // ── OCR · 英文断字还原 ──────────────────────────────
    [Fact]
    public void O35_Dehyphenate_JoinsBrokenWords()
    {
        Assert.Equal("international", OcrTextEngine.Dehyphenate("inter-\nnational"));
        // 下一行大写开头（专名/新句）不合并
        Assert.Contains("-\n", OcrTextEngine.Dehyphenate("inter-\nNational"));
    }

    // ── OCR · 断行合并保护 ──────────────────────────────
    [Fact]
    public void O36_Reconstruct_DoesNotMergeListItems()
    {
        var raw = "会计的基本职能包括：\n1. 核算\n2. 监督";
        var s = OcrTextEngine.ReconstructCjkLines(raw);
        Assert.Contains("1. 核算", s);
        Assert.Contains("2. 监督", s); // 编号行不得被并进上一段
    }

    [Fact]
    public void O37_Reconstruct_StillMergesSoftWrap()
    {
        var raw = "会计等式是复式记账的\n基础，必须先掌握。";
        var s = OcrTextEngine.ReconstructCjkLines(raw);
        Assert.Contains("会计等式是复式记账的基础", s.Replace(" ", ""));
    }

    // ── OCR · TSV 间隙聚类（抗基线漂移）──────────────────
    [Fact]
    public void O38_TsvRows_SurviveBaselineDrift()
    {
        // 同一行内 Y 缓慢漂移（每行 +1px），v2 拿行首词 Y 做基准会串行
        var words = new List<OcrTextEngine.WordTok>();
        for (var i = 0; i < 5; i++) words.Add(new OcrTextEngine.WordTok($"w{i}", 90, i * 20, 100 + i));
        for (var i = 0; i < 5; i++) words.Add(new OcrTextEngine.WordTok($"v{i}", 90, i * 20, 200 + i));

        var text = OcrTextEngine.ReconstructFromTsvWords(words, minConf: 40, rowTolerance: 8);
        var lines = text.Split('\n');
        Assert.Equal(2, lines.Length); // 必须仍是两行
    }

    // ── OCR · 模糊投票 ──────────────────────────────────
    [Fact]
    public void O39_VotePasses_ToleratesMinorDifferences()
    {
        // 三路 PSM 结果只有空白/全角差异，v2 精确匹配会全投不中
        var a = "会计要素\n会计等式";
        var b = "会计 要素\n会计等式";
        var c = "会计要素\n会计等式 ";
        var voted = OcrTextEngine.VotePasses(new[] { a, b, c });
        Assert.Contains("会计要素", voted);
        Assert.Contains("会计等式", voted);
    }

    [Fact]
    public void O40_VotePasses_RejectsHallucination()
    {
        // 注意：VotePasses 会忽略长度 ≤1 的行，故用 2 字以上的词
        var voted = OcrTextEngine.VotePasses(new[] { "甲甲乙乙\n幻觉行X", "甲甲乙乙\n另一幻觉", "甲甲乙乙\n丙丙丁丁" });
        Assert.Contains("甲甲乙乙", voted);
        Assert.DoesNotContain("幻觉行X", voted);
        Assert.DoesNotContain("丙丙丁丁", voted); // 只出现 1 路，未达多数
    }
}
