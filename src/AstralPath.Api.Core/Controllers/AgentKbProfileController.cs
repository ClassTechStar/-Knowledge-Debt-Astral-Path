using Microsoft.AspNetCore.Mvc;
using AstralPath.Contracts;
using AstralPath.Core.Agent;
using AstralPath.Core.Graph;
using AstralPath.Infrastructure;

namespace AstralPath.Api.Controllers;

/// <summary>
/// §44 受约束智能体 / §45 知识库 / §46 用户画像 的对外接口。
/// 路由与请求响应类型对齐《技术方案》§4.0 接口目录。
///
/// 演示版实现范围（诚实标注，避免"看似完成"）：
///   · 已实现：agent turns/sessions/tools/goldens；kb documents(CRUD/tags/versions/publish/rollback/archive)、
///             kb tags/search；profile overview/features/tags/radar/timeline/opt-out；modules/status；graph csr。
///   · 延后：kb 分片直传（uploads / commit）、kb 片段读取（chunks）、internal 导入 —— 依赖对象存储与
///           跨服务鉴权，演示版显式返回 501 NOT_IMPLEMENTED 并登记为待办。
/// </summary>
[ApiController]
public sealed class AgentKbProfileController : ControllerBase
{
    private const string DeferredReason = "该能力依赖对象存储/跨服务鉴权，演示版未实现；已登记为待办（方案 §48.7 / 分工方案 §8.5）。";

    private readonly AstralPathStore _store;
    private readonly AstralPathModules _modules;

    public AgentKbProfileController(AstralPathStore store, AstralPathModules modules)
    {
        _store = store;
        _modules = modules;
    }

    private static bool IsValidRole(string? role) =>
        role is AgentRoles.Student or AgentRoles.Teacher or AgentRoles.Admin or AgentRoles.Demo;

    /// <summary>从运行目录向上定位仓库根（含 AstralPath.slnx）。</summary>
    private static string ProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.File.Exists(Path.Combine(dir.FullName, "AstralPath.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    // ═══════════════════════════ §44 智能体 ═══════════════════════════════

    [HttpPost("/v1/agent/turns")]
    public IResult Turn([FromBody] AgentTurnRequest? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Utterance))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "utterance 不能为空");
        if (string.IsNullOrWhiteSpace(body.UserId))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "userId 必填");
        if (!IsValidRole(body.Role))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, $"role 无效：{body.Role}");
        // P3-4：长度上限（原实现 12MB 的 utterance 也会被收下并写入会话日志）
        var tooLong = RequestLimits.FirstViolation(
            ("userId", body.UserId, RequestLimits.Id),
            ("utterance", body.Utterance, RequestLimits.Body),
            ("sessionId", body.SessionId, RequestLimits.ShortText));
        if (tooLong is not null)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, tooLong);

        var sessionId = string.IsNullOrWhiteSpace(body.SessionId) ? $"sess-{Guid.NewGuid():N}" : body.SessionId!;
        return _store.Lock(() => HttpResults.Success(_modules.AgentTurn(sessionId, body.UserId, body.Role, body.Utterance, _store)));
    }

    [HttpGet("/v1/agent/sessions/{sessionId}")]
    public IResult Session(string sessionId)
    {
        var state = _modules.GetAgentSession(sessionId);
        return state is null
            ? HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "会话不存在（可能已过期或已结束）")
            : HttpResults.Success(state);
    }

    [HttpDelete("/v1/agent/sessions/{sessionId}")]
    public IResult EndSession(string sessionId) =>
        _modules.EndAgentSession(sessionId) ? Results.NoContent() : HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "会话不存在");

    [HttpGet("/v1/agent/tools")]
    public IResult Tools([FromQuery] string? role)
    {
        if (!IsValidRole(role))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "role 必填且须为 student|teacher|admin|demo");
        return HttpResults.Success(_modules.AgentTools(role!));
    }

    /// <summary>读取智能体金样（内部 / CI 使用，§44.6.2 金样文件结构）。</summary>
    [HttpGet("/v1/agent/goldens/{goldenId}")]
    public IResult Golden(string goldenId)
    {
        if (string.IsNullOrWhiteSpace(goldenId) || goldenId.Contains("..")
            || goldenId.Contains('/') || goldenId.Contains('\\'))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "goldenId 非法");

        var root = ProjectRoot();
        foreach (var sub in new[] { Path.Combine("contracts", "golden"), Path.Combine("eval", "golden") })
        {
            var dir = Path.Combine(root, sub);
            if (!System.IO.Directory.Exists(dir)) continue;
            var hit = System.IO.Directory.EnumerateFiles(dir, "*.json", System.IO.SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(goldenId, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return HttpResults.Success(new
                {
                    goldenId,
                    path = Path.GetRelativePath(root, hit).Replace('\\', '/'),
                    content = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(hit)).RootElement
                });
            }
        }
        return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, $"金样不存在：{goldenId}");
    }

    // ═══════════════════════════ §45 知识库 ═══════════════════════════════

    [HttpPost("/v1/kb/documents")]
    public IResult CreateDocument([FromBody] KbCreateDocument? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Title))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "title 不能为空");
        if (string.IsNullOrWhiteSpace(body.OwnerUserId))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "ownerUserId 必填");
        if (body.Visibility is not null && body.Visibility is not ("private" or "consented" or "course" or "public"))
            return HttpResults.Fail(422, ErrorCodes.ValidationError, "visibility 须为 private|consented|course|public");
        // P3-4：长度上限
        var titleTooLong = RequestLimits.FirstViolation(
            ("title", body.Title, RequestLimits.Title),
            ("ownerUserId", body.OwnerUserId, RequestLimits.Id),
            ("courseCode", body.CourseCode, RequestLimits.ShortText),
            ("text", body.Text, RequestLimits.Body));
        if (titleTooLong is not null)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, titleTooLong);

        return _store.Lock(() =>
        {
            var doc = _modules.IngestKb(body.Title, body.OwnerUserId, body.Visibility ?? "private",
                body.CourseCode ?? "", body.Text ?? "", body.Tags);
            return HttpResults.Created(doc);
        });
    }

    [HttpGet("/v1/kb/documents/{id}")]
    public IResult GetDocument(string id, [FromQuery] string? userId, [FromQuery] string? role)
    {
        var (u, r) = Actor(userId, role);
        var doc = _modules.GetKbDocument(_store, id, u, r);
        return doc is null
            ? HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "文档不存在或无可见性")
            : HttpResults.Success(doc);
    }

    [HttpPatch("/v1/kb/documents/{id}")]
    public IResult PatchDocument(string id, [FromBody] KbPatchDocument? body,
        [FromQuery] string? userId, [FromQuery] string? role)
    {
        if (body is null) return HttpResults.Fail(400, ErrorCodes.ValidationError, "请求体不能为空");
        var (u, r) = Actor(userId, role);
        return MapKb(_modules.PatchKbDocument(_store, id, u, r, body.Title, body.Visibility, body.CourseCode, body.Tags));
    }

    [HttpPut("/v1/kb/documents/{id}/tags")]
    public IResult SetTags(string id, [FromBody] KbSetTags? body,
        [FromQuery] string? userId, [FromQuery] string? role)
    {
        if (body?.Tags is null) return HttpResults.Fail(400, ErrorCodes.ValidationError, "tags 不能为空");
        var (u, r) = Actor(userId, role);
        return MapKb(_modules.SetKbTags(_store, id, u, r, body.Tags));
    }

    [HttpPost("/v1/kb/documents/{id}/versions")]
    public IResult CreateVersion(string id, [FromBody] KbCreateVersion? body)
    {
        var result = _modules.CreateKbVersion(id, body?.ActorId ?? "demo-student-a", AgentRoles.Student,
            body?.Text ?? "", body?.Note);
        if (result is null) return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "文档不存在");
        if (result is "forbidden") return HttpResults.Fail(403, ErrorCodes.Forbidden, "仅文档所有者可提交版本");
        if (result is "archived") return HttpResults.Fail(409, ErrorCodes.StateConflict, "文档已归档，不可提交版本");
        return HttpResults.Created(result);
    }

    [HttpGet("/v1/kb/documents/{id}/versions")]
    public IResult ListVersions(string id, [FromQuery] string? userId, [FromQuery] string? role)
    {
        var (u, r) = Actor(userId, role);
        var result = _modules.ListKbVersions(id, u, r, _store);
        if (result is null) return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "文档不存在");
        if (result is "forbidden") return HttpResults.Fail(403, ErrorCodes.Forbidden, "无可见性");
        return HttpResults.Success(result);
    }

    [HttpPost("/v1/kb/documents/{id}/publish")]
    public IResult Publish(string id, [FromBody] KbPublishRequest? body)
    {
        var result = _modules.PublishKb(id, body?.ActorId ?? "demo-student-a", AgentRoles.Student, body?.Version);
        return result switch
        {
            null => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "文档不存在"),
            "forbidden" => HttpResults.Fail(403, ErrorCodes.Forbidden, "仅文档所有者可发布"),
            "archived" => HttpResults.Fail(409, ErrorCodes.StateConflict, "文档已归档，不可发布"),
            "no_version" => HttpResults.Fail(409, ErrorCodes.StateConflict, "尚无版本，无法发布"),
            "version_not_found" => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "指定版本不存在"),
            _ => HttpResults.Success(result)
        };
    }

    [HttpPost("/v1/kb/documents/{id}/rollback")]
    public IResult Rollback(string id, [FromBody] KbRollbackRequest? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Version))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "version 必填");
        var result = _modules.RollbackKb(id, body.ActorId ?? "demo-student-a", AgentRoles.Student, body.Version);
        return result switch
        {
            null => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "文档不存在"),
            "forbidden" => HttpResults.Fail(403, ErrorCodes.Forbidden, "仅文档所有者可回滚"),
            "version_not_found" => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "指定版本不存在"),
            _ => HttpResults.Success(result)
        };
    }

    [HttpPost("/v1/kb/documents/{id}/archive")]
    public IResult Archive(string id, [FromQuery] string? userId, [FromQuery] string? role)
    {
        var (u, r) = Actor(userId, role);
        var result = _modules.ArchiveKb(id, u, r);
        if (result is null) return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "文档不存在");
        if (result is "forbidden") return HttpResults.Fail(403, ErrorCodes.Forbidden, "仅文档所有者可归档");
        return HttpResults.Success(result);
    }

    [HttpGet("/v1/kb/tags")]
    public IResult TagTree([FromQuery] string? userId, [FromQuery] string? role)
    {
        var (u, r) = Actor(userId, role);
        return _store.Lock(() => HttpResults.Success(_modules.KbTagTree(u, r, _store)));
    }

    /// <summary>知识库混合检索（§45.6.7）。可见性前置过滤，越权文档不进入召回集。</summary>
    [HttpPost("/v1/kb/search")]
    public IResult Search([FromBody] KbSearchRequest? body)
    {
        if (body is null) return HttpResults.Fail(400, ErrorCodes.ValidationError, "请求体不能为空");
        var (u, r) = Actor(body.UserId, body.Role);
        return HttpResults.Success(_modules.SearchKb(_store, u, r, body.Query ?? ""));
    }

    // ── 分片直传 / 证据片段 / 内部导入（原 501 延后项，已补齐）─────────────

    /// <summary>申请知识库分片直传票据。body：{"title":"…","ownerUserId":"…","visibility":"private","courseCode":"LINALG","partCount":3}</summary>
    [HttpPost("/v1/kb/uploads")]
    public IResult UploadTicket([FromBody] KbUploadTicketRequest? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.OwnerUserId))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "title 与 ownerUserId 必填");
        try
        {
            var ticket = _modules.CreateUploadTicket(body.Title, body.OwnerUserId,
                body.Visibility ?? "private", body.CourseCode ?? "", body.PartCount);
            return HttpResults.Created(ticket);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return HttpResults.Fail(413, ErrorCodes.ValidationError, ex.Message);
        }
    }

    /// <summary>分片合并提交。body：{"ownerUserId":"…","parts":["第一段","第二段"]}</summary>
    [HttpPost("/v1/kb/uploads/{uploadId}/commit")]
    public IResult UploadCommit(string uploadId, [FromBody] KbUploadCommitRequest? body)
    {
        if (body?.Parts is null || body.Parts.Count == 0)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "parts 不能为空");
        var result = _modules.CommitUpload(uploadId, body.OwnerUserId ?? "demo-student-a",
            body.Role ?? AgentRoles.Student, body.Parts);
        return result switch
        {
            null => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "上传票据不存在或已过期"),
            "forbidden" => HttpResults.Fail(403, ErrorCodes.Forbidden, "仅所有者可提交分片"),
            "part_count_mismatch" => HttpResults.Fail(409, ErrorCodes.StateConflict, "分片数与票据登记不符"),
            _ => HttpResults.Success(result)
        };
    }

    /// <summary>读取证据片段（含上下文）。Query：context（0–3，默认 1）</summary>
    [HttpGet("/v1/kb/chunks/{chunkId}")]
    public IResult Chunk(string chunkId, [FromQuery] string? docId, [FromQuery] string? userId,
        [FromQuery] string? role, [FromQuery] int context = 1)
    {
        if (string.IsNullOrWhiteSpace(docId))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "docId 必填（片段需归属文档）");
        var (u, r) = Actor(userId, role);
        var result = _modules.GetChunk(docId, chunkId, u, r, context);
        return result switch
        {
            null => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "文档不存在"),
            // 与文档读取保持一致：越权一律 404，避免泄漏文档是否存在（§45.9）
            "forbidden" => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "文档不存在或无可见性"),
            "chunk_not_found" => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "片段不存在"),
            _ => HttpResults.Success(result)
        };
    }

    /// <summary>知识库系统导入（INTERNAL）。body：{"ownerUserId":"…","items":[{"title":"…","visibility":"public","courseCode":"LINALG","text":"…"}]}</summary>
    [HttpPost("/internal/v1/kb/import")]
    public IResult InternalImport([FromBody] KbImportRequest? body)
    {
        if (body is null || body.Items is null || body.Items.Count == 0)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "items 不能为空");
        var items = body.Items.Select(i => (i.Title ?? "", i.Visibility ?? "private", i.CourseCode ?? "", i.Text ?? "")).ToList();
        return _store.Lock(() => HttpResults.Created(_modules.InternalImport(body.OwnerUserId ?? "demo-student-a", items)));
    }

    // ═══════════════════════════ §46 用户画像 ═════════════════════════════

    [HttpGet("/v1/profile/{studentId}")]
    public IResult Profile(string studentId, [FromQuery] bool teacherSide = false)
    {
        return _store.Lock(() =>
        {
            var result = _modules.ProfileOverview(_store, studentId, teacherSide);
            return result is null ? HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在") : HttpResults.Success(result);
        });
    }

    [HttpGet("/v1/profile/{studentId}/features")]
    public IResult Features(string studentId, [FromQuery] bool teacherSide = false)
    {
        return _store.Lock(() =>
        {
            var result = _modules.ProfileFeatures(_store, studentId, teacherSide);
            return result is null ? HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在") : HttpResults.Success(result);
        });
    }

    [HttpGet("/v1/profile/{studentId}/tags")]
    public IResult Tags(string studentId, [FromQuery] bool teacherSide = false)
    {
        return _store.Lock(() =>
            _store.Students.ContainsKey(studentId)
                ? HttpResults.Success(_modules.GetProfile(studentId, teacherSide))
                : HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在"));
    }

    [HttpGet("/v1/profile/{studentId}/radar")]
    public IResult Radar(string studentId, [FromQuery] bool teacherSide = false)
    {
        return _store.Lock(() =>
        {
            var result = _modules.ProfileRadar(_store, studentId, teacherSide);
            return result is null ? HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在") : HttpResults.Success(result);
        });
    }

    [HttpGet("/v1/profile/{studentId}/timeline")]
    public IResult Timeline(string studentId, [FromQuery] bool teacherSide = false)
    {
        return _store.Lock(() =>
        {
            var result = _modules.ProfileTimeline(_store, studentId, teacherSide);
            return result is null ? HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在") : HttpResults.Success(result);
        });
    }

    /// <summary>写入画像标签（§46.3）。禁列语义域（sensitive/crisis/semantic）一律拒绝。</summary>
    [HttpPost("/v1/profile/{studentId}/tags")]
    public IResult UpsertTag(string studentId, [FromBody] ProfileTagUpsert? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Tag) || string.IsNullOrWhiteSpace(body.Domain))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "tag 与 domain 必填");
        return _store.Lock(() =>
        {
            if (!_store.Students.ContainsKey(studentId))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");
            try
            {
                return HttpResults.Success(_modules.UpsertProfile(studentId, body.Tag, body.Domain, body.Weight, body.OptOut));
            }
            catch (InvalidOperationException ex)
            {
                return HttpResults.Fail(422, ErrorCodes.ValidationError, ex.Message);
            }
        });
    }

    /// <summary>画像 opt-out（§46.7）：关闭个性化标签展示，不影响学业事实。</summary>
    [HttpPost("/v1/profile/{studentId}/opt-out")]
    public IResult OptOut(string studentId)
    {
        return _store.Lock(() =>
            _store.Students.ContainsKey(studentId)
                ? HttpResults.Success(_modules.OptOutProfile(studentId))
                : HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在"));
    }

    // ═══════════════════════════ 模块自检与图索引 ═════════════════════════

    [HttpGet("/v1/modules/status")]
    public IResult ModuleStatus() => _store.Lock(() => HttpResults.Success(_modules.ModuleStatus(_store)));

    /// <summary>知识图谱 CSR 索引统计（§48.1：紧凑邻接 + 类型位图）。支持课程包与教材自动图。</summary>
    [HttpGet("/v1/knowledge-graphs/{id}/csr")]
    public IResult Csr(string id)
    {
        return _store.Lock(() =>
        {
            // 1) 课程包
            if (string.Equals(id, _store.PackId, StringComparison.OrdinalIgnoreCase))
            {
                var csrPack = CsrGraph.Build(
                    _store.Graph.Edges.Select(e => (e.From, e.To, e.EdgeType)),
                    _store.Graph.Nodes.Select(n => n.Id));
                return HttpResults.Success(new
                {
                    graphId = id,
                    source = "course-pack",
                    nodeCount = csrPack.NodeCount,
                    offsets = csrPack.Offsets.Count,
                    edges = csrPack.AdjDst.Count,
                    edgeTypes = csrPack.TypeNames
                });
            }

            // 2) 教材自动图（识网）
            var auto = MaterialRegistry.GetGraph(id);
            if (auto != null)
            {
                var csrAuto = CsrGraph.Build(
                    auto.Edges.Select(e => (e.From, e.To, e.EdgeType ?? "prerequisite")),
                    auto.Nodes.Select(n => n.Id));
                return HttpResults.Success(new
                {
                    graphId = id,
                    source = "material-auto",
                    materialName = auto.MaterialName,
                    nodeCount = csrAuto.NodeCount,
                    offsets = csrAuto.Offsets.Count,
                    edges = csrAuto.AdjDst.Count,
                    edgeTypes = csrAuto.TypeNames,
                    ok = MaterialPipeline.ValidateGenerated(auto).Ok
                });
            }

            return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, $"图包不存在：{id}");
        });
    }

    private static (string UserId, string Role) Actor(string? userId, string? role) =>
        (string.IsNullOrWhiteSpace(userId) ? "demo-student-a" : userId!,
         IsValidRole(role) ? role! : AgentRoles.Student);

    private static IResult MapKb(object? result) => result switch
    {
        null => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "文档不存在"),
        "forbidden" => HttpResults.Fail(403, ErrorCodes.Forbidden, "仅文档所有者可修改"),
        "archived" => HttpResults.Fail(409, ErrorCodes.StateConflict, "文档已归档，不可修改"),
        "invalid_visibility" => HttpResults.Fail(422, ErrorCodes.ValidationError, "visibility 取值非法"),
        _ => HttpResults.Success(result)
    };
}
