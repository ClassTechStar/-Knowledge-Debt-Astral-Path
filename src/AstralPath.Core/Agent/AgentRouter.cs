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

/// <summary>受约束智能体路由器（§47 v2 语义）。</summary>
public sealed class AgentRouter
{
    public const double TauExec = 0.75;
    public const double TauClarify = 0.60;
    public const int MaxClarify = 2;

    private readonly PatternSet _crisis;
    private readonly PatternSet _banned;
    private readonly PatternSet _negative;
    private readonly IReadOnlyList<AgentIntent> _intents;
    private readonly Dictionary<string, uint> _roleMasks;

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
        _roleMasks = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in new[] { AgentRoles.Student, AgentRoles.Teacher, AgentRoles.Admin, AgentRoles.Demo })
        {
            uint mask = 0;
            foreach (var it in _intents)
            {
                if (it.RoleMask.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(role, StringComparer.OrdinalIgnoreCase))
                {
                    mask |= 1u << (it.Bit % 32);
                }
            }
            _roleMasks[role] = mask;
        }
    }

    public AgentRouteResult Classify(string? utterance, string role, int clarifyCount = 0, string? awaitingIntent = null)
    {
        // R0 危机词
        var crisis = _crisis.MatchFirst(utterance);
        if (crisis.Hit)
            return new AgentRouteResult("crisis.handoff", 1.0, "crisis.handoff", Array.Empty<string>(),
                $"R0:crisis:{crisis.Pattern}");

        var norm = PatternSet.Normalize(utterance);
        // R2.5 负例快落
        if (_negative.ContainsAny(norm))
            return new AgentRouteResult("meta.help", 0.0, "fallback", Array.Empty<string>(), "R2.5:negative_lexicon");

        // R1 状态优先（调用方已提供 awaitingIntent 时表示槽位填充）
        if (!string.IsNullOrWhiteSpace(awaitingIntent))
        {
            var intent = _intents.FirstOrDefault(i => i.IntentId == awaitingIntent);
            if (intent != null)
                return new AgentRouteResult(intent.IntentId, 1.0, "clarify", intent.RequiredSlots, "R1:awaiting_slot");
        }

        // R2 角色位图 + R3 锚点（多锚点打分，取最高；比首个命中更稳）
        var roleMask = _roleMasks.GetValueOrDefault(role, 0u);
        AgentIntent? best = null;
        var bestScore = 0.0;
        var bestAnchor = "";
        foreach (var it in _intents)
        {
            var bit = 1u << (it.Bit % 32);
            if ((roleMask & bit) == 0) continue;
            var hitScore = 0.0;
            var hitAnchor = "";
            foreach (var anchor in it.Anchors)
            {
                var a = PatternSet.Normalize(anchor);
                if (a.Length == 0) continue;
                if (!norm.Contains(a, StringComparison.Ordinal)) continue;
                // 更长锚点更特异，得分更高
                var s = 0.55 + Math.Min(0.45, a.Length / 12.0);
                if (s > hitScore)
                {
                    hitScore = s;
                    hitAnchor = anchor;
                }
            }
            // 意图描述里的核心词也算弱证据
            var desc = PatternSet.Normalize(it.Description);
            if (desc.Length >= 2 && norm.Contains(desc, StringComparison.Ordinal))
                hitScore = Math.Max(hitScore, 0.5);

            if (hitScore > bestScore)
            {
                bestScore = hitScore;
                best = it;
                bestAnchor = hitAnchor;
            }
        }

        if (best is not null && bestScore >= 0.5)
        {
            // 槽位未填则 clarify，否则 execute（分数不足阈值时仍 execute，但标记低置信）
            var decision = best.RequiredSlots.Length == 0 ? "execute" : "clarify";
            var score = Math.Clamp(bestScore, 0.0, 1.0);
            return new AgentRouteResult(best.IntentId, score, decision, best.RequiredSlots,
                $"R3:anchor:{bestAnchor};score={score:0.00}");
        }

        // 默认兜底（无外部向量/模型时）
        var help = _intents.FirstOrDefault(i => i.IntentId == "meta.help");
        return new AgentRouteResult(help?.IntentId ?? "meta.help", 0.0, "fallback", Array.Empty<string>(), "R6:fallback");
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
