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
    public IResult ListMaterials() => HttpResults.Success(MaterialRegistry.ListMaterials());

    [HttpPost("/v1/materials/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(512L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 512L * 1024 * 1024)]
    public async Task<IResult> Upload([FromForm(Name = "file")] IFormFile? file, [FromQuery] string ocr = "standard")
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

    /// <summary>一次上传多个文件并排队解析。</summary>
    [HttpPost("/v1/materials/upload-batch")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(512L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 512L * 1024 * 1024)]
    public async Task<IResult> UploadBatch([FromForm(Name = "files")] List<IFormFile>? files, [FromQuery] string ocr = "standard")
    {
        var first = files?.FirstOrDefault(f => f is { Length: > 0 });
        var list = await SaveUploadedFilesAsync(first, files, ocr);
        if (list == null || list.Count == 0)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "请至少上传一个资料文件（字段名 files）");
        return HttpResults.Created(new { count = list.Count, items = list });
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
            var safeName = SanitizeFileName(file.FileName);
            var dest = Path.Combine(MaterialsDir, $"{id}_{safeName}");
            Directory.CreateDirectory(MaterialsDir);
            await using (var fs = System.IO.File.Create(dest))
            {
                await file.CopyToAsync(fs);
            }

            var doc = new MaterialDoc(id, safeName, dest, file.Length, "parsing", ocr, false, 0, 0, 0, 0,
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
            var fromName = graph.Nodes.FirstOrDefault(n => n.Id == edge.From)?.Name ?? edge.From;
            var toName = graph.Nodes.FirstOrDefault(n => n.Id == edge.To)?.Name ?? edge.To;
            var scoreP = 28 + rnd.Next(0, 15);
            var scoreC = 38 + rnd.Next(0, 12);
            var freq = rnd.Next(2, 7);
            inputs.Add((edge.From, edge.To, fromName, toName, scoreP, scoreC, freq, 0, edge.Weight));
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
            @"C:\Users\18948\Downloads\Kotlin编程实践：Kotlin从入门到实战\Kotlin编程实践：Kotlin从入门到实战.pdf",
            @"C:\Users\18948\Downloads\Python编程从入门到实践（第3版）(1)\Python编程：从入门到实践（第3版）.pdf",
            @"C:\Users\18948\Downloads\C#从入门到精通（第7版）+(明日科技)+.pdf",
            @"C:\Users\18948\Downloads\Java从入门到精通（第6版） (明日科技) .pdf",
            @"C:\Users\18948\Downloads\Go语言从入门到精通.pdf",
            @"C:\Users\18948\Downloads\大模型应用开发：动手做 AI Agent (黄佳) .pdf",
            @"C:\Users\18948\Downloads\深度学习入门：基于Python的理论与实现+(斋藤康毅)+.pdf",
            @"C:\Users\18948\Downloads\深度学习进阶：自然语言处理(1)\深度学习进阶：自然语言处理 (斋藤康毅) .pdf",
            @"C:\Users\18948\Downloads\深度学习入门2：自制框架 (斋藤康毅)-扫描版 (1).PDF",
            @"C:\Users\18948\Downloads\图灵程序设计丛书--深度学习入门4：强化学习 ([日] 斋藤康毅) (1).pdf",
            @"C:\Users\18948\Downloads\深度学习 Deep Learning [花书]\深度学习 Deep Learning [花书] (Ian Goodfellow,Yoshua Bengio,Aaron Courville) .pdf",
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
        var pending = MaterialRegistry.ListMaterials()
            .Where(m => m.Status is "uploaded" or "failed" or "parsing" && m.NodeCount == 0)
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

    [HttpGet("/v1/materials/today-from-books")]
    public IResult TodayFromBooks([FromQuery] int maxTasks = 8, [FromQuery] bool rotate = false)
    {
        // 1) 优先：教材真题题库（避免误匹配：按材料名匹配题库键）
        foreach (var m in MaterialRegistry.ListMaterials())
        {
            var bank = TextbookQuestionBank.MatchBankKey(m.Name);
            if (bank == null) continue;
            var bankTasks = TextbookQuestionBank.ToTodayTasks(m.Name, maxTasks, rotate);
            if (bankTasks.Count == 0) continue;
            MaterialRegistry.MergeLatestTasks(m.Name, bankTasks);
            var bankBrief = MaterialTaskGenerator.BuildDayBrief(bankTasks, m.Name);
            var stats = TextbookQuestionBank.BankStats(m.Name);
            return HttpResults.Success(new
            {
                materialName = m.Name,
                brief = bankBrief,
                tasks = bankTasks,
                source = "textbook-question-bank",
                bank,
                bankStats = stats,
                rotated = rotate
            });
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

    private static string SanitizeFileName(string? rawName)
    {
        var name = Path.GetFileName(rawName ?? "material.pdf");
        if (string.IsNullOrWhiteSpace(name)) name = "material.pdf";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => ch switch
        {
            '#' or '%' or '+' or '?' or '&' or '*' or '"' or '<' or '>' or '|' or ':' => '_',
            _ when invalid.Contains(ch) => '_',
            _ => ch
        }).ToArray();
        var cleaned = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "material.pdf" : cleaned;
    }
}
