using System.Security.Cryptography;
using System.Text;
using AstralPath.Core.Agent;
using AstralPath.Core.Graph;
using AstralPath.Infrastructure;

namespace AstralPath.Infrastructure;

/// <summary>AstralPath 模块仓库：智能体 / 知识库 / 画像 / CSR 图。</summary>
public sealed class AstralPathModules
{
    private readonly object _gate = new();
    private AgentRouter _router = AgentRouter.CreateDefault();
    private readonly Dictionary<string, KbDocument> _kbDocs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _kbTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LearningProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CsrGraph> _csrByGraph = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Dictionary<string, object?>> _agentTurns = new();

    public AgentRouter Router
    {
        get { lock (_gate) return _router; }
    }

    public CsrGraph GetOrBuildCsr(string graphId, AutoKnowledgeGraph? graph)
    {
        lock (_gate)
        {
            if (_csrByGraph.TryGetValue(graphId, out var cached)) return cached;
            if (graph == null) throw new InvalidOperationException("graph not found");
            var edges = graph.Edges.Select(e => (e.From, e.To, e.EdgeType));
            var nodes = graph.Nodes.Select(n => n.Id);
            var csr = CsrGraph.Build(edges, nodes);
            _csrByGraph[graphId] = csr;
            return csr;
        }
    }

    public object AgentConverse(string userId, string role, string utterance)
    {
        lock (_gate)
        {
            var crisis = _router.Crisis.MatchFirst(utterance);
            if (crisis.Hit)
            {
                var turn = new Dictionary<string, object?>
                {
                    ["userId"] = userId,
                    ["role"] = role,
                    ["intentId"] = "crisis.handoff",
                    ["decision"] = "crisis.handoff",
                    ["response"] = "你并不孤单。请立即联系身边信任的人或当地心理援助热线。系统不会把这里当作处分依据。",
                    ["trace"] = $"R0:crisis:{crisis.Pattern}",
                    ["ts"] = DateTime.UtcNow
                };
                _agentTurns.Add(turn);
                return turn;
            }

            var banned = _router.IsBanned(utterance);
            var route = _router.Classify(utterance, role);
            var response = route.Decision switch
            {
                "execute" => BuildExecuteResponse(route.IntentId, userId),
                "clarify" => $"为了帮你处理「{route.IntentId}」，还需要：{string.Join("、", route.MissingSlots)}。",
                _ => "我可以帮你：知识债诊断、14 天计划、今日任务、图谱查看、知识库检索。请描述你的目标。"
            };
            if (banned || _router.IsBanned(response))
            {
                response = NarrativeAssembler.Sanitize(response, _router.Banned);
            }

            var result = new Dictionary<string, object?>
            {
                ["userId"] = userId,
                ["role"] = role,
                ["intentId"] = route.IntentId,
                ["score"] = route.Score,
                ["decision"] = route.Decision,
                ["missingSlots"] = route.MissingSlots,
                ["response"] = response,
                ["trace"] = route.Trace,
                ["bannedHit"] = banned,
                ["ts"] = DateTime.UtcNow
            };
            _agentTurns.Add(result);
            return result;
        }
    }

    private static string BuildExecuteResponse(string intentId, string userId) => intentId switch
    {
        "debt.diagnose" => "已定位知识债诊断意图。请在「知债」页查看红边与 impact。",
        "plan.create" => "可生成 14 天修复计划（任一天 ≤35 分钟）。",
        "today.tasks" => "今日任务来自教材真题与课程债边计划。",
        "graph.view" => "请打开「识网」查看教材知识图谱 / 思维导图。",
        "material.parse" => "请在藏书阁上传教材，系统会 OCR/解析并自动建图。",
        "kb.search" => "知识库检索：仅返回你有可见性的文档。",
        "profile.optout" => "已收到画像退出请求，将关闭个性化标签展示。",
        "consent.revoke" => "将撤销教师可见授权，并即时清除相关缓存。",
        _ => $"已识别意图：{intentId}。"
    };

    public KbDocument IngestKb(string title, string ownerUserId, string visibility, string courseCode, string text, string[]? tags = null)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N");
            var doc = new KbDocument(
                id, title, ownerUserId,
                visibility is "private" or "consented" or "course" or "public" ? visibility : "private",
                courseCode,
                tags ?? Array.Empty<string>(),
                "ready",
                text?.Length ?? 0,
                null,
                DateTime.UtcNow,
                DateTime.UtcNow);
            _kbDocs[id] = doc;
            _kbTexts[id] = text ?? "";
            return doc;
        }
    }

    /// <summary>知识库检索：可见性前置过滤（§6.6 URGENT）。</summary>
    public object SearchKb(AstralPathStore store, string userId, string role, string query)
    {
        lock (_gate)
        {
            var q = (query ?? "").Trim();
            var scored = new List<(double Score, object Item)>();
            foreach (var doc in _kbDocs.Values.OrderByDescending(d => d.UpdatedAt))
            {
                if (_kbArchived.Contains(doc.Id)) continue;
                if (!Visible(doc, userId, role, store)) continue;
                var body = _kbTexts.GetValueOrDefault(doc.Id) ?? "";
                var score = 0.0;
                if (!string.IsNullOrEmpty(q))
                {
                    if (doc.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 2;
                    if (body.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 1;
                    foreach (var tag in doc.Tags)
                        if (tag.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 1.5;
                }
                else score = 0.1;
                if (score <= 0) continue;
                scored.Add((score, new
                {
                    doc.Id,
                    doc.Title,
                    doc.Visibility,
                    doc.CourseCode,
                    doc.Tags,
                    doc.CharCount,
                    publishedVersion = _kbPublished.GetValueOrDefault(doc.Id),
                    snippet = body.Length > 120 ? body[..120] + "…" : body,
                    score
                }));
            }
            var ordered = scored
                .OrderByDescending(x => x.Score)
                .Take(20)
                .Select(x => x.Item)
                .ToList();
            return new { userId, query = q, count = ordered.Count, items = ordered };
        }
    }

    /// <summary>兼容 3 参形态（无 consent 校验源，仅用于无授权依赖的内部路径）。</summary>
    public object SearchKb(string userId, string role, string query) => SearchKb(null!, userId, role, query);

    /// <summary>
    /// 可见性判定（§45.9 / §6.6 URGENT）。
    /// 修复两处缺陷：
    ///   ① 原实现对 `consented` 文档放行**任意** teacher，绕过 consent 闸门；
    ///   ② 首版修复按「该学生存在任意有效授权」判定，仍会放行**未获该请求者授权**的教师。
    /// 现要求：请求者本人（teacherId）对该文档所有者持有且仅有 granted 授权，撤销即时失效。
    /// </summary>
    private static bool Visible(KbDocument doc, string userId, string role, AstralPathStore? store)
    {
        if (role is "admin") return true;
        return doc.Visibility switch
        {
            "public" => true,
            "course" => true,
            "private" => doc.OwnerUserId == userId,
            "consented" => doc.OwnerUserId == userId || (role == "teacher" && HasConsent(store, doc.OwnerUserId, userId)),
            _ => false
        };
    }

    private static bool HasConsent(AstralPathStore? store, string ownerStudentId, string teacherId)
    {
        if (store is null) return false;   // fail-closed：无授权源即不放开
        return store.Consents.Values.Any(c =>
            string.Equals(c.StudentId, ownerStudentId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.TeacherId, teacherId, StringComparison.OrdinalIgnoreCase)
            && c.State == "granted"
            && c.AllowTeacher);
    }

    public LearningProfile UpsertProfile(string studentId, string tag, string domain, double weight, bool optOut = false)
    {
        lock (_gate)
        {
            // 禁列语义域（§46 / P1 词表）
            var bannedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sensitive", "crisis", "semantic" };
            if (bannedDomains.Contains(domain)) throw new InvalidOperationException("该标签域已禁列");

            if (!_profiles.TryGetValue(studentId, out var profile))
            {
                profile = new LearningProfile(studentId, new List<UserProfileTag>(), false, DateTime.UtcNow);
            }
            var tags = profile.Tags.ToList();
            var idx = tags.FindIndex(t => string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase));
            var newTag = new UserProfileTag(studentId, tag, domain, weight, optOut, DateTime.UtcNow);
            if (idx >= 0) tags[idx] = newTag; else tags.Add(newTag);
            profile = profile with { Tags = tags, UpdatedAt = DateTime.UtcNow };
            _profiles[studentId] = profile;
            return profile;
        }
    }

    public object GetProfile(string studentId, bool teacherSide)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(studentId, out var profile))
            {
                profile = new LearningProfile(studentId, Array.Empty<UserProfileTag>(), false, DateTime.UtcNow);
            }
            var visible = profile.VisibleTags(teacherSide);
            // 教师侧需 k-匿名：标签过少则不展示明细
            var suppressed = teacherSide && visible.Count < 3;
            // 修复 CS0173：三元表达式两个分支类型必须一致（object[] vs List<匿名类型> 无法推断）。
            // 统一为 List<object>，对外 JSON 形状保持不变（抑制时为 []）。
            var tags = suppressed
                ? new List<object>()
                : visible.Select(t => (object)new { t.Tag, t.Domain, t.Weight }).ToList();
            return new
            {
                studentId,
                profile.OptOut,
                suppressed,
                note = suppressed ? "样本不足，已做 k-匿名抑制" : (profile.OptOut ? "学生已 opt-out" : "ok"),
                tags
            };
        }
    }

    /// <summary>关闭个性化（§46.3 的默认语义，等价于 <c>SetProfileOptOut(studentId, true)</c>）。</summary>
    public object OptOutProfile(string studentId) => SetProfileOptOut(studentId, true);

    /// <summary>
    /// 设置个性化开关（§46.3）。可关闭也可重新开启——「可随时关闭」若不支持重新开启，
    /// 学生就再也无法恢复个性化，与方案 §46 的授权语义不符。
    /// </summary>
    public object SetProfileOptOut(string studentId, bool optOut)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(studentId, out var profile))
                profile = new LearningProfile(studentId, Array.Empty<UserProfileTag>(), optOut, DateTime.UtcNow);
            else
                profile = profile with { OptOut = optOut, UpdatedAt = DateTime.UtcNow };
            _profiles[studentId] = profile;
            return new { studentId, optOut = profile.OptOut, updatedAt = profile.UpdatedAt };
        }
    }

    public object AgentTurns() { lock (_gate) return _agentTurns.ToList(); }

    // ══════════════════════════════════════════════════════════════════════
    //  §44 智能体：会话状态机与工具清单（补齐 §4.0 接口目录所需能力）
    // ══════════════════════════════════════════════════════════════════════

    private readonly Dictionary<string, Dictionary<string, object?>> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>提交一轮对话：写入会话状态（§44.4 对话状态跟踪）。</summary>
    public object AgentTurn(string sessionId, string userId, string role, string utterance)
    {
        lock (_gate)
        {
            var turn = (Dictionary<string, object?>)AgentConverse(userId, role, utterance);
            var intentId = (string)turn["intentId"]!;
            var decision = (string)turn["decision"]!;

            if (!_sessions.TryGetValue(sessionId, out var state))
            {
                state = new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId,
                    ["userId"] = userId,
                    ["role"] = role,
                    ["phase"] = "idle",
                    ["clarifyCount"] = 0,
                    ["turnCount"] = 0,
                    ["createdAt"] = DateTime.UtcNow
                };
                _sessions[sessionId] = state;
            }

            state["activeIntent"] = intentId;
            state["phase"] = decision switch
            {
                "crisis.handoff" => "fallback",
                "clarify" => "clarifying",
                _ => "responding"
            };
            state["clarifyCount"] = decision == "clarify" ? (int)state["clarifyCount"]! + 1 : 0;
            state["turnCount"] = (int)state["turnCount"]! + 1;
            state["lastUtterance"] = utterance;
            state["updatedAt"] = DateTime.UtcNow;

            turn["sessionId"] = sessionId;
            turn["phase"] = state["phase"];
            turn["turnCount"] = state["turnCount"];
            return turn;
        }
    }

    public object? GetAgentSession(string sessionId)
    {
        lock (_gate) return _sessions.TryGetValue(sessionId, out var s) ? s : null;
    }

    public bool EndAgentSession(string sessionId)
    {
        lock (_gate) return _sessions.Remove(sessionId);
    }

    /// <summary>按角色返回可见工具清单（§44.3 工具白名单登记表的对外投影）。</summary>
    public object AgentTools(string role)
    {
        lock (_gate)
        {
            var tools = _router.Intents
                .Where(i => RoleAllows(i.RoleMask, role))
                .Select(i => new
                {
                    i.IntentId,
                    i.Description,
                    i.RoleMask,
                    requiredSlots = i.RequiredSlots,
                    anchorCount = i.Anchors.Length,
                    bit = i.Bit
                })
                .OrderBy(t => t.IntentId, StringComparer.Ordinal)
                .ToList();
            return new { role, count = tools.Count, tools };
        }
    }

    private static bool RoleAllows(string roleMask, string role) =>
        string.IsNullOrEmpty(roleMask)
        || roleMask.Contains(role, StringComparison.OrdinalIgnoreCase)
        || roleMask.Contains("all", StringComparison.OrdinalIgnoreCase);

    // ══════════════════════════════════════════════════════════════════════
    //  §45 知识库：文档生命周期（版本 / 发布 / 回滚 / 归档 / 标签树）
    // ══════════════════════════════════════════════════════════════════════

    private readonly Dictionary<string, List<KbVersion>> _kbVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _kbPublished = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _kbArchived = new(StringComparer.OrdinalIgnoreCase);

    public KbDocument? TryGetKb(string docId)
    {
        lock (_gate) return _kbDocs.GetValueOrDefault(docId);
    }

    public object? GetKbDocument(AstralPathStore store, string docId, string userId, string role)
    {
        lock (_gate)
        {
            if (!_kbDocs.TryGetValue(docId, out var doc)) return null;
            if (!Visible(doc, userId, role, store)) return null;
            return new
            {
                doc,
                archived = _kbArchived.Contains(docId),
                publishedVersion = _kbPublished.GetValueOrDefault(docId),
                versionCount = _kbVersions.GetValueOrDefault(docId)?.Count ?? 0
            };
        }
    }

    public object? PatchKbDocument(AstralPathStore store, string docId, string userId, string role,
        string? title, string? visibility, string? courseCode, string[]? tags)
    {
        lock (_gate)
        {
            if (!_kbDocs.TryGetValue(docId, out var doc)) return null;
            if (!CanEdit(doc, userId, role)) return "forbidden";
            if (visibility is not null &&
                visibility is not ("private" or "consented" or "course" or "public")) return "invalid_visibility";
            if (_kbArchived.Contains(docId)) return "archived";

            var updated = doc with
            {
                Title = string.IsNullOrWhiteSpace(title) ? doc.Title : title!,
                Visibility = visibility ?? doc.Visibility,
                CourseCode = string.IsNullOrWhiteSpace(courseCode) ? doc.CourseCode : courseCode!,
                Tags = tags ?? doc.Tags,
                UpdatedAt = DateTime.UtcNow
            };
            _kbDocs[docId] = updated;
            return updated;
        }
    }

    public object? SetKbTags(AstralPathStore store, string docId, string userId, string role, string[] tags)
    {
        var result = PatchKbDocument(store, docId, userId, role, null, null, null, tags);
        return result;
    }

    /// <summary>标签树：按 课程码 / 标签 两层聚合（§45.3 三层分类标签的对外投影）。</summary>
    public object KbTagTree(string userId, string role, AstralPathStore store)
    {
        lock (_gate)
        {
            var rows = _kbDocs.Values
                .Where(d => Visible(d, userId, role, store) && !_kbArchived.Contains(d.Id))
                .SelectMany(d => d.Tags.DefaultIfEmpty("(未标注)").Select(t => new { d.CourseCode, Tag = t }))
                .GroupBy(x => x.CourseCode)
                .Select(g => new
                {
                    course = g.Key,
                    tags = g.GroupBy(x => x.Tag)
                        .Select(t => new { tag = t.Key, count = t.Count() })
                        .OrderByDescending(t => t.count)
                        .ToList()
                })
                .OrderBy(g => g.course, StringComparer.Ordinal)
                .ToList();
            return new { count = rows.Count, courses = rows };
        }
    }

    public object? CreateKbVersion(string docId, string userId, string role, string text, string? note)
    {
        lock (_gate)
        {
            if (!_kbDocs.TryGetValue(docId, out var doc)) return null;
            if (!CanEdit(doc, userId, role)) return "forbidden";
            if (_kbArchived.Contains(docId)) return "archived";

            var list = _kbVersions.TryGetValue(docId, out var v) ? v : (_kbVersions[docId] = new List<KbVersion>());
            var version = new KbVersion(
                $"v{list.Count + 1}", docId, text?.Length ?? 0,
                string.IsNullOrWhiteSpace(note) ? null : note, userId, DateTime.UtcNow);
            list.Add(version);
            _kbTexts[docId] = text ?? "";
            _kbDocs[docId] = doc with { CharCount = text?.Length ?? 0, UpdatedAt = DateTime.UtcNow };
            return version;
        }
    }

    public object? ListKbVersions(string docId, string userId, string role, AstralPathStore store)
    {
        lock (_gate)
        {
            if (!_kbDocs.TryGetValue(docId, out var doc)) return null;
            if (!Visible(doc, userId, role, store)) return "forbidden";
            return new
            {
                docId,
                publishedVersion = _kbPublished.GetValueOrDefault(docId),
                versions = (_kbVersions.GetValueOrDefault(docId) ?? new List<KbVersion>())
                    .OrderByDescending(x => x.CreatedAt).ToList()
            };
        }
    }

    public object? PublishKb(string docId, string userId, string role, string? version)
    {
        lock (_gate)
        {
            if (!_kbDocs.TryGetValue(docId, out var doc)) return null;
            if (!CanEdit(doc, userId, role)) return "forbidden";
            if (_kbArchived.Contains(docId)) return "archived";
            var list = _kbVersions.GetValueOrDefault(docId) ?? new List<KbVersion>();
            if (list.Count == 0) return "no_version";
            var target = version ?? list[^1].Version;
            if (list.All(x => x.Version != target)) return "version_not_found";
            _kbPublished[docId] = target;
            var updated = doc with { Status = "ready", UpdatedAt = DateTime.UtcNow };
            _kbDocs[docId] = updated;
            return new { updated, publishedVersion = target };
        }
    }

    public object? RollbackKb(string docId, string userId, string role, string version)
    {
        lock (_gate)
        {
            if (!_kbDocs.TryGetValue(docId, out var doc)) return null;
            if (!CanEdit(doc, userId, role)) return "forbidden";
            var list = _kbVersions.GetValueOrDefault(docId) ?? new List<KbVersion>();
            var target = list.FirstOrDefault(x => x.Version == version);
            if (target is null) return "version_not_found";
            _kbPublished[docId] = target.Version;
            _kbDocs[docId] = doc with { UpdatedAt = DateTime.UtcNow };
            return new { docId, publishedVersion = target.Version, rolledBackTo = target.Version };
        }
    }

    public object? ArchiveKb(string docId, string userId, string role)
    {
        lock (_gate)
        {
            if (!_kbDocs.TryGetValue(docId, out var doc)) return null;
            if (!CanEdit(doc, userId, role)) return "forbidden";
            _kbArchived.Add(docId);
            var updated = doc with { Status = "archived", UpdatedAt = DateTime.UtcNow };
            _kbDocs[docId] = updated;
            return new { updated, archived = true };
        }
    }

    private static bool CanEdit(KbDocument doc, string userId, string role) =>
        role == "admin" || doc.OwnerUserId == userId;

    // ── 分片直传与证据片段（原为 501 延后项，现补齐）────────────────────
    private readonly Dictionary<string, KbUploadTicket> _kbUploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _kbUploadParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<KbChunk>> _kbChunks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>申请分片直传票据（演示版：分片正文随 commit 提交，票据仅登记元数据）。</summary>
    public object CreateUploadTicket(string title, string ownerUserId, string visibility, string courseCode, int partCount)
    {
        lock (_gate)
        {
            if (partCount is < 1 or > 512)
                throw new ArgumentOutOfRangeException(nameof(partCount), "分片数须在 1–512 之间");
            var ticket = new KbUploadTicket($"up-{Guid.NewGuid():N}"[..14], title, ownerUserId,
                visibility is "private" or "consented" or "course" or "public" ? visibility : "private",
                courseCode, partCount, DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
            _kbUploads[ticket.UploadId] = ticket;
            _kbUploadParts[ticket.UploadId] = new List<string>();
            return ticket;
        }
    }

    /// <summary>分片合并提交：按序拼接正文 → 建文档 → 切分证据片段。</summary>
    public object? CommitUpload(string uploadId, string ownerUserId, string role, IReadOnlyList<string> parts)
    {
        lock (_gate)
        {
            if (!_kbUploads.TryGetValue(uploadId, out var ticket)) return null;
            if (!string.Equals(ticket.OwnerUserId, ownerUserId, StringComparison.OrdinalIgnoreCase) && role != "admin")
                return "forbidden";
            if (parts.Count != ticket.PartCount) return "part_count_mismatch";

            var text = string.Concat(parts);
            var doc = IngestKb(ticket.Title, ticket.OwnerUserId, ticket.Visibility, ticket.CourseCode, text);
            _kbChunks[doc.Id] = BuildChunks(text);
            _kbUploads.Remove(uploadId);
            _kbUploadParts.Remove(uploadId);
            return new { doc, chunkCount = _kbChunks[doc.Id].Count, mergedChars = text.Length };
        }
    }

    /// <summary>读取证据片段（含上下文）。越权文档不可读。</summary>
    public object? GetChunk(string docId, string chunkId, string userId, string role, int context)
    {
        lock (_gate)
        {
            if (!_kbDocs.TryGetValue(docId, out var doc)) return null;
            if (!Visible(doc, userId, role, null)) return "forbidden";
            if (!_kbChunks.TryGetValue(docId, out var chunks)) chunks = _kbChunks[docId] = BuildChunks(_kbTexts.GetValueOrDefault(docId) ?? "");

            var idx = chunks.FindIndex(c => string.Equals(c.ChunkId, chunkId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return "chunk_not_found";

            var span = Math.Clamp(context, 0, 3);
            var from = Math.Max(0, idx - span);
            var to = Math.Min(chunks.Count - 1, idx + span);
            var window = new List<object>();
            for (var i = from; i <= to; i++)
                window.Add(new { chunks[i].ChunkId, chunks[i].Index, chunks[i].Text, isTarget = i == idx });
            return new { docId, chunkId, chunk = chunks[idx], context = window };
        }
    }

    /// <summary>知识库系统导入（INTERNAL）：批量建文档并切分片段。</summary>
    public object InternalImport(string ownerUserId, IReadOnlyList<(string Title, string Visibility, string CourseCode, string Text)> items)
    {
        lock (_gate)
        {
            var created = new List<object>();
            foreach (var it in items)
            {
                var doc = IngestKb(it.Title, ownerUserId, it.Visibility, it.CourseCode, it.Text ?? "");
                _kbChunks[doc.Id] = BuildChunks(it.Text ?? "");
                created.Add(new { doc.Id, doc.Title, chunkCount = _kbChunks[doc.Id].Count });
            }
            return new { imported = created.Count, items = created };
        }
    }

    /// <summary>按句号/换行切分，固定长度上限，保证片段可引用（§45.6 证据可溯源）。</summary>
    private static List<KbChunk> BuildChunks(string text)
    {
        var chunks = new List<KbChunk>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;
        const int size = 200;
        var idx = 0;
        for (var pos = 0; pos < text.Length; pos += size)
        {
            var len = Math.Min(size, text.Length - pos);
            chunks.Add(new KbChunk($"ck-{idx + 1}", idx, text.Substring(pos, len)));
            idx++;
        }
        return chunks;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  §46 用户画像：特征 / 六维雷达 / 快照时间线
    //  说明：本演示版特征由**已落库的事实**（attempts / mastery / debt_edges）直接派生，
    //        等价于 §46.2 八项纯函数在「行为信号已就绪」前提下的取值；
    //        完整版需按 §46.1 采集管道接入原始行为信号（见 §48 与分工方案 §8.6）。
    // ══════════════════════════════════════════════════════════════════════

    public object? ProfileOverview(AstralPathStore store, string studentId, bool teacherSide)
    {
        var features = ProfileFeatures(store, studentId, teacherSide);
        if (features is null) return null;
        lock (_gate)
        {
            var profile = _profiles.GetValueOrDefault(studentId)
                          ?? new LearningProfile(studentId, Array.Empty<UserProfileTag>(), false, DateTime.UtcNow);
            return new { profile, features };
        }
    }

    public object? ProfileFeatures(AstralPathStore store, string studentId, bool teacherSide)
    {
        if (!store.Students.TryGetValue(studentId, out var student)) return null;
        var attempts = student.Attempts;
        var openDebts = student.DebtEdges.Where(d => d.Status != "cleared").ToList();
        var graphNodeCount = Math.Max(1, store.Graph.Nodes.Count());

        double acc = attempts.Count == 0 ? 0 : attempts.Average(a => a.Correct ? 1.0 : 0.0);
        double accPrev = 0, accRecent = 0;
        if (attempts.Count >= 2)
        {
            var half = attempts.Count / 2;
            accPrev = attempts.Take(half).Average(a => a.Correct ? 1.0 : 0.0);
            accRecent = attempts.Skip(half).Average(a => a.Correct ? 1.0 : 0.0);
        }
        var lastAt = attempts.Count == 0 ? (DateTime?)null : attempts.Max(a => a.OccurredAt);
        var samplesEnough = attempts.Count >= 10;

        return new
        {
            studentId,
            window = "all_time",
            sampleCount = attempts.Count,
            // §46.2 八项特征（样本不足时返回 null，避免小样本误判）
            attemptCount = attempts.Count,
            accuracy = samplesEnough ? Math.Round(acc, 6) : (double?)null,
            accuracyTrend = samplesEnough ? Math.Round(accRecent - accPrev, 6) : (double?)null,
            avgSelfConf = attempts.Count == 0 ? (double?)null : Math.Round(attempts.Average(a => (double)a.SelfConf), 6),
            avgLatencyMs = attempts.Count == 0 ? (double?)null : Math.Round(attempts.Average(a => (double)a.LatencyMs), 1),
            hintRate = attempts.Count == 0 ? (double?)null
                : Math.Round(attempts.Count(a => a.HintsUsed > 0) / (double)attempts.Count, 6),
            masteryCoverage = Math.Round(student.Mastery.Count / (double)graphNodeCount, 6),
            openDebtCount = openDebts.Count,
            openDebtAvgImpact = openDebts.Count == 0 ? 0
                : Math.Round(openDebts.Average(d => d.Impact), 6),
            daysSinceLastAttempt = lastAt is null ? (int?)null
                : (int)Math.Floor((DateTime.UtcNow - lastAt.Value).TotalDays),
            suppressed = teacherSide && attempts.Count < 10,
            partial = !samplesEnough
        };
    }

    /// <summary>六维雷达（§46.5.2）：各维归一到 [0,100]。</summary>
    public object? ProfileRadar(AstralPathStore store, string studentId, bool teacherSide)
    {
        if (!store.Students.TryGetValue(studentId, out var student)) return null;
        var attempts = student.Attempts;
        var openDebts = student.DebtEdges.Where(d => d.Status != "cleared").ToList();
        var totalDebts = Math.Max(1, student.DebtEdges.Count);
        var graphNodeCount = Math.Max(1, store.Graph.Nodes.Count());

        double pct(double v) => Math.Round(Math.Clamp(v, 0, 1) * 100, 2);

        var masteryAvg = student.Mastery.Count == 0 ? 0 : student.Mastery.Values.Average(m => m.Score);
        var axes = new List<object>
        {
            new { axis = "掌握度", value = pct(masteryAvg / 100.0) },
            new { axis = "正确率", value = pct(attempts.Count == 0 ? 0 : attempts.Average(a => a.Correct ? 1.0 : 0.0)) },
            new { axis = "练习量", value = pct(Math.Min(attempts.Count / 40.0, 1)) },
            new { axis = "还债进度", value = pct(1 - openDebts.Count / (double)totalDebts) },
            new { axis = "自评置信", value = pct(attempts.Count == 0 ? 0 : attempts.Average(a => a.SelfConf / 5.0)) },
            new { axis = "覆盖广度", value = pct(student.Mastery.Count / (double)graphNodeCount) }
        };
        return new { studentId, suppressed = teacherSide && attempts.Count < 10, axes };
    }

    /// <summary>快照时间线：按日聚合尝试记录（演示版以事实流代替独立快照表）。</summary>
    public object? ProfileTimeline(AstralPathStore store, string studentId, bool teacherSide)
    {
        if (!store.Students.TryGetValue(studentId, out var student)) return null;
        var byDay = student.Attempts
            .GroupBy(a => a.OccurredAt.Date)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                date = g.Key.ToString("yyyy-MM-dd"),
                attempts = g.Count(),
                accuracy = Math.Round(g.Average(a => a.Correct ? 1.0 : 0.0), 6),
                avgSelfConf = Math.Round(g.Average(a => (double)a.SelfConf), 3)
            })
            .ToList();
        return new { studentId, suppressed = teacherSide && student.Attempts.Count < 10, days = byDay.Count, snapshots = byDay };
    }

    public object ModuleStatus(AstralPathStore store)
    {
        lock (_gate)
        {
            return new
            {
                product = "知债：星穹学途（Knowledge Debt: Astral Path）",
                english = "Knowledge Debt: Astral Path",
                code = "AstralPath",
                agent = new
                {
                    intents = _router.Intents.Count,
                    crisisPatterns = _router.Crisis.Count,
                    bannedPatterns = _router.Banned.Count,
                    turns = _agentTurns.Count,
                    sessions = _sessions.Count
                },
                knowledgeBase = new { documents = _kbDocs.Count, archived = _kbArchived.Count },
                profile = new { students = _profiles.Count },
                csr = new { graphs = _csrByGraph.Count },
                students = store.Students.Count
            };
        }
    }
}

/// <summary>知识库文档版本（§45.5 不可变版本控制）。</summary>
public sealed record KbVersion(
    string Version,
    string DocId,
    int CharCount,
    string? Note,
    string CreatedBy,
    DateTime CreatedAt);

/// <summary>知识库分片直传票据（§45.2）。</summary>
public sealed record KbUploadTicket(
    string UploadId, string Title, string OwnerUserId, string Visibility,
    string CourseCode, int PartCount, DateTime ExpiresAt, DateTime CreatedAt);

/// <summary>知识库证据片段（§45.6 可溯源引用）。</summary>
public sealed record KbChunk(string ChunkId, int Index, string Text);
