using System.Text.Json;
using AstralPath.Core.Algorithms;
using AstralPath.Core.Exam;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// P2 迁移验证：试卷作业错题结构化与知识点层级图谱 golden 契约（eval/golden/exam_parse.json）。
/// 覆盖板块识别、题头多形态、选项切分（独立行/内联）、答案分配（单题归属/编号批量/顺序兜底）、
/// 题型推断、页标记剥离、知识点匹配（长词优先+位置不重叠+单题上限）、
/// 三层节点图谱（主题/知识点/题目 + attach/tested/related/prerequisite 四类边 + 清环）。
/// </summary>
public class GoldenExamParseTests
{
    private static JsonDocument Load()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_GOLDEN_DIR");
        var dir = !string.IsNullOrWhiteSpace(env) && Directory.Exists(env) ? env : FindGoldenDir();
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "exam_parse.json")));
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

    private static List<ExamKpEntry> ReadLex(JsonElement parent)
    {
        var lex = new List<ExamKpEntry>();
        if (!parent.TryGetProperty("lex", out var arr)) return lex;
        foreach (var l in arr.EnumerateArray())
            lex.Add(new ExamKpEntry(
                l.GetProperty("title").GetString()!,
                l.GetProperty("w").GetDouble(),
                l.TryGetProperty("chTitle", out var ct) ? ct.GetString() : null,
                l.TryGetProperty("chId", out var ci) ? ci.GetString() : null,
                l.TryGetProperty("nodeId", out var ni) ? ni.GetString() : null));
        return lex;
    }

    private static List<ExamItem> ReadItems(JsonElement parent)
    {
        var items = new List<ExamItem>();
        var i = 0;
        if (parent.TryGetProperty("items", out var arr))
        {
            foreach (var it in arr.EnumerateArray())
            {
                var opts = it.TryGetProperty("options", out var o)
                    ? o.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();
                items.Add(new ExamItem(
                    ++i, i,
                    it.TryGetProperty("section", out var s) ? s.GetString() : null,
                    it.GetProperty("stem").GetString()!,
                    opts,
                    it.TryGetProperty("answer", out var a) ? a.GetString() ?? "" : "",
                    it.TryGetProperty("qtype", out var q) ? q.GetString() ?? "" : "",
                    "", new List<string>()));
            }
        }
        return items;
    }

    [Fact]
    public void Golden_Exam_Structure_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("structureCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var expect = c.GetProperty("expect");
            var st = ExamParser.Structure(c.GetProperty("input").GetString());

            Assert.Equal(expect.GetProperty("items").GetInt32(), st.Items.Count);

            if (expect.TryGetProperty("sections", out var secs))
            {
                var actual = st.Sections.Select(s => s.Title).ToList();
                var expected = secs.EnumerateArray().Select(x => x.GetString()!).ToList();
                Assert.True(expected.SequenceEqual(actual), $"{id}: sections expect=[{string.Join(",", expected)}] actual=[{string.Join(",", actual)}]");
            }
            if (expect.TryGetProperty("types", out var types))
            {
                var expected = types.EnumerateArray().Select(x => x.GetString()!).ToList();
                var actual = st.Items.Select(x => x.QType).ToList();
                Assert.True(expected.SequenceEqual(actual), $"{id}: types expect=[{string.Join(",", expected)}] actual=[{string.Join(",", actual)}]");
            }
            if (expect.TryGetProperty("answers", out var answers))
            {
                var expected = answers.EnumerateArray().Select(x => x.GetString()!).ToList();
                var actual = st.Items.Select(x => x.Answer).ToList();
                Assert.True(expected.SequenceEqual(actual), $"{id}: answers expect=[{string.Join(",", expected)}] actual=[{string.Join(",", actual)}]");
            }
            if (expect.TryGetProperty("options0", out var o0))
            {
                var expected = o0.EnumerateArray().Select(x => x.GetString()!).ToList();
                var actual = st.Items[0].Options.ToList();
                Assert.True(expected.SequenceEqual(actual), $"{id}: options0 expect=[{string.Join(",", expected)}] actual=[{string.Join(",", actual)}]");
            }
            if (expect.TryGetProperty("options0Count", out var o0c))
                Assert.Equal(o0c.GetInt32(), st.Items[0].Options.Count);
            if (expect.TryGetProperty("rawNos", out var rawNos))
            {
                var expected = rawNos.EnumerateArray().Select(x => x.GetInt32()).ToList();
                var actual = st.Items.Select(x => x.RawNo ?? 0).ToList();
                Assert.True(expected.SequenceEqual(actual), $"{id}: rawNos expect=[{string.Join(",", expected)}] actual=[{string.Join(",", actual)}]");
            }
            if (expect.TryGetProperty("type0", out var t0))
                Assert.Equal(t0.GetString(), st.Items[0].QType);
            if (expect.TryGetProperty("stemContains", out var sc))
                Assert.Contains(sc.GetString()!, st.Items[0].Stem);
            if (expect.TryGetProperty("headContains", out var hc))
                Assert.Contains(hc.GetString()!, st.Items[0].Stem);
            if (expect.TryGetProperty("stemNotContains", out var snc))
                Assert.DoesNotContain(snc.GetString()!, st.Items[0].Stem);
            if (expect.TryGetProperty("rawNotContains", out var rnc))
                Assert.DoesNotContain(rnc.GetString()!, st.Items[0].Raw);
        }
    }

    [Fact]
    public void Golden_Exam_SectionOf_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("sectionCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var actual = ExamParser.SectionOf(c.GetProperty("input").GetString()) != null;
            Assert.True(actual == c.GetProperty("expect").GetBoolean(),
                $"{id}: input='{c.GetProperty("input").GetString()}' expect={c.GetProperty("expect").GetBoolean()} actual={actual}");
        }
    }

    [Fact]
    public void Golden_Exam_MatchKps_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("matchKpCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var lex = ReadLex(c);
            var limit = c.GetProperty("limit").GetInt32();
            var hits = ExamParser.MatchKps(c.GetProperty("text").GetString(), lex, limit);
            var expect = c.GetProperty("expect");
            if (expect.TryGetProperty("count", out var cnt))
            {
                Assert.True(cnt.GetInt32() == hits.Count, $"{id}: count expect={cnt.GetInt32()} actual={hits.Count}");
            }
            if (expect.TryGetProperty("titles", out var titles))
            {
                var expected = titles.EnumerateArray().Select(x => x.GetString()!).ToList();
                var actual = hits.Select(h => h.Title).ToList();
                Assert.True(expected.SequenceEqual(actual), $"{id}: titles expect=[{string.Join(",", expected)}] actual=[{string.Join(",", actual)}]");
            }
        }
    }

    [Fact]
    public void Golden_Exam_ExamKg_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("kgCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var lex = ReadLex(c);
            var items = ReadItems(c);

            if (c.TryGetProperty("itemCount", out var ic))
            {
                // 生成 N 个含词库关键词的题目（题目节点上限用例）
                var n = ic.GetInt32();
                items = Enumerable.Range(0, n).Select(i => new ExamItem(
                    i + 1, i + 1, "一、单项选择题", "会计题目" + (i + 1),
                    new List<string> { "A.甲", "B.乙" }, "A", "choice", "", new List<string>())).ToList();
            }

            var bookEdges = new List<BookEdge>();
            if (c.TryGetProperty("bookEdges", out var be))
                foreach (var e in be.EnumerateArray())
                    bookEdges.Add(new BookEdge(e.GetProperty("from").GetString()!, e.GetProperty("to").GetString()!, e.GetProperty("tg").GetBoolean()));

            var kg = ExamKgBuilder.Build(
                c.GetProperty("paperName").GetString()!, items, null, lex,
                bookEdges.Count > 0 ? bookEdges : null);

            var expect = c.GetProperty("expect");
            if (expect.TryGetProperty("topicCount", out var tc))
                Assert.Equal(tc.GetInt32(), kg.Nodes.Count(n => n.Kind == "topic"));
            if (expect.TryGetProperty("kpCount", out var kc))
                Assert.Equal(kc.GetInt32(), kg.Nodes.Count(n => n.Kind == "kp"));
            if (expect.TryGetProperty("itemNodes", out var ino))
                Assert.Equal(ino.GetInt32(), kg.Nodes.Count(n => n.Kind == "item"));
            if (expect.TryGetProperty("statItems", out var si))
                Assert.Equal(si.GetInt32(), kg.Stats.Items);
            if (expect.TryGetProperty("shownItems", out var shi))
                Assert.Equal(shi.GetInt32(), kg.Stats.ShownItems);
            if (expect.TryGetProperty("edgeTypesContain", out var etc))
            {
                var present = kg.Edges.Select(e => e.EType).ToHashSet(StringComparer.Ordinal);
                foreach (var t in etc.EnumerateArray())
                    Assert.True(present.Contains(t.GetString()!), $"{id}: 缺少边类型 {t.GetString()}，实有 [{string.Join(",", present)}]");
            }
            if (expect.TryGetProperty("acyclic", out var ac) && ac.GetBoolean())
            {
                var nodeIds = kg.Nodes.Select(n => n.Id).ToList();
                var tuples = kg.Edges.Select(e => (e.From, e.To)).ToList();
                var order = GraphTopology.TopologicalOrder(nodeIds, tuples);
                Assert.Equal(nodeIds.Count, order.Count);
            }
        }
    }

    [Fact]
    public void Golden_Exam_QTypeLabel_AllCases()
    {
        using var doc = Load();
        foreach (var c in doc.RootElement.GetProperty("qtypeLabelCases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString();
            var actual = ExamParser.QTypeLabel(c.GetProperty("input").GetString());
            Assert.True(actual == c.GetProperty("expect").GetString(),
                $"{id}: expect='{c.GetProperty("expect").GetString()}' actual='{actual}'");
        }
    }

    [Fact]
    public void ExamParser_MaxItems_CapsAt300()
    {
        // 单卷题目上限 300（源 EXAM_MAX_ITEMS）
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 320; i++) sb.Append(i + ". 题干" + i + "\nA. 甲\nB. 乙\n");
        var st = ExamParser.Structure(sb.ToString());
        Assert.Equal(300, st.Items.Count);
    }

    [Fact]
    public void ExamKgBuilder_Triples_RelationLabels()
    {
        var lex = new List<ExamKpEntry> { new("会计", 1.6, NodeId: "n1") };
        var items = new List<ExamItem> { new(1, 1, "一、单项选择题", "会计题目", new List<string> { "A.甲" }, "A", "choice", "", new List<string>()) };
        var kg = ExamKgBuilder.Build("试卷", items, null, lex);
        var relations = kg.Triples.Select(t => t.Relation).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("归属", relations);
        Assert.Contains("考查", relations);
    }
}
