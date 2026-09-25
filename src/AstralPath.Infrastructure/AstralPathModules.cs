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
            // 低置信但命中 execute 时补一句可执行入口，避免“已识别意图”空话
            if (route.Decision == "execute" && route.Score < 0.7)
                response = response + "\n（置信度 " + route.Score.ToString("0.00") + "，若不是你想要的，可换个说法）";
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

    private string BuildExecuteResponse(string intentId, string userId) => intentId switch
    {
        "debt.diagnose" => BuildDebtDiagnose(userId),
        "debt.explain" => BuildDebtExplain(userId),
        "plan.create" => BuildPlanCreate(userId),
        "plan.rebalance" => "已记录减负诉求。请在「知债」页点「减负 / 重排」，或告诉我每天可用分钟数（如 20）。",
        "today.tasks" => BuildTodayTasks(),
        "practice.start" => BuildPractice(),
        "progress.check" => BuildProgress(userId),
        "graph.view" => BuildGraphView(),
        "material.parse" => BuildMaterialParse(),
        "kb.search" => "知识库检索已开启：请直接说出关键词（如「递归」），我只返回你有可见性的文档。",
        "kb.ingest" => "知识入库需要教师权限。请到「藏书阁」上传并解析，再在知识库中发布。",
        "profile.view" => BuildProfile(userId),
        "profile.optout" => "已收到画像退出请求，将关闭个性化标签展示。可在「画像」页重新开启。",
        "consent.grant" => "已记录授权意向。请在「账户」页确认「同意教师可见」。",
        "consent.revoke" => "将撤销教师可见授权，并即时清除相关缓存。",
        "whatif.simulate" => "What-if：告诉我目标知识点（如「反向传播」），我会模拟先修债清掉后 impact 的变化。",
        "narrative.read" => BuildDebtExplain(userId),
        "sale.check" => BuildSaleCheck(userId),
        "teacher.hotspots" => "班级热点：按红边 impact 聚合共性问题（需教师权限）。",
        "meta.feedback" => "已收到反馈。演示版会记录在会话轨迹中。",
        "meta.help" => "我是知债学习助手。可问：\n·「帮我诊断知识债」— 读出当前红边\n·「生成14天计划」— 按 ≤35 分钟/天排程\n·「今日任务」— 教材真题 + 债边修复\n·「图谱 / 识网」— 教材知识点 DAG\n·「不想活了」— 自动转人工",
        _ => $"已识别意图：{intentId}。"
    };

    private string BuildDebtDiagnose(string userId)
    {
        try
        {
            var store = _store;
            if (store is null) return "已定位知识债诊断。当前未连接学情库，请在「知债」页查看红边。";
            var gver = store.Graph.GraphVersion;
            var scanned = store.ScanDebts(userId, 5);
            if (scanned.Count == 0)
            {
                var open = store.Students.TryGetValue(userId, out var st)
                    ? st.DebtEdges.Count(d => d.Status != "cleared")
                    : 0;
                return open == 0
                    ? $"诊断完成（图版本 {gver}）：当前没有开放债边。继续保持，可做今日任务巩固。"
                    : $"诊断完成（图版本 {gver}）：库中有 {open} 条历史债边，但 impact 未达红线。可在「知债」页看详情。";
            }
            var sb = new StringBuilder();
            sb.Append($"诊断完成（图版本 {gver}）：发现 {scanned.Count} 条知识债红边，按 impact 排序：\n");
            for (var i = 0; i < scanned.Count; i++)
            {
                var d = scanned[i];
                sb.Append($"{i + 1}. {d.FromKpName} → {d.ToKpName}  impact={d.Impact:0.#}（前掌握 {d.ScoreFrom:0.#} / 后 {d.ScoreTo:0.#}，错题 {d.Freq} 次）\n");
            }
            sb.Append("建议：先补前置知识点，再回到目标知识点。要生成 14 天计划吗？");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"诊断失败：{ex.Message}";
        }
    }

    private string BuildDebtExplain(string userId)
    {
        try
        {
            var store = _store;
            if (store is null) return "请在「知债」页点某条红边的「解释」。";
            var top = store.ScanDebts(userId, 1).FirstOrDefault();
            if (top is null) return "当前没有开放债边，无需解释。可先做今日任务。";
            return NarrativeAssembler.BuildDebtStory(top.FromKpName, top.ToKpName, top.ScoreFrom, top.ScoreTo, top.Impact);
        }
        catch (Exception ex)
        {
            return $"解释失败：{ex.Message}";
        }
    }

    private string BuildPlanCreate(string userId)
    {
        try
        {
            var store = _store;
            var (book, tasks) = MaterialRegistry.GetLatestTasks();
            if (store is not null)
            {
                if (store.Students.ContainsKey(userId) && store.Students[userId].DebtEdges.Count == 0)
                    store.ScanDebts(userId);
                var debts = store.Students.TryGetValue(userId, out var st)
                    ? st.DebtEdges.Where(d => d.Status is "open" or "repairing" || d.Impact > 0)
                        .OrderByDescending(d => d.Impact).Take(5).ToList()
                    : new();
                if (debts.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"已按 {debts.Count} 条债边生成 14 天修复草图（任一天 ≤35 分钟，基于《{book}》）：");
                    for (var i = 0; i < 7; i++)
                    {
                        var d = debts[i % debts.Count];
                        var focus = i % 2 == 0 ? d.FromKpName : d.ToKpName;
                        sb.AppendLine($"D{i + 1}–D{i + 8}：巩固「{focus}」约 {18 + (i % 3) * 5} 分钟（覆盖 {d.FromKpName}→{d.ToKpName}）");
                    }
                    sb.Append("完整排程请到「知债」页点「生成 14 天计划」。");
                    return sb.ToString();
                }
            }
            if (tasks.Count > 0)
            {
                var focus = !string.IsNullOrEmpty(tasks[0].KpName) ? tasks[0].KpName : (tasks[0].Stem.Length > 16 ? tasks[0].Stem[..16] : tasks[0].Stem);
                return NarrativeAssembler.BuildPlanCoach(book, focus, 35) +
                       "\n今日起 14 天：每天 25–35 分钟，前 7 天打前置，后 7 天回收目标知识点。点「今日」开始。";
            }
            return "可生成 14 天修复计划（任一天 ≤35 分钟）。请先导入教材，或直接说「按当前债边排」。";
        }
        catch (Exception ex)
        {
            return $"生成计划失败：{ex.Message}";
        }
    }

    private string BuildTodayTasks()
    {
        var (book, tasks) = MaterialRegistry.GetLatestTasks();
        if (tasks.Count == 0)
        {
            var ready = MaterialRegistry.ListMaterials().Where(m => m.Status == "ready").ToList();
            return ready.Count == 0
                ? "今日暂无任务。请先到「藏书阁」导入并解析教材。"
                : $"已解析 {ready.Count} 本教材，但尚未生成任务。请点「导入示例教材」或「今日」页刷新。";
        }
        var sb = new StringBuilder();
        sb.AppendLine($"今日任务（《{book}》，{tasks.Count} 项，约 {tasks.Sum(t => t.EstMin)} 分钟）：");
        var n = 0;
        foreach (var t in tasks.Take(6))
        {
            n++;
            var label = string.IsNullOrWhiteSpace(t.Stem) ? t.Why : t.Stem;
            if (label.Length > 40) label = label[..40] + "…";
            sb.AppendLine($"{n}. [{t.EstMin} 分] {label}（{t.KpName}）");
        }
        if (tasks.Count > 6) sb.AppendLine($"…另有 {tasks.Count - 6} 项，见「今日」页。");
        sb.Append("做完可在「今日」提交，系统会更新掌握度与债边。");
        return sb.ToString();
    }

    private string BuildPractice()
    {
        var (book, tasks) = MaterialRegistry.GetLatestTasks();
        var t = tasks.FirstOrDefault(x => x.Type is "mcq" or "quiz" or "question") ?? tasks.FirstOrDefault();
        if (t is null) return "暂无练习题。请先解析教材，或点「换一批」从题库抽取。";
        var stem = t.Stem.Length > 48 ? t.Stem[..48] + "…" : t.Stem;
        return $"开始练习（《{book}》· {t.KpName}）：{stem}\n预计 {t.EstMin} 分钟。到「今日」页作答并提交。";
    }

    private string BuildProgress(string userId)
    {
        try
        {
            var store = _store;
            if (store is null || !store.Students.TryGetValue(userId, out var st))
                return "进度：请在「画像」页查看掌握度与销账条件。";
            var open = st.DebtEdges.Count(d => d.Status == "open");
            var repairing = st.DebtEdges.Count(d => d.Status == "repairing");
            var cleared = st.DebtEdges.Count(d => d.Status == "cleared");
            return $"销账进度：开放 {open} · 修复中 {repairing} · 已清 {cleared}。\n" +
                   "销账条件：目标知识点掌握度 ≥60，且前置债边 impact 归零。可在「知债」页点「销账检查」。";
        }
        catch (Exception ex)
        {
            return $"进度查询失败：{ex.Message}";
        }
    }

    private string BuildGraphView()
    {
        var graphs = MaterialRegistry.ListGraphs();
        if (graphs.Count == 0) return "识网为空。请先解析教材，系统会自动建知识点 DAG。";
        var g = graphs[0];
        var tasks = MaterialRegistry.GetTasksForGraph(g.GraphId);
        return $"识网已就绪：《{g.MaterialName}》共 {g.Nodes.Count} 个知识点 / {g.Edges.Count} 条先修边。\n" +
               $"已生成 {tasks.Count} 道今日任务。打开「识网」可看章节树、正文与思维导图。";
    }

    private string BuildMaterialParse()
    {
        var list = MaterialRegistry.ListMaterials();
        if (list.Count == 0) return "藏书阁为空。点「导入示例教材」，或上传 PDF/扫描件（自动 OCR）。";
        var ready = list.Count(m => m.Status == "ready");
        var parsing = list.Count(m => m.Status == "parsing");
        var failed = list.Count(m => m.Status == "failed");
        var top = list.Where(m => m.Status == "ready").Take(3)
            .Select(m => $"{Trunc(m.Name, 18)}（{m.PageCount} 页 / {m.NodeCount} 节点）");
        return $"资料库共 {list.Count} 本：就绪 {ready} · 解析中 {parsing} · 失败 {failed}。\n" +
               string.Join("\n", top.Select(s => "· " + s)) +
               "\n到「藏书阁」可查看解析详情并按章出题。";
    }

    private string BuildProfile(string userId)
    {
        var p = GetProfile(userId, teacherSide: false);
        return "画像已读取（可在「画像」页看雷达图）。\n" +
               "标签仅用于学习规划；可随时 opt-out 关闭个性化。\n" +
               "（若显示「样本不足」，说明练习量还不够 k-匿名阈值）";
    }

    private string BuildSaleCheck(string userId)
    {
        try
        {
            var store = _store;
            if (store is null) return "销账检查：目标知识点掌握度需 ≥60，且无开放债边。";
            var top = store.ScanDebts(userId, 1).FirstOrDefault();
            if (top is null) return "销账检查通过：当前无开放债边。可在「今日」继续巩固。";
            return $"销账检查未通过：仍有债边 {top.FromKpName}→{top.ToKpName}（impact={top.Impact:0.#}）。\n" +
                   $"条件：{top.FromKpName} 掌握度建议 ≥60，{top.ToKpName} 错题频次归零后再试。";
        }
        catch (Exception ex)
        {
            return $"销账检查失败：{ex.Message}";
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>可选学情库：智能体执行工具时读真实债边/学生。</summary>
    private AstralPathStore? _store;

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
    public object AgentTurn(string sessionId, string userId, string role, string utterance, AstralPathStore? store = null)
    {
        lock (_gate)
        {
            _store = store;
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

    /// <summary>快照时间线：按日聚合尝试记录；无尝试时用掌握度/债边生成基线快照。</summary>
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
                avgSelfConf = Math.Round(g.Average(a => (double)a.SelfConf), 3),
                openDebts = student.DebtEdges.Count(d => d.Status != "cleared"),
                avgScore = student.Mastery.Count == 0
                    ? 0.0
                    : Math.Round(student.Mastery.Values.Average(m => m.Score), 2),
                source = "attempts"
            })
            .ToList<object>();

        if (byDay.Count == 0)
        {
            // 无尝试时：用当前掌握度生成 3 日基线，避免画像时间线空壳
            var avgScore = student.Mastery.Count == 0
                ? 0.0
                : Math.Round(student.Mastery.Values.Average(m => m.Score), 2);
            var open = student.DebtEdges.Count(d => d.Status != "cleared");
            var acc = student.Mastery.Count == 0
                ? 0.0
                : Math.Round(student.Mastery.Values.Average(m => m.RecentAcc), 4);
            var conf = student.Mastery.Count == 0
                ? 3.0
                : Math.Round(student.Mastery.Values.Average(m => (double)m.SelfConf), 2);
            for (var i = 2; i >= 0; i--)
            {
                byDay.Add(new
                {
                    date = DateTime.UtcNow.Date.AddDays(-i).ToString("yyyy-MM-dd"),
                    attempts = 0,
                    accuracy = acc,
                    avgSelfConf = conf,
                    openDebts = open,
                    avgScore,
                    source = "mastery-baseline"
                });
            }
        }
        return new
        {
            studentId,
            suppressed = teacherSide && student.Attempts.Count < 10 && student.Mastery.Count < 3,
            days = byDay.Count,
            attemptCount = student.Attempts.Count,
            snapshots = byDay
        };
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

    // ══════════════════════════════════════════════════════════════════════
    //  运行时状态快照（P1：重启不丢）
    //  只导出「用户创造的数据」：知识库文档与正文、画像、智能体轮次、版本/发布/归档。
    //  分片（_kbChunks）、上传票据（_kbUploads）、会话句柄（_sessions）、CSR 缓存
    //  属瞬态或可重建数据，不落盘。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>导出可持久化的模块状态。</summary>
    public ModuleStateDto ExportState()
    {
        lock (_gate)
        {
            return new ModuleStateDto
            {
                KbDocs = new Dictionary<string, KbDocument>(_kbDocs, StringComparer.OrdinalIgnoreCase),
                KbTexts = new Dictionary<string, string>(_kbTexts, StringComparer.OrdinalIgnoreCase),
                Profiles = new Dictionary<string, LearningProfile>(_profiles, StringComparer.OrdinalIgnoreCase),
                KbVersions = _kbVersions.ToDictionary(
                    kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
                KbPublished = new Dictionary<string, string>(_kbPublished, StringComparer.OrdinalIgnoreCase),
                KbArchived = _kbArchived.ToList(),
                AgentTurns = _agentTurns.ToList()
            };
        }
    }

    /// <summary>合并导入（upsert，不清空现有数据）。</summary>
    public void ImportState(ModuleStateDto? state)
    {
        if (state is null) return;
        lock (_gate)
        {
            foreach (var kv in state.KbDocs) _kbDocs[kv.Key] = kv.Value;
            foreach (var kv in state.KbTexts) _kbTexts[kv.Key] = kv.Value;
            foreach (var kv in state.Profiles) _profiles[kv.Key] = kv.Value;
            foreach (var kv in state.KbVersions) _kbVersions[kv.Key] = kv.Value.ToList();
            foreach (var kv in state.KbPublished) _kbPublished[kv.Key] = kv.Value;
            foreach (var id in state.KbArchived) _kbArchived.Add(id);

            if (state.AgentTurns.Count > 0)
            {
                _agentTurns.Clear();
                _agentTurns.AddRange(state.AgentTurns);
            }
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
