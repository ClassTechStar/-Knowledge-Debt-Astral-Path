using System.Text;
using System.Text.RegularExpressions;

namespace AstralPath.Core.Agent;

/// <summary>
/// 模式集匹配（§47 OC-1）：归一化 + Contains 级联 / 正则回退。
/// 语义等价：命中任一模式即 true；用于危机词、禁词、锚点。
/// </summary>
public sealed class PatternSet
{
    private readonly string[] _patterns;
    private readonly bool[] _isRegex;
    private readonly Regex[] _regexes;
    public string Name { get; }
    public int Count => _patterns.Length;

    public PatternSet(string name, IEnumerable<string> patterns)
    {
        Name = name;
        var list = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        _patterns = list.ToArray();
        _isRegex = new bool[_patterns.Length];
        _regexes = new Regex[_patterns.Length];
        for (var i = 0; i < _patterns.Length; i++)
        {
            var p = _patterns[i];
            var looksRegex = p.Contains('(') || p.Contains('[') || p.Contains('|') || p.Contains('*') || p.Contains('+');
            _isRegex[i] = looksRegex;
            if (looksRegex)
            {
                _regexes[i] = new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
    }

    /// <summary>文本归一化：全半角、空白、常见繁简差异折叠（§47 R0/R3）。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = ch;
            // 全角 → 半角
            if (c >= 0xFF01 && c <= 0xFF5E) c = (char)(c - 0xFEE0);
            if (c == 0x3000) c = ' ';
            if (char.IsWhiteSpace(c) || c == '　') c = ' ';
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public bool ContainsAny(string? raw)
    {
        var norm = Normalize(raw);
        if (norm.Length == 0) return false;
        for (var i = 0; i < _patterns.Length; i++)
        {
            if (_isRegex[i])
            {
                if (_regexes[i].IsMatch(norm) || _regexes[i].IsMatch(raw ?? "")) return true;
            }
            else
            {
                var p = Normalize(_patterns[i]);
                if (p.Length > 0 && norm.Contains(p, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    public (bool Hit, string? Pattern, int Index) MatchFirst(string? raw)
    {
        var norm = Normalize(raw);
        for (var i = 0; i < _patterns.Length; i++)
        {
            if (_isRegex[i])
            {
                if (_regexes[i].IsMatch(norm) || _regexes[i].IsMatch(raw ?? ""))
                    return (true, _patterns[i], i);
            }
            else
            {
                var p = Normalize(_patterns[i]);
                if (p.Length > 0 && norm.Contains(p, StringComparison.Ordinal))
                    return (true, _patterns[i], i);
            }
        }
        return (false, null, -1);
    }

    /// <summary>返回命中的最长模式长度（用于判断"整句是否就是这句客套话"）。</summary>
    public int LongestMatchLength(string? raw)
    {
        var norm = Normalize(raw);
        if (norm.Length == 0) return 0;
        var best = 0;
        for (var i = 0; i < _patterns.Length; i++)
        {
            var p = Normalize(_patterns[i]);
            if (p.Length == 0) continue;
            if (_isRegex[i])
            {
                if (_regexes[i].IsMatch(norm)) best = Math.Max(best, norm.Length);
            }
            else if (norm.Contains(p, StringComparison.Ordinal))
            {
                best = Math.Max(best, p.Length);
            }
        }
        return best;
    }
}

/// <summary>受约束智能体角色与意图位图（§47 OC-2 / §44 R2）。</summary>
public static class AgentRoles
{
    public const string Student = "student";
    public const string Teacher = "teacher";
    public const string Admin = "admin";
    public const string Demo = "demo";
}

public sealed record AgentIntent(
    int Bit,
    string IntentId,
    string RoleMask,
    string[] Anchors,
    string[] RequiredSlots,
    string Description);

public sealed record AgentRouteResult(
    string IntentId,
    double Score,
    string Decision, // execute | clarify | fallback | crisis.handoff
    string[] MissingSlots,
    string Trace);

/// <summary>打分明细（调试与教师侧可解释性用）。</summary>
public sealed record IntentScore(string IntentId, double Score, string Anchor);

/// <summary>
/// 受约束智能体路由器 v3。
///
/// 相对 v2 的修复：
/// ① v2 定义了 TauExec / TauClarify / MaxClarify 三个常量却**从未使用**，
///    实际阈值是硬编码 0.5，注释说的"低置信"分支根本不存在。
///    v3 真正启用它们，并加入「次优差距过小 → 判为歧义」的判定。
/// ② 打分只取单个最高锚点，多锚点同时命中（更强的证据）不加分。
///    v3 改为「主锚点 + 其余锚点饱和累加 + 覆盖率奖励」。
/// ③ 锚点分数只取决于长度，与句子无关 → 长句里偶然命中一个 2 字词也会 execute。
///    v3 引入覆盖率（命中长度 / 句子长度）抑制偶然命中。
/// ④ R2.5 负例用 ContainsAny → "好的，帮我生成计划" 会被整句判死。
///    v3 只在负例占整句 ≥60% 时才落。
/// ⑤ R1 槽位分支只要传了 awaitingIntent 就永远返回 clarify，**永远不会 execute**，
///    而且既不抽取已填槽位，也不检查 clarifyCount → 追问可以无限循环。
///    v3：抽取已填槽位、槽位齐了就 execute、超过 MaxClarify 停止追问、
///    且允许"中途换话题"（新意图分数够高就切换）。
/// ⑥ 角色过滤用 1u &lt;&lt; (Bit % 32) 位图 —— 意图超过 32 个就会位冲突，
///    且每意图都要 Split 字符串。v3 改为预建 HashSet，无 32 位上限。
/// ⑦ 无多轮上下文：用户说"再解释一下"无法继承上文意图。v3 支持 prevIntent。
/// </summary>
public sealed class AgentRouter
{
    public const double TauExec = 0.75;
    public const double TauClarify = 0.60;
    public const int MaxClarify = 2;

    private readonly PatternSet _crisis;
    private readonly PatternSet _banned;
    private readonly PatternSet _negative;
    private readonly IReadOnlyList<AgentIntent> _intents;
    private readonly Dictionary<string, HashSet<string>> _roleIntents;

    public PatternSet Banned => _banned;
    public PatternSet Crisis => _crisis;
    public IReadOnlyList<AgentIntent> Intents => _intents;

    public AgentRouter(
        IEnumerable<string> crisisWords,
        IEnumerable<string> bannedWords,
        IEnumerable<string> negativeWords,
        IEnumerable<AgentIntent> intents)
    {
        _crisis = new PatternSet("crisis", crisisWords);
        _banned = new PatternSet("banned", bannedWords);
        _negative = new PatternSet("negative", negativeWords);
        _intents = intents.ToList();
        _roleIntents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in new[] { AgentRoles.Student, AgentRoles.Teacher, AgentRoles.Admin, AgentRoles.Demo })
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in _intents)
            {
                if (it.RoleMask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    set.Add(it.IntentId);
                }
            }
            _roleIntents[role] = set;
        }
    }

    private bool Allows(string role, string intentId)
        => _roleIntents.TryGetValue(role, out var set) && set.Contains(intentId);

    /// <summary>对一句话打分，返回全部候选（按分数降序）。</summary>
    public IReadOnlyList<IntentScore> Score(string? utterance, string role)
    {
        var norm = PatternSet.Normalize(utterance);
        var scores = new List<IntentScore>();
        if (norm.Length == 0) return scores;

        foreach (var it in _intents)
        {
            if (it.IntentId == "crisis.handoff") continue; // 由 R0 单独处理
            if (!Allows(role, it.IntentId)) continue;

            double maxS = 0, sumOthers = 0;
            var bestAnchor = "";
            var matchedLen = 0;
            foreach (var anchor in it.Anchors)
            {
                var a = PatternSet.Normalize(anchor);
                if (a.Length == 0 || !norm.Contains(a, StringComparison.Ordinal)) continue;
                // 越长锚点越特异
                var s = 0.5 + 0.5 * Math.Min(1.0, a.Length / 8.0);
                matchedLen += a.Length;
                if (s > maxS) { sumOthers += maxS; maxS = s; bestAnchor = anchor; }
                else sumOthers += s;
            }

            // 意图描述里的核心词算弱证据
            var desc = PatternSet.Normalize(it.Description);
            if (desc.Length >= 2 && norm.Contains(desc, StringComparison.Ordinal))
                maxS = Math.Max(maxS, 0.5);

            if (maxS <= 0) continue;

            // 多锚点饱和累加 + 覆盖率奖励（抑制长句里的偶然短词命中）
            var score = maxS + 0.5 * Math.Min(sumOthers, 1.0);
            score += 0.15 * Math.Min(1.0, matchedLen / Math.Max(6.0, norm.Length));
            score = Math.Clamp(score, 0.0, 1.0);
            scores.Add(new IntentScore(it.IntentId, Math.Round(score, 6), bestAnchor));
        }
        return scores.OrderByDescending(s => s.Score).ThenBy(s => s.IntentId, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 分类。
    /// </summary>
    /// <param name="awaitingIntent">上一轮正在追问的意图（槽位填充中）。</param>
    /// <param name="prevIntentId">上一轮已执行的意图，用于"再解释一下"这类省略指代。</param>
    public AgentRouteResult Classify(
        string? utterance,
        string role,
        int clarifyCount = 0,
        string? awaitingIntent = null,
        string? prevIntentId = null)
    {
        // ── R0 危机词（最高优先级，永远先判）────────────
        var crisis = _crisis.MatchFirst(utterance);
        if (crisis.Hit)
            return new AgentRouteResult("crisis.handoff", 1.0, "crisis.handoff", Array.Empty<string>(),
                $"R0:crisis:{crisis.Pattern}");

        var norm = PatternSet.Normalize(utterance);

        // ── R2.5 负例快落（v3：要求负例占整句 ≥60%，避免误杀"好的，帮我…"）──
        if (norm.Length > 0 && _negative.ContainsAny(norm))
        {
            var longest = _negative.LongestMatchLength(norm);
            var ratio = norm.Length == 0 ? 0 : (double)longest / norm.Length;
            if (ratio >= 0.6)
                return new AgentRouteResult("meta.help", 0.0, "fallback", Array.Empty<string>(),
                    $"R2.5:negative_lexicon;ratio={ratio:0.00}");
        }

        var ranked = Score(utterance, role);
        var best = ranked.Count > 0 ? ranked[0] : null;
        var second = ranked.Count > 1 ? ranked[1] : null;

        // ── R1 槽位填充 / 话题切换 ───────────────────────
        if (!string.IsNullOrWhiteSpace(awaitingIntent))
        {
            var pending = _intents.FirstOrDefault(i => i.IntentId == awaitingIntent);

            // 用户中途换话题：新意图分数够高就切换
            if (best is not null && !string.Equals(best.IntentId, awaitingIntent, StringComparison.Ordinal)
                && best.Score >= TauExec)
            {
                return Decide(best, ranked, pendingSlots: Array.Empty<string>(), tracePrefix: "R1:topic_switch");
            }

            if (pending is not null)
            {
                var filled = ExtractProvidedSlots(pending, norm);
                var remaining = pending.RequiredSlots
                    .Where(s => !filled.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .ToArray();

                if (remaining.Length == 0)
                    return new AgentRouteResult(pending.IntentId, 1.0, "execute", Array.Empty<string>(),
                        "R1:slots_complete");

                // 追问次数用完 → 不再纠缠，按已有信息执行
                if (clarifyCount >= MaxClarify)
                    return new AgentRouteResult(pending.IntentId, 0.6, "execute", remaining,
                        $"R1:max_clarify_reached({MaxClarify})");

                return new AgentRouteResult(pending.IntentId, 0.8, "clarify", remaining,
                    "R1:awaiting_slot:" + string.Join(",", remaining));
            }
        }

        // ── R3 锚点路由 ──────────────────────────────────
        if (best is not null)
        {
            var intent = _intents.FirstOrDefault(i => i.IntentId == best.IntentId);
            var filled = intent is null ? Array.Empty<string>() : ExtractProvidedSlots(intent, norm);
            var missing = intent?.RequiredSlots
                .Where(s => !filled.Contains(s, StringComparer.OrdinalIgnoreCase))
                .ToArray() ?? Array.Empty<string>();

            // 分数够高，或上文意图延续（"再解释一下"）
            var inherits = prevIntentId is not null
                           && string.Equals(best.IntentId, prevIntentId, StringComparison.Ordinal);
            var threshold = inherits ? TauClarify : TauExec;

            // 歧义：与次优差距过小 → 先问一句
            var ambiguous = second is not null && best.Score - second.Score < 0.08;

            if (best.Score >= threshold && !ambiguous)
                return Decide(best, ranked, missing, "R3:anchor");

            if (best.Score >= TauClarify)
                return new AgentRouteResult(best.IntentId, best.Score,
                    missing.Length > 0 || ambiguous ? "clarify" : "execute",
                    missing, $"R3:mid_confidence;score={best.Score:0.00};ambiguous={ambiguous}");

            // 低分：沿用上文意图兜底（避免每句话都掉回 help）
            if (inherits)
                return new AgentRouteResult(prevIntentId!, best.Score, "execute", missing,
                    $"R3:anaphora;score={best.Score:0.00}");
        }

        // ── R6 兜底 ──────────────────────────────────────
        var help = _intents.FirstOrDefault(i => i.IntentId == "meta.help");
        return new AgentRouteResult(help?.IntentId ?? "meta.help", 0.0, "fallback", Array.Empty<string>(), "R6:fallback");
    }

    private AgentRouteResult Decide(IntentScore best, IReadOnlyList<IntentScore> ranked, string[] pendingSlots, string tracePrefix)
    {
        var decision = pendingSlots.Length == 0 ? "execute" : "clarify";
        var near = ranked.Count > 1 ? ranked[1] : null;
        return new AgentRouteResult(best.IntentId, best.Score, decision, pendingSlots,
            $"{tracePrefix}:{best.Anchor};score={best.Score:0.00};second={(near?.IntentId ?? "-")}");
    }

    /// <summary>口语填充词：去掉后仍有内容才算"提供了槽位值"。</summary>
    private static readonly string[] SlotFillers =
    {
        "帮我", "请", "麻烦", "我想", "我要", "给我", "我的", "能不能", "可以", "一下", "一个",
        "帮", "查", "找", "看", "要", "给", "的", "是", "了", "吗", "呢", "把", "个"
    };

    private const string SlotPunct = "，,。.！!？?：:；;、\"'“”‘’（）()【】[]";

    /// <summary>
    /// 从用户话里抽取已提供的槽位。
    /// 两级启发式（纯函数、可测；真实部署应替换为 NER/模板抽槽）：
    ///   ① 槽位**名**出现 → 该槽位已填（"书名是会计基础"）
    ///   ② 残余法：把命中锚点与口语填充词剔除后仍有实质内容，
    ///      且只剩一个槽位未填 → 该残余即它的**值**（"检索 会计基础"）
    /// 缺了 ② 就会出现"用户明明给了值，系统还在追问"的死循环。
    /// </summary>
    public static IReadOnlyList<string> ExtractProvidedSlots(AgentIntent intent, string normalizedUtterance)
    {
        if (intent.RequiredSlots.Length == 0) return Array.Empty<string>();
        var norm = PatternSet.Normalize(normalizedUtterance);
        if (norm.Length == 0) return Array.Empty<string>();

        var filled = new List<string>();
        foreach (var s in intent.RequiredSlots)
            if (norm.Contains(PatternSet.Normalize(s), StringComparison.Ordinal)) filled.Add(s);
        if (filled.Count == intent.RequiredSlots.Length) return filled;

        var residual = norm;
        foreach (var a in intent.Anchors)
        {
            var na = PatternSet.Normalize(a);
            if (na.Length > 0) residual = residual.Replace(na, "", StringComparison.Ordinal);
        }
        foreach (var f in SlotFillers)
            residual = residual.Replace(f, "", StringComparison.Ordinal);
        residual = new string(residual.Where(c => !char.IsWhiteSpace(c) && !SlotPunct.Contains(c)).ToArray());

        var unfilled = intent.RequiredSlots.Where(s => !filled.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        if (residual.Length >= 2 && unfilled.Count == 1) filled.Add(unfilled[0]);

        return filled;
    }

    public bool IsBanned(string? text) => _banned.ContainsAny(text);

    public static AgentRouter CreateDefault() => new(
        crisisWords: new[] { "自杀", "不想活", "轻生", "结束生命", "自残", "伤害自己", "跳楼", "上吊", "suicide", "kill myself" },
        bannedWords: Formula.FormulaWeights.BannedWords,
        negativeWords: new[] { "哈哈", "呵呵", "随便", "没事了", "thanks", "thank you", "ok", "好的" },
        intents: DefaultIntents());

    public static IReadOnlyList<AgentIntent> DefaultIntents() => new AgentIntent[]
    {
        new(0, "crisis.handoff", "student,teacher,admin,demo", Array.Empty<string>(), Array.Empty<string>(), "危机转介"),
        new(1, "debt.diagnose", "student,demo", new[] { "知识债", "债边", "诊断", "红边", "薄弱" }, new string[0], "知识债诊断"),
        new(2, "debt.explain", "student,teacher,demo", new[] { "解释", "为什么", "怎么来的", "公式" }, new string[0], "债边解释"),
        new(3, "plan.create", "student,demo", new[] { "计划", "14天", "安排", "复习计划" }, new string[0], "生成修复计划"),
        new(4, "plan.rebalance", "student,demo", new[] { "减负", "重排", "调整计划", "太重" }, new string[0], "计划减负"),
        new(5, "today.tasks", "student,demo", new[] { "今日", "今天做", "今日任务" }, new string[0], "今日任务"),
        new(6, "practice.start", "student,demo", new[] { "练习", "做题", "小测", "测验" }, new string[0], "开始练习"),
        new(7, "progress.check", "student,demo", new[] { "进度", "销账", "进展", "streak" }, new string[0], "销账进度"),
        new(8, "graph.view", "student,teacher,demo", new[] { "图谱", "识网", "知识图谱", "思维导图" }, new string[0], "查看图谱"),
        new(9, "material.parse", "student,teacher,demo", new[] { "解析资料", "上传", "OCR", "教材" }, new string[0], "解析资料"),
        new(10, "kb.search", "student,teacher,demo", new[] { "知识库", "检索", "查资料", "文档" }, new string[0], "知识库检索"),
        new(11, "kb.ingest", "teacher,admin", new[] { "入库", "上传文档", "发布知识" }, new string[0], "知识入库"),
        new(12, "profile.view", "student,demo", new[] { "画像", "我的标签", "学习画像" }, new string[0], "查看画像"),
        new(13, "profile.optout", "student,demo", new[] { "退出画像", "关闭画像", "opt-out" }, new string[0], "画像退出"),
        new(14, "teacher.hotspots", "teacher,admin,demo", new[] { "热点", "班级", "共性问题" }, new string[0], "班级热点"),
        new(15, "consent.grant", "student,demo", new[] { "授权", "同意可见", "consent" }, new string[0], "授权教师可见"),
        new(16, "consent.revoke", "student,demo", new[] { "撤销", "取消授权", "关闭可见" }, new string[0], "撤销授权"),
        new(17, "whatif.simulate", "student,teacher,demo", new[] { "what-if", "如果", "模拟", "假设" }, new string[0], "What-if 模拟"),
        new(18, "narrative.read", "student,demo", new[] { "故事", "叙事", "讲解" }, new string[0], "叙事讲解"),
        new(19, "sale.check", "student,demo", new[] { "销账检查", "能否销账", "条件" }, new string[0], "销账检查"),
        new(20, "meta.help", "student,teacher,admin,demo", new[] { "帮助", "怎么用", "说明" }, new string[0], "帮助"),
        new(21, "meta.feedback", "student,teacher,demo", new[] { "反馈", "建议", "不好用" }, new string[0], "反馈"),
    };
}

/// <summary>结构化叙事装配（§47 OC-6）：模板 + 槽位，天生避开禁词。</summary>
public static class NarrativeAssembler
{
    public static string BuildDebtStory(string fromKp, string toKp, double scoreFrom, double scoreTo, double impact)
    {
        return $"你在{toKp}相关练习上多次出错，回溯显示{fromKp}掌握度为 {scoreFrom:0.##}，" +
               $"当前知识点掌握度为 {scoreTo:0.##}，影响分 {impact:0.##}。建议先巩固{fromKp}，再回到{toKp}。";
    }

    public static string BuildPlanCoach(string book, string focus, int minutes)
        => $"今天先围绕《{book}》掌握「{focus}」，合计约 {minutes} 分钟。";

    public static string Sanitize(string text, PatternSet banned)
    {
        if (!banned.ContainsAny(text)) return text;
        return "这段内容暂时无法展示，请查看教材原文或联系老师。";
    }
}

/// <summary>
/// 默认意图登记表（P2：单一事实源对拍）。
/// 与 tools/agent-intents.json 及 deploy/monolith-web/index.html 的 INTENTS 字面量
/// 三方逐字一致；scripts/verify_agent_intents.py 负责对拍，改动任何一方必须同步。
/// </summary>
public static class DefaultAgentIntents
{
    public static readonly AgentIntent[] Table =
    {
        new(1,  "debt.diagnose",   "student", new[] { "知识债", "债边", "诊断", "红边", "薄弱" },  Array.Empty<string>(), "诊断知识债"),
        new(2,  "debt.explain",    "student", new[] { "解释", "为什么", "怎么来的", "公式" },      Array.Empty<string>(), "解释红边怎么来的"),
        new(3,  "plan.create",     "student", new[] { "计划", "14天", "安排", "复习计划" },        Array.Empty<string>(), "生成 14 天计划"),
        new(4,  "plan.rebalance",  "student", new[] { "减负", "重排", "调整计划", "太重" },        Array.Empty<string>(), "计划减负"),
        new(5,  "today.tasks",     "student", new[] { "今日", "今天做", "今日任务" },              Array.Empty<string>(), "今日任务"),
        new(6,  "practice.start",  "student", new[] { "练习", "做题", "小测", "测验" },            Array.Empty<string>(), "开始练习"),
        new(7,  "progress.check",  "student", new[] { "进度", "销账", "进展", "streak" },          Array.Empty<string>(), "进度查询"),
        new(8,  "graph.view",      "student", new[] { "图谱", "识网", "知识图谱", "思维导图" },    Array.Empty<string>(), "识网摘要"),
        new(9,  "material.parse",  "student", new[] { "解析资料", "上传", "ocr", "教材", "藏书" }, Array.Empty<string>(), "资料解析引导"),
        new(10, "kb.search",       "student", new[] { "知识库", "检索", "查资料", "文档" },        Array.Empty<string>(), "章节检索"),
        new(11, "profile.view",    "student", new[] { "画像", "我的标签", "学习画像" },            Array.Empty<string>(), "查看画像"),
        new(12, "profile.optout",  "student", new[] { "退出画像", "关闭画像", "opt-out" },         Array.Empty<string>(), "关闭画像"),
        new(13, "whatif.simulate", "student", new[] { "what-if", "如果", "模拟", "假设" },         Array.Empty<string>(), "What-if 模拟"),
        new(14, "sale.check",      "student", new[] { "销账检查", "能否销账", "销账条件" },        Array.Empty<string>(), "销账条件查询"),
        new(15, "meta.help",       "student", new[] { "帮助", "怎么用", "说明" },                  Array.Empty<string>(), "帮助兜底")
    };
}
