using System.Text.Json;
using AstralPath.Contracts;
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
            Environment.GetEnvironmentVariable("MIMO_PYTHON") ?? "",
            @"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\tools\ocr-venv\Scripts\python.exe",
            @"C:\Program Files\Xiaomi MiMo\resources\runtimes\win32-x64\python\python.exe"
        };
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;
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

    /// <summary>OCR 进程并发闸：高负载时避免多份 Python/Tesseract 打满 CPU/内存。</summary>
    private static readonly SemaphoreSlim OcrGate = new(2, 2);

    public static async Task<JsonElement> RunOcrAsync(string filePath, string ocrMode, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("资料文件不存在或已被删除", filePath);
        if (new FileInfo(filePath).Length <= 0)
            throw new InvalidDataException("资料文件为空");

        await OcrGate.WaitAsync(ct);
        try
        {
            return await RunOcrCoreAsync(filePath, ocrMode, ct);
        }
        finally
        {
            OcrGate.Release();
        }
    }

    private static async Task<JsonElement> RunOcrCoreAsync(string filePath, string ocrMode, CancellationToken ct)
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
            // 经典死锁：stderr 缓冲被 fontTools 等警告写满时，进程会卡在写 stderr，
            // 而顺序 ReadToEnd(stdout) 永远等不到 EOF。必须并行读 + 超时杀进程。
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new TimeoutException($"OCR 超时(180s)：{Path.GetFileName(filePath)} mode={ocrMode}");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!File.Exists(outFile))
                throw new InvalidOperationException($"OCR output missing. exit={proc.ExitCode} stdout={Truncate(stdout, 400)} stderr={Truncate(stderr, 400)}");

            var text = await File.ReadAllTextAsync(outFile, ct);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* ignore */ }
        }
    }

    private static string Truncate(string? s, int n)
    {
        s = s ?? "";
        return s.Length <= n ? s : s[..n] + "…";
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
        var nodes = graph.Nodes;
        var edges = graph.Edges;
        var issues = new List<string>();
        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var cycles = new List<string>();

        // 自动教材图谱：只要求无环 + 节点/边合法，不套用会计课程包「≥40 边」口径
        if (nodes.Count < 1)
            issues.Add("节点数为 0");
        foreach (var e in edges)
        {
            if (!ids.Contains(e.From)) issues.Add($"边起点不存在: {e.From}");
            if (!ids.Contains(e.To)) issues.Add($"边终点不存在: {e.To}");
            if (e.Weight <= 0 || e.Weight > 2)
                issues.Add($"边权重非法: {e.From}->{e.To} weight={e.Weight}");
        }

        // 无环检测（Kahn）
        var adj = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        var indeg = ids.ToDictionary(i => i, _ => 0);
        foreach (var e in edges)
        {
            if (!adj.ContainsKey(e.From) || !indeg.ContainsKey(e.To)) continue;
            adj[e.From].Add(e.To);
            indeg[e.To]++;
        }
        var q = new Queue<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var seen = 0;
        var remain = new Dictionary<string, int>(indeg);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            seen++;
            foreach (var v in adj[u])
            {
                remain[v]--;
                if (remain[v] == 0) q.Enqueue(v);
            }
        }
        if (seen != ids.Count)
        {
            cycles.Add($"存在环：拓扑排序仅覆盖 {seen}/{ids.Count} 节点");
            issues.Add(cycles[0]);
        }

        var ok = issues.Count == 0;
        return new GraphValidateResult(ok, nodes.Count, edges.Count, cycles, issues);
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
        // 去掉版本号/作者括号噪声，保留可读短书名
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"[（(\[].{0,24}[)）\]]", " ");
        stem = System.Text.RegularExpressions.Regex.Replace(stem, @"[_+\s]+", " ").Trim();
        if (stem.StartsWith("C ", StringComparison.Ordinal)) stem = "C#" + stem[2..];
        if (stem.Length > 12) stem = stem[..12];
        return string.IsNullOrWhiteSpace(stem) ? "MATERIAL" : stem;
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

    public static MaterialChapterBundle? ParseChapterBundle(string materialId, string materialName, JsonElement payload)
    {
        if (!payload.TryGetProperty("chapterBodies", out var chArr) || chArr.ValueKind != JsonValueKind.Array)
            return null;

        List<ChapterQuestionItem> ReadQs(JsonElement node)
        {
            var list = new List<ChapterQuestionItem>();
            if (!node.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array) return list;
            foreach (var q in qs.EnumerateArray())
            {
                var options = new List<string>();
                if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in opts.EnumerateArray())
                        options.Add(o.GetString() ?? "");
                }
                list.Add(new ChapterQuestionItem(
                    q.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
                    q.TryGetProperty("stem", out var stem) ? stem.GetString() ?? "" : "",
                    options,
                    q.TryGetProperty("correctIndex", out var ci) ? ci.GetInt32() : 0,
                    q.TryGetProperty("why", out var why) ? why.GetString() ?? "" : "",
                    q.TryGetProperty("type", out var t) ? t.GetString() ?? "quiz" : "quiz",
                    q.TryGetProperty("difficulty", out var d) ? d.GetInt32() : 2,
                    q.TryGetProperty("estMin", out var m) ? m.GetInt32() : 6,
                    q.TryGetProperty("kp", out var kp) ? kp.GetString() ?? "" : ""));
            }
            return list;
        }

        ChapterBodyItem ReadBody(JsonElement node) => new(
            node.TryGetProperty("id", out var id) ? id.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
            node.TryGetProperty("title", out var title) ? title.GetString() ?? "章节" : "章节",
            node.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "chapter" : "chapter",
            node.TryGetProperty("level", out var lv) ? lv.GetInt32() : 0,
            node.TryGetProperty("parentId", out var pid) && pid.ValueKind != JsonValueKind.Null ? pid.GetString() : null,
            node.TryGetProperty("chapterNum", out var cn) && cn.ValueKind != JsonValueKind.Null ? cn.GetInt32() : null,
            node.TryGetProperty("charCount", out var cc) ? cc.GetInt32() : 0,
            node.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "",
            ReadQs(node));

        var chapters = new List<ChapterBodyItem>();
        foreach (var n in chArr.EnumerateArray()) chapters.Add(ReadBody(n));

        var sections = new List<ChapterBodyItem>();
        if (payload.TryGetProperty("sectionBodies", out var secArr) && secArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in secArr.EnumerateArray()) sections.Add(ReadBody(n));
        }

        object toc = payload.TryGetProperty("toc", out var tEl) ? (object)tEl : new { };
        object stats = payload.TryGetProperty("deepStats", out var sEl) ? (object)sEl : new { };
        return new MaterialChapterBundle(materialId, materialName, chapters, sections, toc, stats);
    }

    public static List<TodayTaskDto> ChapterQuestionsToTasks(MaterialChapterBundle bundle, int max = 8)
    {
        var tasks = new List<TodayTaskDto>();
        foreach (var ch in bundle.Chapters)
        {
            foreach (var q in ch.Questions)
            {
                tasks.Add(new TodayTaskDto(
                    Guid.NewGuid().ToString("N"),
                    $"chapter:{ch.Id}",
                    string.IsNullOrWhiteSpace(q.Kp) ? ch.Title : q.Kp,
                    q.Type,
                    q.Difficulty,
                    q.EstMin,
                    $"来自《{bundle.MaterialName}》「{ch.Title}」：{q.Why}",
                    q.Id,
                    q.Stem,
                    q.Options,
                    q.CorrectIndex,
                    null));
                if (tasks.Count >= max) return tasks;
            }
        }
        return tasks;
    }
}
