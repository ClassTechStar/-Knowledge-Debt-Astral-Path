using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AstralPath.Core.Agent;

namespace AstralPath.Core.Graph;

/// <summary>图谱节点（合并管线通用）。</summary>
public sealed record GNode(string Id, string Title, string Body, int Ch, string Kind, string Src);

/// <summary>图谱边（合并管线通用）。</summary>
public sealed record GEdge(string From, string To, double W, string EType, double? Conf, string Src);

/// <summary>LLM 抽取结果（mergeGraphAI 入参同构）。</summary>
public sealed record AiGraphExtract(IReadOnlyList<AiConcept> Concepts, IReadOnlyList<AiPrereq> Prereqs);

public sealed record AiConcept(string Title, int Ch, string Def);

public sealed record AiPrereq(string From, string To, double W);

/// <summary>合并结果（源 mergeGraphAI 返回 {addedN, addedE, cut}）。</summary>
public sealed record AiGraphMergeResult(int AddedN, int AddedE, int Cut, IReadOnlyList<GNode> Nodes, IReadOnlyList<GEdge> Edges);

/// <summary>
/// AI 增强构图合并管线（P1 迁移，源：Astralpath mergeGraphAI，L4361-4400）。
/// 流程：LLM 概念/先修去重合并（src:"ai" 标记）→ Kahn 拓扑清环 → 重建派生结构由调用方处理。
/// 关键约束（T15）：整池重建题池时必须保留 layer:"ai" 题——本类以 KeepAiQuestions 显式表达该约束。
/// </summary>
public static class AiGraphMerger
{
    /// <summary>标题归一化去重键（源 normalize + byT，L4364/L6637）。</summary>
    public static string NormalizeTitle(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var c = ch;
            if (c >= '！' && c <= '～') c = (char)(c - 0xFEE0); // 全角 → 半角
            if (c == '　') c = ' ';
            sb.Append(c);
        }
        return Regex.Replace(sb.ToString(), "\\s+", " ").Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 合并 LLM 抽取结果进图谱（源 L4361-4396）。
    /// 概念上限 20、先修上限 20；同名（归一化）跳过；自环/未知端点跳过；边去重 from&gt;to&gt;etype。
    /// 无任何新增返回 null（调用方保留原图谱）。
    /// </summary>
    public static AiGraphMergeResult? Merge(
        List<GNode> nodes, List<GEdge> edges, AiGraphExtract ai, int chMax,
        Func<string, string> makeId)
    {
        if (ai is null) return null;
        var byT = new Dictionary<string, GNode>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            var key = NormalizeTitle(n.Title);
            if (!byT.ContainsKey(key)) byT[key] = n;
        }

        var addedN = 0;
        foreach (var c in ai.Concepts.Take(20))
        {
            var t = (c.Title ?? "").Trim();
            if (t.Length == 0) continue;
            var key = NormalizeTitle(t);
            if (key.Length == 0 || byT.ContainsKey(key)) continue;
            var n = new GNode(
                makeId(t),
                t.Length <= 24 ? t : t.Substring(0, 24),
                (c.Def ?? "").Length <= 120 ? (c.Def ?? "") : c.Def!.Substring(0, 120),
                Math.Min(Math.Max(c.Ch, 0), chMax),
                Kind: "term",
                Src: "ai");
            nodes.Add(n);
            byT[key] = n;
            addedN++;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in edges) seen.Add(e.From + ">" + e.To + ">" + e.EType);
        var addedE = 0;
        foreach (var p in ai.Prereqs.Take(20))
        {
            if (p.From is null || p.To is null) continue;
            if (!byT.TryGetValue(NormalizeTitle(p.From), out var a)) continue;
            if (!byT.TryGetValue(NormalizeTitle(p.To), out var b)) continue;
            if (a.Id == b.Id) continue;
            var kk = a.Id + ">" + b.Id + ">prerequisite";
            if (!seen.Add(kk)) continue;
            edges.Add(new GEdge(a.Id, b.Id, Math.Min(Math.Max(p.W, 0.5), 2.0), "prerequisite", 0.75, "ai"));
            addedE++;
        }

        if (addedN == 0 && addedE == 0) return null;
        var cleaned = RemoveCyclesTopo(nodes, edges);
        return new AiGraphMergeResult(addedN, addedE, cleaned.Cut, nodes, cleaned.Edges);
    }

    /// <summary>
    /// Kahn 拓扑清环（源 removeCyclesTopo，L1536-1564）。
    /// 无环 → 原样返回；有环 → 按拓扑位置裁剪回边（pa&lt;pb 保留），返回裁剪数。
    /// 与 GraphTopology.TopologicalOrder 不同：不抛异常，而是裁剪回边保证债网无环可传播。
    /// </summary>
    public static (IReadOnlyList<GEdge> Edges, int Cut) RemoveCyclesTopo(IReadOnlyList<GNode> nodes, IReadOnlyList<GEdge> edges)
    {
        var deg = new Dictionary<string, int>(StringComparer.Ordinal);
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var n in nodes) { deg[n.Id] = 0; adj[n.Id] = new List<string>(); }
        foreach (var e in edges)
        {
            if (string.IsNullOrEmpty(e.From) || string.IsNullOrEmpty(e.To) || e.From == e.To) continue;
            if (!deg.ContainsKey(e.From) || !deg.ContainsKey(e.To)) continue;
            adj[e.From].Add(e.To);
            deg[e.To]++;
        }

        var order = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var n in nodes) if (deg[n.Id] == 0) { queue.Enqueue(n.Id); used.Add(n.Id); }
        while (queue.Count > 0)
        {
            var x = queue.Dequeue();
            order.Add(x);
            foreach (var y in adj[x])
            {
                if (--deg[y] == 0) { queue.Enqueue(y); used.Add(y); }
            }
        }

        if (order.Count >= nodes.Count)
            return (edges.Where(e => !string.IsNullOrEmpty(e.From) && !string.IsNullOrEmpty(e.To) && e.From != e.To).ToList(), 0);

        var pos = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++) pos[order[i]] = i;
        var rest = nodes.Where(n => !used.Contains(n.Id)).Select(n => n.Id).ToList();

        var kept = new List<GEdge>();
        var cut = 0;
        foreach (var e in edges)
        {
            if (string.IsNullOrEmpty(e.From) || string.IsNullOrEmpty(e.To) || e.From == e.To) continue;
            var pa = pos.TryGetValue(e.From, out var a) ? a : rest.IndexOf(e.From) + nodes.Count;
            var pb = pos.TryGetValue(e.To, out var b) ? b : rest.IndexOf(e.To) + nodes.Count;
            if (pa < pb) kept.Add(e); else cut++;
        }
        return (kept, cut);
    }

    /// <summary>
    /// 题池重建约束（T15，源 L4394-4398）：保留 layer:"ai" 题置前，仅重建规则题，超限从头截断。
    /// </summary>
    public static IReadOnlyList<Q> KeepAiQuestions<Q>(IEnumerable<Q> pool, IEnumerable<Q> rebuilt, Func<Q, string> layerOf, int cap = 300)
    {
        var keptAi = pool.Where(q => layerOf(q) == "ai").ToList();
        var merged = keptAi.Concat(rebuilt).ToList();
        return merged.Count > cap ? merged.Take(cap).ToList() : (IReadOnlyList<Q>)merged;
    }

    /// <summary>
    /// 泛型 Kahn 拓扑清环（P2 扩展）：供试卷知识图谱等异构节点/边结构复用同一清环语义，
    /// 避免为每种图谱重复实现。语义与上重载完全一致（无环原样返回；有环按拓扑位置裁剪回边）。
    /// </summary>
    public static (IReadOnlyList<TEdge> Edges, int Cut) RemoveCyclesTopo<TNode, TEdge>(
        IReadOnlyList<TNode> nodes,
        IReadOnlyList<TEdge> edges,
        Func<TNode, string> idOf,
        Func<TEdge, string> fromOf,
        Func<TEdge, string> toOf)
    {
        var nodeIds = nodes.Select(idOf).ToList();
        var deg = new Dictionary<string, int>(StringComparer.Ordinal);
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in nodeIds) { deg[id] = 0; adj[id] = new List<string>(); }
        foreach (var e in edges)
        {
            var f = fromOf(e); var t = toOf(e);
            if (string.IsNullOrEmpty(f) || string.IsNullOrEmpty(t) || f == t) continue;
            if (!deg.ContainsKey(f) || !deg.ContainsKey(t)) continue;
            adj[f].Add(t);
            deg[t]++;
        }

        var order = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var id in nodeIds) if (deg[id] == 0) { queue.Enqueue(id); used.Add(id); }
        while (queue.Count > 0)
        {
            var x = queue.Dequeue();
            order.Add(x);
            foreach (var y in adj[x]) if (--deg[y] == 0) { queue.Enqueue(y); used.Add(y); }
        }

        if (order.Count >= nodes.Count)
            return (edges.Where(e => { var f = fromOf(e); var t = toOf(e); return !string.IsNullOrEmpty(f) && !string.IsNullOrEmpty(t) && f != t; }).ToList(), 0);

        var pos = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++) pos[order[i]] = i;
        var rest = nodeIds.Where(id => !used.Contains(id)).ToList();

        var kept = new List<TEdge>();
        var cut = 0;
        foreach (var e in edges)
        {
            var f = fromOf(e); var t = toOf(e);
            if (string.IsNullOrEmpty(f) || string.IsNullOrEmpty(t) || f == t) continue;
            var pa = pos.TryGetValue(f, out var a) ? a : rest.IndexOf(f) + nodes.Count;
            var pb = pos.TryGetValue(t, out var b) ? b : rest.IndexOf(t) + nodes.Count;
            if (pa < pb) kept.Add(e); else cut++;
        }
        return (kept, cut);
    }
}

/// <summary>
/// 演示模式构图素材（P1 迁移，源：demoGraphAI，L4348-4357）。
/// 无 Key 时从章节标题派生概念/先修，走同一合并管线，明确标注【演示模式】。
/// </summary>
public static class DemoGraphAi
{
    public static AiGraphExtract Build(IEnumerable<(string Title, int Index)> chapters)
    {
        var concepts = new List<AiConcept>();
        var list = chapters.Take(6).ToList();
        foreach (var (title, i) in list)
        {
            var t = Regex.Replace(title ?? "", "^\\d+(\\.\\d+)*\\s*", "").Trim();
            if (t.Length == 0) continue;
            var core = (t.Length <= 20 ? t : t.Substring(0, 20)) + "·核心";
            var body = "（演示）「" + (t.Length <= 16 ? t : t.Substring(0, 16)) + "」相关概念，配置模型 Key 后由大模型生成 80 字定义。";
            concepts.Add(new AiConcept(core, i, body));
        }
        var prereqs = new List<AiPrereq>();
        for (var i = 0; i < concepts.Count - 1; i++)
            prereqs.Add(new AiPrereq(concepts[i].Title, concepts[i + 1].Title, 1.2));
        return new AiGraphExtract(concepts, prereqs);
    }
}

/// <summary>
/// 构图抽取 JSON 解析（源 btnGraphAI 的 parseJsonLoose 通道，L7269-7272）。
/// 期望 {"concepts":[{"title","ch","def"}],"prereqs":[{"from","to","w"}]}。
/// </summary>
public static class AiGraphExtractParser
{
    public static AiGraphExtract? Parse(string? modelText)
    {
        var root = JsonLoose.Parse(modelText);
        if (root == null) return null;
        var r = root.Value;

        var concepts = new List<AiConcept>();
        if (r.TryGetProperty("concepts", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cs.EnumerateArray().Take(20))
            {
                if (!c.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String) continue;
                var title = t.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(title)) continue;
                var ch = c.TryGetProperty("ch", out var chEl) && chEl.ValueKind == JsonValueKind.Number && chEl.TryGetInt32(out var chv) ? chv : 0;
                var def = c.TryGetProperty("def", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                concepts.Add(new AiConcept(title, ch, def));
            }
        }

        var prereqs = new List<AiPrereq>();
        if (r.TryGetProperty("prereqs", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray().Take(20))
            {
                if (!p.TryGetProperty("from", out var f) || f.ValueKind != JsonValueKind.String) continue;
                if (!p.TryGetProperty("to", out var to) || to.ValueKind != JsonValueKind.String) continue;
                var w = p.TryGetProperty("w", out var wEl) && wEl.ValueKind == JsonValueKind.Number && wEl.TryGetDouble(out var wv) ? wv : 1.2;
                prereqs.Add(new AiPrereq(f.GetString() ?? "", to.GetString() ?? "", w));
            }
        }

        if (concepts.Count == 0 && prereqs.Count == 0) return null;
        return new AiGraphExtract(concepts, prereqs);
    }

    /// <summary>构图系统提示词（源 btnGraphAI L7269）。</summary>
    public static string SystemPrompt =>
        "你是知识图谱构建器。严格输出 JSON：{\"concepts\":[{\"title\":\"概念名\",\"ch\":0,\"def\":\"不超过80字定义\"}],\"prereqs\":[{\"from\":\"概念A\",\"to\":\"概念B\",\"w\":1.2}]}。"
        + "concepts 给 8-15 个教材核心概念（不与已有术语重复），prereqs 给 5-12 条先修关系（from 为基础、to 建立在其上），"
        + "from/to 必须使用 concepts 或已有术语中的准确名称。不要输出 JSON 以外的任何内容。";

    public static string UserPrompt(string bookName, string toc, string terms)
        => "教材《" + bookName + "》章节目录：" + (string.IsNullOrEmpty(toc) ? "无" : toc) + "\n已有术语：" + (string.IsNullOrEmpty(terms) ? "无" : terms);
}
