using AstralPath.Shared.Json;

namespace AstralPath.Shared.Services;

/// <summary>
/// <see cref="OfflineDemoDataSource"/> 的 §44 / §45 / §46 部分。
///
/// 这三个模块在 <c>AstralPathModules</c> 中返回匿名类型，本文件用
/// <see cref="JsonShape"/> 规范化为 <see cref="System.Text.Json.JsonElement"/> 后
/// 映射为 UI 侧强类型行记录，避免把匿名类型泄漏到 XAML 绑定。
/// </summary>
public sealed partial class OfflineDemoDataSource
{
    // ── §44 受约束智能体 ────────────────────────────────────────

    public AgentTurnResult AgentTurn(string userId, string role, string utterance, string? sessionId)
    {
        var sid = string.IsNullOrWhiteSpace(sessionId)
            ? $"sess-{Guid.NewGuid():N}"[..18]
            : sessionId!;

        var raw = JsonShape.Normalize(_modules.AgentTurn(sid, userId, role, utterance));
        var decision = raw.Str("decision") ?? "fallback";

        return new AgentTurnResult(
            SessionId: raw.Str("sessionId") ?? sid,
            IntentId: raw.Str("intentId") ?? "meta.help",
            Score: raw.Num("score"),
            Decision: decision,
            Text: raw.Str("response") ?? string.Empty,
            Trace: raw.Str("trace") ?? string.Empty,
            MissingSlots: raw.StrArr("missingSlots"),
            Phase: raw.Str("phase") ?? "idle",
            Crisis: decision is "crisis.handoff");
    }

    public IReadOnlyList<AgentToolRow> AgentTools(string role)
    {
        var raw = JsonShape.Normalize(_modules.AgentTools(role));
        return raw.Arr("tools").Select(t => new AgentToolRow(
            IntentId: t.StrAny("IntentId", "intentId") ?? string.Empty,
            Description: t.StrAny("Description", "description") ?? string.Empty,
            RoleMask: t.StrAny("RoleMask", "roleMask") ?? string.Empty,
            RequiredSlots: t.StrArr("requiredSlots"),
            AnchorCount: t.Int("anchorCount"))).ToList();
    }

    // ── §45 知识库 ─────────────────────────────────────────────

    public IReadOnlyList<KbDocRow> ListKbDocuments() => _store.Lock(() =>
    {
        // 空查询词 = 列出当前角色可见的全部文档（可见性过滤在模块内完成）
        var raw = JsonShape.Normalize(_modules.SearchKb(_store, "demo-student-a", "student", string.Empty));
        return raw.Arr("items").Select(MapKbDoc).ToList();
    });

    public IReadOnlyList<KbHitRow> KbSearch(string userId, string role, string query)
    {
        var raw = JsonShape.Normalize(_modules.SearchKb(_store, userId, role, query));
        return raw.Arr("items").Select(t =>
        {
            var docId = t.StrAny("Id", "id") ?? "doc";
            return new KbHitRow(
                DocumentId: docId,
                ChunkId: $"{docId}#0",
                Title: t.Str("Title") ?? string.Empty,
                Snippet: t.Str("snippet") ?? string.Empty,
                Score: t.Num("score"),
                Visibility: t.Str("Visibility") ?? "private");
        }).ToList();
    }

    public KbDocRow CreateKbDocument(string title, string ownerUserId, string visibility, string text)
    {
        var doc = _modules.IngestKb(title, ownerUserId, visibility, "ACC-101", text);
        return new KbDocRow(doc.Id, doc.Title, doc.Visibility, "draft", 0, doc.Tags, ownerUserId);
    }

    private static KbDocRow MapKbDoc(System.Text.Json.JsonElement t)
    {
        var published = t.Int("publishedVersion", -1);
        return new KbDocRow(
            DocumentId: t.StrAny("Id", "id") ?? string.Empty,
            Title: t.Str("Title") ?? string.Empty,
            Visibility: t.Str("Visibility") ?? "private",
            Status: published >= 0 ? "published" : "draft",
            CurrentVersion: published,
            Tags: t.StrArr("Tags"),
            OwnerUserId: t.StrAny("OwnerUserId", "ownerUserId") ?? string.Empty);
    }

    // ── §46 用户画像 ───────────────────────────────────────────

    public ProfileOverviewRow? GetProfile(string studentId, bool teacherSide)
    {
        // 与 /v1/profile/{id} 对齐：形状是 { profile, features }
        var raw = JsonShape.Normalize(_modules.ProfileOverview(_store, studentId, teacherSide));
        if (raw.IsMissing()) return null;

        var profile = raw.Prop("profile");
        var features = raw.Prop("features");

        var optOut = profile.BoolAny("OptOut", "optOut") || raw.BoolAny("OptOut", "optOut");
        var suppressed = features.BoolAny("suppressed", "Suppressed") || raw.BoolAny("suppressed", "Suppressed");
        var sampleCount = features.Int("sampleCount", raw.Int("sampleCount"));

        return new ProfileOverviewRow(
            StudentId: profile.StrAny("StudentId", "studentId") ?? studentId,
            OptOut: optOut,
            SampleCount: sampleCount,
            Summary: suppressed
                ? "样本不足，暂不展示明细"
                : optOut
                    ? "已关闭个性化：行为等同无画像"
                    : sampleCount == 0
                        ? "暂无行为样本，先完成一次练习即可生成画像"
                        : $"已生成画像标签（样本 {sampleCount} 次作答）");
    }

    public ProfileRadarRow? GetProfileRadar(string studentId, bool teacherSide)
    {
        var raw = JsonShape.Normalize(_modules.ProfileRadar(_store, studentId, teacherSide));
        if (raw.IsMissing()) return null;

        return new ProfileRadarRow(
            StudentId: raw.Str("studentId") ?? studentId,
            Suppressed: raw.Bool("suppressed"),
            Axes: raw.Arr("axes")
                .Select(a => new ProfileAxis(a.Str("axis") ?? string.Empty, a.Num("value")))
                .ToArray());
    }

    public ProfileTagRow[] GetProfileTags(string studentId, bool teacherSide)
    {
        // 与 /v1/profile/{id}/tags 对齐：形状是 { studentId, OptOut, suppressed, note, tags }
        var raw = JsonShape.Normalize(_modules.GetProfile(studentId, teacherSide));

        var arr = raw.Arr("tags");
        if (arr.Length == 0) arr = raw.Prop("profile").Arr("Tags");

        return arr
            .Select(t => new ProfileTagRow(
                Tag: t.StrAny("Tag", "tag") ?? string.Empty,
                Domain: t.StrAny("Domain", "domain") ?? string.Empty,
                Weight: t.Num("Weight", 1.0),
                OptOut: t.BoolAny("OptOut", "optOut")))
            .ToArray();
    }

    public ProfileTimelineRow[] GetProfileTimeline(string studentId, bool teacherSide)
    {
        var raw = JsonShape.Normalize(_modules.ProfileTimeline(_store, studentId, teacherSide));

        // 模块返回 { days = 天数(int), snapshots = 逐日数组 }；
        // 注意 days 是**计数**而不是列表，不能当数组读（否则时间线永远为空）。
        var arr = raw.Arr("snapshots");
        if (arr.Length == 0) arr = raw.Arr("items");
        if (arr.Length == 0) arr = raw.Arr("points");

        return arr.Select(d => new ProfileTimelineRow(
            Date: d.Str("date") ?? string.Empty,
            Attempts: d.Int("attempts"),
            Accuracy: d.Num("accuracy"))).ToArray();
    }

    public ProfileOverviewRow? SetProfileOptOut(string studentId, bool optOut)
    {
        var raw = JsonShape.Normalize(_modules.SetProfileOptOut(studentId, optOut));
        if (raw.IsMissing()) return null;

        // 以数据源回读的实际状态为准，不假设写入一定成功（避免 UI 显示与后端不一致）
        var actual = raw.Bool("optOut");
        return new ProfileOverviewRow(
            StudentId: raw.Str("studentId") ?? studentId,
            OptOut: actual,
            SampleCount: raw.Int("sampleCount"),
            Summary: actual
                ? "已关闭个性化：行为等同无画像"
                : "已重新开启个性化");
    }
}
