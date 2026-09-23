using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AstralPath.Contracts;
using AstralPath.Infrastructure;

namespace AstralPath.Api.Controllers;

[ApiController]
public sealed class MaterialsController : ControllerBase
{
    private readonly AstralPathStore _store;

    public MaterialsController(AstralPathStore store) => _store = store;

    private static string MaterialsDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "materials-uploads");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    [HttpGet("/v1/materials")]
    public IResult ListMaterials()
    {
        MaterialRegistry.HydrateFromDirectory(MaterialsDir);
        return HttpResults.Success(MaterialRegistry.ListMaterials());
    }

    [HttpPost("/v1/materials/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024)]
    public async Task<IResult> Upload([FromForm(Name = "file")] IFormFile? file, [FromQuery] string ocr = "standard")
    {
        try
        {
            var list = await SaveUploadedFilesAsync(file, null, ocr);
            if (list == null)
            {
                return HttpResults.Fail(400, ErrorCodes.ValidationError,
                    "请上传资料文件（PDF/图片/文本）。字段名 file 或 files。",
                    new
                    {
                        hint = "支持一次选择多个文件；也可对每个文件分别上传",
                        contentType = Request.ContentType,
                        formKeys = Request.HasFormContentType ? Request.Form.Keys.ToArray() : Array.Empty<string>()
                    });
            }
            object payload = list.Count == 1 ? list[0] : list;
            return HttpResults.Created(payload);
        }
        catch (Exception ex)
        {
            return HttpResults.Fail(500, ErrorCodes.InternalError, $"保存上传失败：{ex.Message}",
                new { hint = "可减小单次体积后重试，或改用 upload-batch 分批" });
        }
    }

    /// <summary>一次上传多个文件并排队解析。</summary>
    [HttpPost("/v1/materials/upload-batch")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(2L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024)]
    public async Task<IResult> UploadBatch([FromForm(Name = "files")] List<IFormFile>? files, [FromQuery] string ocr = "standard")
    {
        try
        {
            var first = files?.FirstOrDefault(f => f is { Length: > 0 });
            var list = await SaveUploadedFilesAsync(first, files, ocr);
            if (list == null || list.Count == 0)
                return HttpResults.Fail(400, ErrorCodes.ValidationError, "请至少上传一个资料文件（字段名 files）");
            return HttpResults.Created(new { count = list.Count, items = list });
        }
        catch (Exception ex)
        {
            return HttpResults.Fail(500, ErrorCodes.InternalError, $"批量保存上传失败：{ex.Message}",
                new { hint = "建议每批 ≤8 个文件或总大小 ≤400MB" });
        }
    }

    private async Task<List<MaterialDoc>?> SaveUploadedFilesAsync(IFormFile? single, List<IFormFile>? multi, string ocr)
    {
        var candidates = new List<IFormFile>();
        if (multi != null)
            candidates.AddRange(multi.Where(f => f is { Length: > 0 }));
        if (single is { Length: > 0 } && !candidates.Any(f => ReferenceEquals(f, single) || f.FileName == single.FileName))
        {
            if (candidates.Count == 0)
                candidates.Add(single);
        }

        // fallback: any form file
        if (candidates.Count == 0 && Request.HasFormContentType)
        {
            foreach (var f in Request.Form.Files)
            {
                if (f.Length > 0) candidates.Add(f);
            }
        }

        if (candidates.Count == 0)
            return null;

        if (ocr is not ("none" or "quick" or "standard"))
            ocr = "standard";

        var created = new List<MaterialDoc>();
        foreach (var file in candidates)
        {
            var id = Guid.NewGuid().ToString("N");
            // 显示标题保留用户原始文件名（仅剥离路径成分）；磁盘文件名另做安全净化。
            // 修复：原实现把净化后的名字同时用作显示标题，导致教材名中的 [ ] ( ) , + 等
            // 被替换为 _（如「…(明日科技)…」显示成「…_明日科技_…」），用户看到会误以为是缺陷。
            var displayName = Path.GetFileName((file.FileName ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(displayName)) displayName = "material.pdf";
            var safeName = SanitizeFileName(displayName);
            var dest = Path.Combine(MaterialsDir, $"{id}_{safeName}");
            Directory.CreateDirectory(MaterialsDir);
            await using (var fs = System.IO.File.Create(dest))
            {
                await file.CopyToAsync(fs);
            }
            try
            {
                await System.IO.File.WriteAllTextAsync(dest + ".meta.json",
                    System.Text.Json.JsonSerializer.Serialize(new { id, name = displayName, size = file.Length }));
            }
            catch { /* meta is best-effort */ }

            var doc = new MaterialDoc(id, displayName, dest, file.Length, "parsing", ocr, false, 0, 0, 0, 0,
                null, "batch-upload", DateTime.UtcNow, DateTime.UtcNow, null);
            MaterialRegistry.UpsertMaterial(doc);
            created.Add(doc);

            var captured = doc;
            _ = Task.Run(async () =>
            {
                try { await ParseAndRegisterAsync(captured); }
                catch (Exception ex)
                {
                    var old = MaterialRegistry.GetMaterial(captured.Id);
                    if (old != null)
                        MaterialRegistry.UpsertMaterial(old with { Status = "failed", Error = ex.Message, UpdatedAt = DateTime.UtcNow });
                }
            });
        }

        return created;
    }

    [HttpPost("/v1/materials/{id}/parse")]
    public async Task<IResult> Parse(string id, [FromQuery] string ocr = "standard")
    {
        var material = MaterialRegistry.GetMaterial(id);
        if (material == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "资料不存在");
        MaterialRegistry.UpsertMaterial(material with { Status = "parsing", UpdatedAt = DateTime.UtcNow });
        try
        {
            var doc = await ParseAndRegisterAsync(MaterialRegistry.GetMaterial(id)! with { OcrMode = ocr });
            return HttpResults.Success(doc);
        }
        catch (Exception ex)
        {
            var old = MaterialRegistry.GetMaterial(id);
            if (old != null)
                MaterialRegistry.UpsertMaterial(old with { Status = "failed", Error = ex.Message, UpdatedAt = DateTime.UtcNow });
            return HttpResults.Fail(503, ErrorCodes.InternalError, $"解析失败：{ex.Message}");
        }
    }

    private static async Task<MaterialDoc> ParseAndRegisterAsync(MaterialDoc material)
    {
        var payload = await MaterialPipeline.RunOcrAsync(material.Path, material.OcrMode ?? "standard");
        var doc = MaterialPipeline.ToDto(material.Id, material.Name, material.Path, material.SizeBytes, payload, material.OcrMode ?? "standard");
        var graph = MaterialPipeline.ToGraph(doc, payload);
        var tasks = MaterialTaskGenerator.GenerateFromGraph(graph, material.Name, 6);
        // 章节深度解析结果入库（侧边栏 + 按章出题）
        var chapterBundle = MaterialPipeline.ParseChapterBundle(material.Id, material.Name, payload);
        if (chapterBundle != null)
        {
            MaterialRegistry.UpsertChapters(material.Id, chapterBundle);
            var chapterTasks = MaterialPipeline.ChapterQuestionsToTasks(chapterBundle, 8);
            if (chapterTasks.Count > 0 && tasks.Count == 0)
                tasks = chapterTasks;
        }
        // 文件名清洗导致 C#→C_ 时的题库兜底
        if (tasks.Count == 0)
        {
            var alt = material.Name.Replace("_", "#");
            tasks = TextbookQuestionBank.ToTodayTasks(alt, 6);
            if (tasks.Count == 0 && material.Name.StartsWith("C_"))
                tasks = TextbookQuestionBank.ToTodayTasks(material.Name.Replace("C_", "C#"), 6);
        }
        MaterialRegistry.UpsertMaterial(doc);
        MaterialRegistry.UpsertGraph(graph, tasks);
        MaterialRegistry.MergeLatestTasks(material.Name, tasks);
        return doc;
    }

    [HttpGet("/v1/materials/{id}")]
    public IResult Get(string id)
    {
        var material = MaterialRegistry.GetMaterial(id);
        return material == null
            ? HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "资料不存在")
            : HttpResults.Success(material);
    }

    [HttpGet("/v1/materials/{id}/tasks")]
    public IResult MaterialTasks(string id, [FromQuery] int maxTasks = 6)
    {
        var material = MaterialRegistry.GetMaterial(id);
        if (material == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "资料不存在");
        var graphId = material.GraphId ?? $"auto-{id}";
        var graph = MaterialRegistry.GetGraph(graphId);
        var tasks = MaterialRegistry.GetTasksForGraph(graphId);
        if (tasks.Count == 0 && graph != null)
        {
            tasks = MaterialTaskGenerator.GenerateFromGraph(graph, material.Name, maxTasks);
            MaterialRegistry.UpsertGraph(graph, tasks);
        }
        tasks = tasks.Take(maxTasks).ToList();
        var brief = MaterialTaskGenerator.BuildDayBrief(tasks, material.Name);
        return HttpResults.Success(new { materialId = id, materialName = material.Name, brief, tasks });
    }

    [HttpPost("/v1/materials/{id}/generate-graph")]
    public IResult GenerateGraph(string id)
    {
        var material = MaterialRegistry.GetMaterial(id);
        if (material == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "资料不存在");
        var graphId = material.GraphId ?? $"auto-{id}";
        var graph = MaterialRegistry.GetGraph(graphId);
        if (graph == null)
        {
            var nodes = new[]
            {
                new AstralPath.Graph.KpNode("A001", Path.GetFileNameWithoutExtension(material.Name), "MATERIAL", "title")
            };
            graph = new AutoKnowledgeGraph(graphId, id, material.Name, 1, DateTime.UtcNow, nodes,
                Array.Empty<AstralPath.Graph.KpEdge>(), "filename-fallback");
            MaterialRegistry.UpsertGraph(graph);
        }
        var validate = MaterialPipeline.ValidateGenerated(graph);
        var tasks = MaterialRegistry.GetTasksForGraph(graph.GraphId);
        return HttpResults.Success(new
        {
            graph.GraphId,
            graph.MaterialId,
            graph.MaterialName,
            graph.GraphVersion,
            graph.GeneratedAt,
            graph.Source,
            graph.Nodes,
            graph.Edges,
            validate.Ok,
            validate.NodeCount,
            validate.EdgeCount,
            validate.Cycles,
            validate.Issues,
            tasks
        });
    }

    [HttpGet("/v1/knowledge-graphs")]
    public IResult ListGraphs()
    {
        var list = MaterialRegistry.ListGraphs()
            .Select(g => new
            {
                g.GraphId,
                g.MaterialId,
                g.MaterialName,
                g.GraphVersion,
                g.GeneratedAt,
                g.Source,
                nodeCount = g.Nodes.Count,
                edgeCount = g.Edges.Count,
                taskCount = MaterialRegistry.GetTasksForGraph(g.GraphId).Count
            })
            .ToList();
        return HttpResults.Success(list);
    }

    [HttpGet("/v1/knowledge-graphs/{graphId}")]
    public IResult GetGraph(string graphId)
    {
        var graph = MaterialRegistry.GetGraph(graphId);
        if (graph == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "知识图谱不存在");
        var validate = MaterialPipeline.ValidateGenerated(graph);
        var tasks = MaterialRegistry.GetTasksForGraph(graphId);
        var extras = MaterialPipeline.ExportGraphExtras(graph);
        return HttpResults.Success(new
        {
            graph.GraphId,
            graph.MaterialId,
            graph.MaterialName,
            graph.GraphVersion,
            graph.GeneratedAt,
            graph.Source,
            graph.Nodes,
            graph.Edges,
            validate.Ok,
            validate.NodeCount,
            validate.EdgeCount,
            validate.Cycles,
            validate.Issues,
            tasks,
            // KnowledgeGraph + mind-map 对齐导出
            triples = extras.Triples,
            propertyGraph = extras.PropertyGraph,
            mindmap = extras.Mindmap,
            markdownOutline = extras.MarkdownOutline,
            logicalLayout = extras.LogicalLayout,
        });
    }

    [HttpPost("/v1/knowledge-graphs/{graphId}/preview-debts")]
    public IResult PreviewDebts(string graphId, [FromQuery] string studentId = "demo-student-a")
    {
        var graph = MaterialRegistry.GetGraph(graphId);
        if (graph == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "知识图谱不存在");
        if (!_store.Students.TryGetValue(studentId, out var student))
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");

        var inputs = new List<(string, string, string, string, double, double, int, int, double)>();
        var rnd = new Random(studentId.GetHashCode());
        foreach (var edge in graph.Edges)
        {
            var fromNode = graph.Nodes.FirstOrDefault(n => n.Id == edge.From);
            var toNode = graph.Nodes.FirstOrDefault(n => n.Id == edge.To);
            var fromName = fromNode?.Name ?? edge.From;
            var toName = toNode?.Name ?? edge.To;
            // 过滤 OCR 噪声节点，避免「4 时预约」这类假债边
            if (IsNoisyNodeName(fromName) || IsNoisyNodeName(toName)) continue;
            if (fromName.Length < 2 || toName.Length < 2) continue;

            var fromFreq = ParseNodeFreq(fromNode?.Description);
            var toFreq = ParseNodeFreq(toNode?.Description);
            // 让多数边落在 BASELINE 触发区：score_p<40 ∧ score_c<50
            var scoreP = (double)Math.Clamp(22 + (fromFreq % 12) + rnd.Next(0, 6), 18, 48);
            var scoreC = (double)Math.Clamp(28 + (toFreq % 10) + rnd.Next(0, 8), 22, 49);
            var freq = Math.Clamp(2 + (fromFreq + toFreq) / 10 + rnd.Next(0, 3), 2, 8);
            inputs.Add((edge.From, edge.To, fromName, toName, Math.Round(scoreP, 1), Math.Round(scoreC, 1), freq, 0, edge.Weight));
        }

        var scanned = Core.Algorithms.DebtScanner.Scan(inputs, 8);
        return HttpResults.Success(new
        {
            studentId,
            graphId,
            materialName = graph.MaterialName,
            topDebts = scanned,
            tasks = MaterialRegistry.GetTasksForGraph(graphId)
        });
    }

    [HttpPost("/v1/materials/seed-samples")]
    public IResult SeedSamples([FromQuery] bool parse = false)
    {
        var samples = new[]
        {
            @"C:\Users\18948\Downloads\Kotlin编程实践：Kotlin从入门到实战.pdf",
            @"C:\Users\18948\Downloads\Python编程：从入门到实践（第3版）.pdf",
            @"C:\Users\18948\Downloads\C#从入门到精通（第7版）+(明日科技)+.pdf",
            @"C:\Users\18948\Downloads\Java从入门到精通（第6版） (明日科技) .pdf",
            @"C:\Users\18948\Downloads\Go语言从入门到精通.pdf",
            @"C:\Users\18948\Downloads\大模型应用开发：动手做 AI Agent (黄佳) .pdf",
            @"C:\Users\18948\Downloads\深度学习入门：基于Python的理论与实现+(斋藤康毅)+.pdf",
            @"C:\Users\18948\Downloads\深度学习进阶：自然语言处理 (斋藤康毅) .pdf",
            @"C:\Users\18948\Downloads\深度学习入门2：自制框架 (斋藤康毅)-扫描版 (1).PDF",
            @"C:\Users\18948\Downloads\图灵程序设计丛书--深度学习入门4：强化学习 ([日] 斋藤康毅) (1).pdf",
            @"C:\Users\18948\Downloads\DeepLearning-Goodfellow-花书.pdf",
            @"C:\Users\18948\Downloads\深度学习 Deep Learning [花书] (Ian Goodfellow,Yoshua Bengio,Aaron Courville) .pdf",
            @"C:\Users\18948\Downloads\黄仁勋：英伟达之芯_【美】斯蒂芬·威特.pdf",
        };

        var created = new List<string>();
        foreach (var src in samples)
        {
            if (!System.IO.File.Exists(src)) continue;
            var name = Path.GetFileName(src);
            if (MaterialRegistry.ListMaterials().Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var id = Guid.NewGuid().ToString("N");
            var dest = Path.Combine(MaterialsDir, $"{id}_{SanitizeFileName(name)}");
            try { System.IO.File.Copy(src, dest, true); } catch { continue; }
            var doc = new MaterialDoc(id, name, dest, new FileInfo(dest).Length, "uploaded",
                "standard", false, 0, 0, 0, 0, null, "seeded", DateTime.UtcNow, DateTime.UtcNow, null);
            MaterialRegistry.UpsertMaterial(doc);
            created.Add(id);
        }

        if (parse && created.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                foreach (var id in created)
                {
                    var m = MaterialRegistry.GetMaterial(id);
                    if (m == null) continue;
                    try { await ParseAndRegisterAsync(m); }
                    catch { /* keep list */ }
                }
            });
        }

        return HttpResults.Success(new { seeded = created.Count, ids = created, parseStarted = parse && created.Count > 0 });
    }

    /// <summary>一键解析全部已上传资料并生成今日任务（用于根据示例教材出题）。</summary>
    [HttpPost("/v1/materials/parse-all")]
    public IResult ParseAll([FromQuery] string ocr = "quick")
    {
        MaterialRegistry.HydrateFromDirectory(MaterialsDir);
        var pending = MaterialRegistry.ListMaterials()
            .Where(m => (m.Status is "uploaded" or "failed" or "parsing") && m.NodeCount == 0)
            .Select(m => m.Id)
            .ToList();
        if (pending.Count == 0)
        {
            // also re-parse ready materials if they have no tasks
            pending = MaterialRegistry.ListMaterials()
                .Where(m => MaterialRegistry.GetTasksForGraph(m.GraphId ?? $"auto-{m.Id}").Count == 0)
                .Select(m => m.Id)
                .ToList();
        }

        _ = Task.Run(async () =>
        {
            foreach (var id in pending)
            {
                var m = MaterialRegistry.GetMaterial(id);
                if (m == null) continue;
                try
                {
                    await ParseAndRegisterAsync(m with { OcrMode = ocr });
                }
                catch
                {
                    var old = MaterialRegistry.GetMaterial(id);
                    if (old != null)
                        MaterialRegistry.UpsertMaterial(old with { Status = "failed", UpdatedAt = DateTime.UtcNow });
                }
            }
        });

        return HttpResults.Success(new { queued = pending.Count, ids = pending });
    }

    private static int _todayBookCursor;

    [HttpGet("/v1/materials/today-from-books")]
    public IResult TodayFromBooks([FromQuery] int maxTasks = 8, [FromQuery] bool rotate = false)
    {
        // 1) 教材真题题库：rotate 时跨书轮换，而不是永远第一本
        var banked = MaterialRegistry.ListMaterials()
            .Where(m => TextbookQuestionBank.MatchBankKey(m.Name) != null)
            .ToList();
        if (banked.Count > 0)
        {
            var idx = 0;
            if (rotate)
            {
                idx = Math.Abs(Interlocked.Increment(ref _todayBookCursor) - 1) % banked.Count;
            }
            else
            {
                idx = Math.Abs(_todayBookCursor) % banked.Count;
            }
            var m = banked[idx];
            var bank = TextbookQuestionBank.MatchBankKey(m.Name);
            var bankTasks = TextbookQuestionBank.ToTodayTasks(m.Name, maxTasks, rotate);
            if (bankTasks.Count > 0)
            {
                MaterialRegistry.MergeLatestTasks(m.Name, bankTasks);
                var bankBrief = MaterialTaskGenerator.BuildDayBrief(bankTasks, m.Name);
                var stats = TextbookQuestionBank.BankStats(m.Name);
                return HttpResults.Success(new
                {
                    materialName = m.Name,
                    materialId = m.Id,
                    brief = bankBrief,
                    tasks = bankTasks,
                    source = "textbook-question-bank",
                    bank,
                    bankStats = stats,
                    rotated = rotate,
                    bookIndex = idx,
                    bookCount = banked.Count,
                    materialNameList = banked.Select(x => x.Name).ToList()
                });
            }
        }

        var (materialName, tasks) = MaterialRegistry.GetLatestTasks();
        if (tasks.Count == 0)
        {
            foreach (var g in MaterialRegistry.ListGraphs())
            {
                var gen = MaterialTaskGenerator.GenerateFromGraph(g, g.MaterialName, maxTasks);
                if (gen.Count > 0)
                {
                    MaterialRegistry.UpsertGraph(g, gen);
                    materialName = g.MaterialName;
                    tasks = gen;
                    break;
                }
            }
        }
        tasks = tasks.Take(maxTasks).ToList();
        var brief = MaterialTaskGenerator.BuildDayBrief(tasks, materialName);
        return HttpResults.Success(new { materialName, brief, tasks, source = "sample-textbooks" });
    }

    [HttpGet("/v1/question-banks")]
    public IResult ListQuestionBanks() => HttpResults.Success(TextbookQuestionBank.ListBanks());

    [HttpGet("/v1/materials/{id}/textbook-questions")]
    public IResult TextbookQuestions(string id, [FromQuery] int maxTasks = 8, [FromQuery] bool rotate = false, [FromQuery] int? seed = null)
    {
        var material = MaterialRegistry.GetMaterial(id);
        if (material == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "资料不存在");
        var tasks = TextbookQuestionBank.ToTodayTasks(material.Name, maxTasks, rotate, seed);
        var bank = TextbookQuestionBank.MatchBankKey(material.Name);
        var stats = TextbookQuestionBank.BankStats(material.Name);
        var brief = MaterialTaskGenerator.BuildDayBrief(tasks, material.Name);
        return HttpResults.Success(new
        {
            materialName = material.Name,
            bank,
            bankStats = stats,
            count = tasks.Count,
            rotated = rotate,
            brief,
            tasks,
            note = tasks.Count == 0
                ? "该资料暂无匹配的教材真题题库"
                : (rotate ? "本批为轮换题目" : "题目来自教材正文知识点；可 rotate=true 换一批")
        });
    }

    private static int ParseNodeFreq(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(description, @"freq=(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static bool IsNoisyNodeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var s = name.Trim();
        if (s.Length < 2) return true;
        // 节点 id 残片 / guid / hex
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9a-fA-F]{16,}$")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9a-f]{8}-[0-9a-f]{4}-")) return true;
        // OCR 垃圾：大量点号、纯符号、出版社/CIP 残片
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[\.\…·]{3,}")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\d\s\.\、，,。①②③ⅠⅡⅢ]+$")) return true;
        if (s.Contains("出版社") || s.Contains("印刷") || s.Contains("ISBN") || s.Contains("责任编辑")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[\d\s]+\s*[时著页版印次]$")) return true;
        // 「4 时预约」「9 著 黄 佳」类
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+\s+\S{0,4}(著|时|页|印|版)")) return true;
        // 纯噪声英文（新闻页脚）
        var low = s.ToLowerInvariant();
        if (low is "posts" or "telecom" or "press" or "openai" or "copyright" or "all rights")
            return true;
        return false;
    }

    [HttpGet("/v1/materials/{id}/chapters")]
    public IResult MaterialChapters(string id)
    {
        var material = MaterialRegistry.GetMaterial(id);
        if (material == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "资料不存在");
        var bundle = MaterialRegistry.GetChapters(id);
        if (bundle == null)
            return HttpResults.Success(new
            {
                materialId = id,
                materialName = material.Name,
                chapters = Array.Empty<object>(),
                sections = Array.Empty<object>(),
                stats = new { chapterCount = 0, sectionCount = 0, questionCount = 0 },
                note = "尚未深度解析，请先在藏书阁解析该资料"
            });
        return HttpResults.Success(new
        {
            materialId = id,
            materialName = bundle.MaterialName,
            chapters = bundle.Chapters.Select(c => new
            {
                c.Id, c.Title, c.Kind, c.Level, c.ParentId, c.ChapterNum, c.CharCount,
                questionCount = c.Questions.Count,
                preview = c.Content.Length > 80 ? c.Content[..80] + "…" : c.Content
            }),
            sections = bundle.Sections.Select(s => new
            {
                s.Id, s.Title, s.Kind, s.Level, s.ParentId, s.ChapterNum, s.CharCount,
                questionCount = s.Questions.Count,
                preview = s.Content.Length > 60 ? s.Content[..60] + "…" : s.Content
            }),
            bundle.Stats,
            toc = bundle.Toc
        });
    }

    [HttpGet("/v1/materials/{id}/chapters/{chapterId}")]
    public IResult MaterialChapterDetail(string id, string chapterId)
    {
        var bundle = MaterialRegistry.GetChapters(id);
        if (bundle == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "章节数据不存在，请先深度解析");
        var item = bundle.Chapters.FirstOrDefault(c => string.Equals(c.Id, chapterId, StringComparison.OrdinalIgnoreCase))
                   ?? bundle.Sections.FirstOrDefault(c => string.Equals(c.Id, chapterId, StringComparison.OrdinalIgnoreCase));
        if (item == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "章节不存在");
        return HttpResults.Success(new
        {
            materialId = id,
            materialName = bundle.MaterialName,
            item.Id, item.Title, item.Kind, item.Level, item.ParentId, item.ChapterNum,
            item.CharCount, item.Content,
            questions = item.Questions
        });
    }

    [HttpGet("/v1/materials/{id}/chapters/{chapterId}/questions")]
    public IResult MaterialChapterQuestions(string id, string chapterId)
    {
        var bundle = MaterialRegistry.GetChapters(id);
        if (bundle == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "章节数据不存在，请先深度解析");
        var item = bundle.Chapters.FirstOrDefault(c => string.Equals(c.Id, chapterId, StringComparison.OrdinalIgnoreCase))
                   ?? bundle.Sections.FirstOrDefault(c => string.Equals(c.Id, chapterId, StringComparison.OrdinalIgnoreCase));
        if (item == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "章节不存在");
        var tasks = item.Questions.Select(q => new TodayTaskDto(
            Guid.NewGuid().ToString("N"),
            $"chapter:{item.Id}",
            string.IsNullOrWhiteSpace(q.Kp) ? item.Title : q.Kp,
            q.Type, q.Difficulty, q.EstMin,
            $"来自《{bundle.MaterialName}》「{item.Title}」：{q.Why}",
            q.Id, q.Stem, q.Options, q.CorrectIndex, null)).ToList();
        return HttpResults.Success(new
        {
            materialId = id,
            materialName = bundle.MaterialName,
            chapterId = item.Id,
            chapterTitle = item.Title,
            count = tasks.Count,
            questions = item.Questions,
            tasks
        });
    }

    [HttpGet("/v1/materials/{id}/chapter-questions")]
    public IResult AllChapterQuestions(string id, [FromQuery] int maxTasks = 12)
    {
        var bundle = MaterialRegistry.GetChapters(id);
        if (bundle == null)
            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "章节数据不存在，请先深度解析");
        var tasks = MaterialPipeline.ChapterQuestionsToTasks(bundle, maxTasks);
        var brief = MaterialTaskGenerator.BuildDayBrief(tasks, bundle.MaterialName);
        return HttpResults.Success(new
        {
            materialId = id,
            materialName = bundle.MaterialName,
            brief,
            tasks,
            chapterCount = bundle.Chapters.Count,
            sectionCount = bundle.Sections.Count
        });
    }

    private static string SanitizeFileName(string? rawName)
    {
        var name = Path.GetFileName(rawName ?? "material.pdf");
        if (string.IsNullOrWhiteSpace(name)) name = "material.pdf";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => ch switch
        {
            '#' or '%' or '+' or '?' or '&' or '*' or '"' or '<' or '>' or '|' or ':'
                or '[' or ']' or '(' or ')' or '{' or '}' or ',' => '_',
            _ when invalid.Contains(ch) => '_',
            _ => ch
        }).ToArray();
        var cleaned = new string(chars).Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "material.pdf";
        // Windows MAX_PATH：目录前缀较长时中文文件名易超 260，截断主体保留扩展名
        var ext = Path.GetExtension(cleaned);
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (stem.Length > 80) stem = stem[..80];
        cleaned = stem + ext;
        return cleaned;
    }
}
