using System.Text.Json;
using AstralPath.Graph;

namespace AstralPath.Infrastructure;

public sealed record GraphExtras(
    object Triples,
    object PropertyGraph,
    object Mindmap,
    string MarkdownOutline,
    object? LogicalLayout);

public sealed record MaterialDoc(
    string Id,
    string Name,
    string Path,
    long SizeBytes,
    string Status, // uploaded | parsing | ready | failed
    string? OcrMode,
    bool OcrUsed,
    int PageCount,
    int ExtractedChars,
    int NodeCount,
    int EdgeCount,
    string? GraphId,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Error);

public sealed record AutoKnowledgeGraph(
    string GraphId,
    string MaterialId,
    string MaterialName,
    int GraphVersion,
    DateTime GeneratedAt,
    IReadOnlyList<KpNode> Nodes,
    IReadOnlyList<KpEdge> Edges,
    string Source);

public static class MaterialPipeline
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ResolveOcrScript()
    {
        var candidates = new[]
        {
            @"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\tools\ocr_pipeline.py",
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "ocr_pipeline.py"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ocr_pipeline.py")
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        throw new FileNotFoundException("ocr_pipeline.py not found");
    }

    public static string ResolvePython()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_OCR_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var candidates = new[]
        {
            @"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\tools\ocr-venv\Scripts\python.exe",
            @"C:\Program Files\Xiaomi MiMo\resources\runtimes\win32-x64\python\python.exe"
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return "python";
    }

    /// <summary>tesseract 环境：官方 CLI + 本仓库 tessdata（对齐 GitHub/tesseract）。</summary>
    public static string? ResolveTesseract()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_TESSERACT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var candidates = new[]
        {
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? ResolveTessdata()
    {
        var env = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        var candidates = new[]
        {
            @"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\tools\tessdata",
            @"C:\Program Files\Tesseract-OCR\tessdata"
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    public static async Task<JsonElement> RunOcrAsync(string filePath, string ocrMode, CancellationToken ct = default)
    {
        var script = ResolveOcrScript();
        var python = ResolvePython();
        var outFile = Path.Combine(Path.GetTempPath(), $"astralpath_ocr_{Guid.NewGuid():N}.json");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("--ocr");
            psi.ArgumentList.Add(ocrMode);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(outFile);

            // tesseract 对齐：CLI + tessdata（与官方 README 一致）
            var tess = ResolveTesseract();
            var tessdata = ResolveTessdata();
            if (!string.IsNullOrWhiteSpace(tess))
            {
                psi.Environment["ASTRALPATH_TESSERACT"] = tess;
                psi.Environment["PATH"] = (Path.GetDirectoryName(tess) ?? "") + Path.PathSeparator + (psi.Environment["PATH"] ?? "");
            }
            if (!string.IsNullOrWhiteSpace(tessdata))
            {
                psi.Environment["TESSDATA_PREFIX"] = tessdata;
                psi.Environment["ASTRALPATH_TESSDATA"] = tessdata;
            }

            using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("failed to start OCR process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (!File.Exists(outFile))
                throw new InvalidOperationException($"OCR output missing. stdout={stdout} stderr={stderr}");

            var text = await File.ReadAllTextAsync(outFile, ct);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* ignore */ }
        }
    }

    public static AutoKnowledgeGraph ToGraph(MaterialDoc material, JsonElement payload)
    {
        var nodes = new List<KpNode>();
        var edges = new List<KpEdge>();
        var course = SanitizeCourse(material.Name);

        if (payload.TryGetProperty("nodes", out var nArr) && nArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in nArr.EnumerateArray())
            {
                var rawCourse = n.TryGetProperty("course", out var c) ? c.GetString() ?? "" : "";
                // 禁止把 materialId/hash 当 course；用可读短书名
                var nodeCourse = NormalizeCourseLabel(rawCourse, course);
                nodes.Add(new KpNode(
                    n.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                    n.GetProperty("name").GetString() ?? "未命名",
                    nodeCourse,
                    n.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""));
            }
        }

        if (payload.TryGetProperty("edges", out var eArr) && eArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in eArr.EnumerateArray())
            {
                edges.Add(new KpEdge(
                    e.GetProperty("from").GetString() ?? "",
                    e.GetProperty("to").GetString() ?? "",
                    e.TryGetProperty("edgeType", out var t) ? t.GetString() ?? "prerequisite" : "prerequisite",
                    e.TryGetProperty("weight", out var w) ? w.GetDouble() : 1.0,
                    e.TryGetProperty("source", out var s) ? s.GetString() ?? "auto" : "auto"));
            }
        }

        // keep only edges with both endpoints
        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        edges = edges.Where(x => ids.Contains(x.From) && ids.Contains(x.To)).ToList();

        var graphId = $"auto-{material.Id}";
        return new AutoKnowledgeGraph(
            graphId,
            material.Id,
            material.Name,
            1,
            DateTime.UtcNow,
            nodes,
            edges,
            material.OcrUsed ? "ocr+heuristic" : "text+heuristic");
    }

    public static GraphValidateResult ValidateGenerated(AutoKnowledgeGraph graph)
    {
        var pack = new GraphPack(
            graph.GraphId,
            graph.MaterialName,
            "AUTO",
            graph.GraphVersion,
            graph.GeneratedAt.ToString("O"),
            graph.Nodes,
            graph.Edges);
        return new KnowledgeGraph(pack).Validate();
    }

    /// <summary>导出三元组/属性图/思维导图树（对齐 KnowledgeGraph 与 mind-map）。</summary>
    public static GraphExtras ExportGraphExtras(AutoKnowledgeGraph graph)
    {
        var nameOf = graph.Nodes.ToDictionary(n => n.Id, n => n.Name, StringComparer.Ordinal);
        var triples = graph.Edges.Select(e => new
        {
            subject = nameOf.GetValueOrDefault(e.From, e.From),
            subjectId = e.From,
            predicate = PredicateOf(e.Source, e.EdgeType),
            predicateSource = e.Source,
            @object = nameOf.GetValueOrDefault(e.To, e.To),
            objectId = e.To,
            weight = e.Weight
        }).ToList();

        var propertyGraph = new
        {
            nodes = graph.Nodes.Select(n => new
            {
                id = n.Id,
                name = n.Name,
                labels = new[] { LabelOf(n) },
                properties = new { course = n.Course, description = n.Description }
            }).ToList(),
            relationships = graph.Edges.Select(e => new
            {
                from = e.From,
                to = e.To,
                type = (e.EdgeType ?? "related").ToUpperInvariant(),
                properties = new { weight = e.Weight, source = e.Source, predicate = PredicateOf(e.Source, e.EdgeType) }
            }).ToList()
        };

        var children = graph.Nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in graph.Edges)
        {
            if (!IsTreeEdge(e.Source)) continue;
            if (!nameOf.ContainsKey(e.From) || !nameOf.ContainsKey(e.To) || e.From == e.To) continue;
            if (parent.ContainsKey(e.To)) continue;
            parent[e.To] = e.From;
            children[e.From].Add(e.To);
        }
        var roots = graph.Nodes.Where(n => !parent.ContainsKey(n.Id)).Select(n => n.Id).ToList();
        var title = graph.MaterialName.Length > 24 ? graph.MaterialName[..24] + "…" : graph.MaterialName;
        var mindmapNode = BuildMindNode("__root__", title, roots, children, nameOf, 0);

        var outline = new List<string>();
        void Walk(MindNode node, int depth)
        {
            if (depth <= 5) outline.Add(new string('#', Math.Max(1, depth)) + " " + node.Text);
            foreach (var c in node.Children) Walk(c, depth + 1);
        }
        Walk(mindmapNode, 1);

        return new GraphExtras(triples, propertyGraph, mindmapNode.ToJson(), string.Join("\n", outline), null);
    }

    private sealed record MindNode(string Id, string Text, List<MindNode> Children)
    {
        public object ToJson() => new
        {
            data = new { text = Text, id = Id, expand = true },
            children = Children.Select(c => c.ToJson()).ToList()
        };
    }

    private static MindNode BuildMindNode(string id, string text, List<string> childIds,
        Dictionary<string, List<string>> children, Dictionary<string, string> names, int depth)
    {
        var kids = new List<MindNode>();
        if (depth < 5)
        {
            foreach (var cid in childIds.Take(40))
            {
                kids.Add(BuildMindNode(cid, names.GetValueOrDefault(cid, cid),
                    children.GetValueOrDefault(cid) ?? new List<string>(), children, names, depth + 1));
            }
        }
        return new MindNode(id, text, kids);
    }

    private static bool IsTreeEdge(string? source)
        => source != null && (
            source.Contains("chapter-sequence") || source.Contains("section-parent")
            || source.Contains("section-near") || source.Contains("term-anchor")
            || source.Contains("term-fallback") || source.Contains("prerequisite"));

    private static string PredicateOf(string? source, string? edgeType)
    {
        if (source != null && source.Contains("chapter-sequence")) return "后继章节";
        if (source != null && source.Contains("section-parent")) return "隶属于章节";
        if (source != null && source.Contains("term-anchor")) return "涉及关键词";
        if (source != null && source.Contains("cooccurrence")) return "共现相关";
        return edgeType ?? "related";
    }

    private static string LabelOf(KpNode n)
    {
        var d = n.Description ?? "";
        if (d is "chapter" or "section" or "term" or "title") return d;
        if (d.Contains("chapter")) return "chapter";
        if (d.Contains("section")) return "section";
        if (d.Contains("freq=") || d.Contains("关键词")) return "term";
        return "concept";
    }

    private static string SanitizeCourse(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name) ?? "MATERIAL";
        stem = stem.Trim();
        if (stem.Length == 0) return "MATERIAL";
        // 可读短标签：优先取前 16 字，去掉路径非法字符
        var cleaned = new string(stem.Take(16).ToArray())
            .Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "MATERIAL" : cleaned;
    }

    private static string NormalizeCourseLabel(string raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var s = raw.Trim();
        // materialId / guid / hash：几乎全是 hex 且较长
        var hexish = s.Length >= 16 && s.All(ch => char.IsAsciiHexDigit(ch) || ch == '-');
        if (hexish) return fallback;
        if (s.Length > 20) s = s[..20];
        return s;
    }

    public static MaterialDoc ToDto(string id, string name, string path, long size, JsonElement payload, string ocrMode)
    {
        var ocrUsed = payload.TryGetProperty("ocrUsed", out var ou) && ou.GetBoolean();
        var pageCount = payload.TryGetProperty("pageCount", out var pc) ? pc.GetInt32() : 0;
        var chars = payload.TryGetProperty("extractedChars", out var ch) ? ch.GetInt32() : 0;
        var nodes = payload.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Array ? n.GetArrayLength() : 0;
        var edges = payload.TryGetProperty("edges", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
        var notes = payload.TryGetProperty("notes", out var nt) && nt.ValueKind == JsonValueKind.Array
            ? string.Join("; ", nt.EnumerateArray().Select(x => x.GetString()))
            : null;
        var now = DateTime.UtcNow;
        return new MaterialDoc(id, name, path, size, "ready", ocrMode, ocrUsed, pageCount, chars, nodes, edges,
            $"auto-{id}", notes, now, now, null);
    }
}
