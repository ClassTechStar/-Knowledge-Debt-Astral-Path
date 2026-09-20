using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AstralPath.Contracts;
using AstralPath.Core.Extensions;
using AstralPath.Infrastructure;

namespace AstralPath.Services;

/// <summary>
/// §19 扩展服务的**单一路由来源**。
/// 单体（AstralPath.Api）引用本项目并映射全部 10 个控制器；各独立服务宿主只映射自己那一个。
/// 这样路由定义只有一份，拆分后路径与行为与拆分前完全一致（等价性由端到端测试保证）。
/// </summary>
public abstract class ExtensionServiceControllerBase : ControllerBase
{
    protected readonly AstralPathStore Store;

    protected ExtensionServiceControllerBase(AstralPathStore store) => Store = store;

    protected static bool Prop(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value);
    }

    protected static double Num(JsonElement el, string name, double fallback = 0)
        => Prop(el, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    protected static int Int(JsonElement el, string name, int fallback = 0)
        => Prop(el, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    protected static string Str(JsonElement el, string name, string fallback = "")
        => Prop(el, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    protected IResult NotFound(string msg) => HttpResults.Fail(404, ErrorCodes.ResourceNotFound, msg);
    protected IResult BadRequest(string msg) => HttpResults.Fail(400, ErrorCodes.ValidationError, msg);
}

// ═══════════ §19.1 concept-diffusion-svc ═══════════
[ApiController]
public sealed class DiffusionController : ExtensionServiceControllerBase
{
    public DiffusionController(AstralPathStore store) : base(store) { }

    [HttpPost("/v1/diffusion/simulate")]
    public IResult Simulate([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var studentId = Str(el, "studentId", "demo-student-a");
        return Store.Lock(() =>
        {
            if (!Store.Students.TryGetValue(studentId, out var student)) return NotFound("学生不存在");
            var nodes = Store.Graph.Nodes.Select(n => n.Id).ToList();
            var score = student.Mastery.ToDictionary(m => m.Key, m => m.Value.Score, StringComparer.Ordinal);
            var intervention = new Dictionary<string, double>(StringComparer.Ordinal);
            if (Prop(el, "intervention", out var iv) && iv.ValueKind == JsonValueKind.Object)
                foreach (var p in iv.EnumerateObject())
                    intervention[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble() : 0;
            var debts = student.DebtEdges.Select(d => (d.FromKp, d.ToKp, d.Impact)).ToList();
            var input = new DiffusionInput(nodes,
                Store.Graph.Edges.Select(e => (e.From, e.To, 1.0)).ToList(),
                score, intervention, debts, Num(el, "alpha", 0.55), Int(el, "depth", 6));
            var result = ConceptDiffusion.Run(input);
            return HttpResults.Success(new
            {
                algoVersion = result.AlgoVersion, alpha = result.Alpha, depth = result.Depth,
                nodeCount = result.NodeCount, truncationBound = result.TruncationBound,
                gains = result.Gains, repairRanking = result.RepairRanking.Take(10).ToList(),
                impactPrediction = ConceptDiffusion.PredictImpact(input, result)
            });
        });
    }
}

// ═══════════ §19.2 exam-impact-svc ═══════════
[ApiController]
public sealed class ExamImpactController : ExtensionServiceControllerBase
{
    public ExamImpactController(AstralPathStore store) : base(store) { }

    // 显式给出 Lock<T> 的泛型实参：两个 return 的静态类型不同（Array.Empty vs List），
    // 不写实参时类型推断失败并误选 Lock(Action) 重载（CS8030）。
    private IReadOnlyList<(string KpId, double Score, double Impact, double Weight)> Debts(string studentId)
        => Store.Lock<IReadOnlyList<(string KpId, double Score, double Impact, double Weight)>>(() =>
        {
            if (!Store.Students.TryGetValue(studentId, out var s))
                return Array.Empty<(string, double, double, double)>();
            return s.DebtEdges.Where(d => d.Status != "cleared")
                .Select(d => (d.ToKp,
                    Score: Store.Students[studentId].Mastery.TryGetValue(d.ToKp, out var mv) ? mv.Score : 0.0,
                    d.Impact, d.Weight)).ToList();
        });

    [HttpPost("/v1/exams/{examId}/impact")]
    public IResult Impact(string examId, [FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var studentId = Str(el, "studentId", "demo-student-a");
        var debts = Debts(studentId);
        if (debts.Count == 0 && !Store.Students.ContainsKey(studentId)) return NotFound("学生不存在");
        var input = new ExamImpactInput(examId, Int(el, "daysToExam", 7), Int(el, "dayBudgetMin", 30), debts);
        return HttpResults.Success(ExamImpact.Run(input));
    }

    [HttpPost("/v1/exams/{examId}/preexam-plan")]
    public IResult PreExamPlan(string examId, [FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var studentId = Str(el, "studentId", "demo-student-a");
        var debts = Debts(studentId);
        if (debts.Count == 0 && !Store.Students.ContainsKey(studentId)) return NotFound("学生不存在");
        var impact = ExamImpact.Run(new ExamImpactInput(examId, Int(el, "daysToExam", 7), Int(el, "dayBudgetMin", 30), debts));

        var days = new List<object>();
        var queue = impact.Plan.Where(p => p.SuggestedMin > 0).ToList();
        for (var day = 1; day <= impact.DaysToExam && queue.Count > 0; day++)
        {
            var remain = 35;                       // BASELINE：任一天 ≤35 分钟
            var items = new List<object>();
            while (remain > 0 && queue.Count > 0)
            {
                var take = Math.Min(remain, Math.Max(5, queue[0].SuggestedMin));
                items.Add(new { kpId = queue[0].KpId, minutes = take });
                remain -= take;
                queue.RemoveAt(0);
            }
            days.Add(new { day, minutes = 35 - remain, items });
        }
        return HttpResults.Success(new { examId, studentId, days, note = "任一天 ≤35 分钟；仅建议，不改写权威计划。" });
    }
}

// ═══════════ §19.3 peer-cohort-svc（强伦理）══════════
[ApiController]
public sealed class PeerCohortController : ControllerBase
{
    [HttpPost("/v1/cohorts/stats")]
    public IResult Stats([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var k = Prop(el, "k", out var kv) && kv.ValueKind == JsonValueKind.Number ? kv.GetInt32() : PeerCohort.DefaultK;
        var members = new List<(string, double)>();
        if (Prop(el, "scores", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number) members.Add(($"s{++i}", v.GetDouble()));
        }
        var result = PeerCohort.Run(new CohortInput(k, members));
        return HttpResults.Success(new
        {
            k, sampleCount = result.SampleCount, suppressed = result.Suppressed,
            p25 = result.P25, p50 = result.P50, p75 = result.P75, note = result.Note,
            ethics = "仅聚合；不输出个体、不排名、不做能力定性。"
        });
    }

    private static bool Prop(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value);
    }
}

// ═══════════ §19.4 study-group-svc（强伦理）══════════
[ApiController]
public sealed class StudyGroupController : ControllerBase
{
    [HttpPost("/v1/study-groups/match")]
    public IResult Match([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var members = new List<GroupMemberInput>();
        if (Prop(el, "members", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var id = Str(m, "studentId", "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var tags = new List<string>();
                if (Prop(m, "tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
                    foreach (var t in tg.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.String) tags.Add(t.GetString() ?? "");
                members.Add(new GroupMemberInput(id, tags));
            }
        }
        if (members.Count < 2)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "成员不足 2 人，无法成组");
        var result = StudyGroup.Match(members, Prop(el, "size", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32() : 4);
        return HttpResults.Success(new
        {
            groups = result.Groups, groupCount = result.Groups.Count, note = result.Note,
            ethics = "仅用聚合标签互补成组；不使用分数明文与排名。"
        });
    }

    private static bool Prop(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value);
    }

    private static string Str(JsonElement el, string name, string fallback = "")
        => Prop(el, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
}

// ═══════════ §19.5 micro-lesson-svc ═══════════
[ApiController]
public sealed class MicroLessonController : ExtensionServiceControllerBase
{
    public MicroLessonController(AstralPathStore store) : base(store) { }

    [HttpPost("/v1/micro-lessons/assemble")]
    public IResult Assemble([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var kpId = Str(el, "kpId", "");
        if (string.IsNullOrWhiteSpace(kpId)) return BadRequest("kpId 必填");
        var resources = new List<string>();
        if (Prop(el, "resources", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String) resources.Add(r.GetString() ?? "");
        var kpName = Store.Lock(() => Store.Graph.TryGetNode(kpId, out var n) ? n.Name : kpId);
        return HttpResults.Created(MicroLesson.Assemble(new MicroLessonInput(kpId, kpName, resources, Int(el, "minutes", 10))));
    }

    [HttpPost("/v1/micro-lessons/draft")]
    public IResult Draft([FromBody] JsonElement? body) => Assemble(body);

    [HttpGet("/v1/micro-lessons/{id}")]
    public IResult Get(string id) => NotFound("演示版微课为请求级装配结果，未持久化；请调用 POST /v1/micro-lessons/assemble 重新生成。");
}

// ═══════════ §19.6 learning-velocity-svc ═══════════
[ApiController]
public sealed class LearningVelocityController : ExtensionServiceControllerBase
{
    public LearningVelocityController(AstralPathStore store) : base(store) { }

    [HttpPost("/v1/velocity/fit")]
    public IResult Fit([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var series = new List<(DateTime, double)>();
        if (Prop(el, "series", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
                if (DateTime.TryParse(Str(p, "at", ""), out var at)) series.Add((at, Num(p, "score", 0)));
        }
        else
        {
            var studentId = Str(el, "studentId", "demo-student-a");
            series = Store.Lock(() =>
            {
                if (!Store.Students.TryGetValue(studentId, out var s)) return new List<(DateTime, double)>();
                return s.Attempts.OrderBy(a => a.OccurredAt)
                    .Select(a => (a.OccurredAt, a.Correct ? 85.0 : 45.0)).ToList();
            });
            if (series.Count == 0) return NotFound("无可用的尝试序列");
        }
        if (series.Count == 0) return BadRequest("series 不能为空");
        return HttpResults.Success(LearningVelocity.Fit(new VelocityInput(series, Num(el, "alpha", 0.3))));
    }
}

// ═══════════ §19.7 spaced-review-svc ═══════════
[ApiController]
public sealed class SpacedReviewController : ExtensionServiceControllerBase
{
    public SpacedReviewController(AstralPathStore store) : base(store) { }

    [HttpPost("/v1/spaced-review/schedule")]
    public IResult Schedule([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var items = new List<ReviewItem>();
        if (Prop(el, "items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                var kp = Str(it, "kpId", "");
                if (string.IsNullOrWhiteSpace(kp)) continue;
                DateTime? last = null;
                if (Prop(it, "lastReviewAt", out var lr) && lr.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(lr.GetString(), out var parsed)) last = parsed;
                items.Add(new ReviewItem(kp, Int(it, "lapses", 0), Num(it, "lastScore", 60), last));
            }
        }
        else
        {
            var studentId = Str(el, "studentId", "demo-student-a");
            items = Store.Lock(() =>
            {
                if (!Store.Students.TryGetValue(studentId, out var s)) return new List<ReviewItem>();
                return s.Mastery.Values.Select(m => new ReviewItem(m.KpId, 0, m.Score, m.LastAttemptAt)).ToList();
            });
        }
        if (items.Count == 0) return BadRequest("items 不能为空且需能解析出知识点");
        var plan = SpacedReview.Schedule(items, DateTime.UtcNow);
        return HttpResults.Success(new { count = plan.Count, plan, note = "仅建议队列；不直接写 mastery / cleared。" });
    }
}

// ═══════════ §19.8 prerequisite-simulator-svc ═══════════
[ApiController]
public sealed class PrerequisiteSimulatorController : ExtensionServiceControllerBase
{
    public PrerequisiteSimulatorController(AstralPathStore store) : base(store) { }

    [HttpPost("/v1/prereq-simulator/simulate")]
    public IResult Simulate([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var target = Str(el, "targetKp", "");
        if (string.IsNullOrWhiteSpace(target)) return BadRequest("targetKp 必填");
        var studentId = Str(el, "studentId", "demo-student-a");
        return Store.Lock(() =>
        {
            if (!Store.Students.TryGetValue(studentId, out var student)) return NotFound("学生不存在");
            var edges = Store.Graph.Edges.Select(e => (e.From, e.To, e.Weight)).ToList();
            var score = student.Mastery.ToDictionary(m => m.Key, m => m.Value.Score, StringComparer.Ordinal);
            var debts = student.DebtEdges.Select(d => (d.FromKp, d.ToKp, d.Impact)).ToList();
            return HttpResults.Success(PrerequisiteSimulator.Simulate(
                new PrereqSimInput(edges, score, debts, target, Num(el, "targetScore", 75))));
        });
    }
}

// ═══════════ §19.9 knowledge-forecast-svc（强伦理）══════════
[ApiController]
public sealed class KnowledgeForecastController : ExtensionServiceControllerBase
{
    public KnowledgeForecastController(AstralPathStore store) : base(store) { }

    [HttpPost("/v1/forecast/student")]
    public IResult Forecast([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var studentId = Str(el, "studentId", "demo-student-a");
        return Store.Lock(() =>
        {
            if (!Store.Students.TryGetValue(studentId, out var student)) return NotFound("学生不存在");
            var current = student.Mastery.Values.Count == 0 ? 0 : student.Mastery.Values.Average(m => m.Score);
            var series = student.Attempts.OrderBy(a => a.OccurredAt)
                .Select(a => (a.OccurredAt, a.Correct ? 85.0 : 45.0)).ToList();
            var velocity = series.Count >= 2 ? LearningVelocity.Fit(new VelocityInput(series, 0.3)).SlopePerDay : 0;
            var openImpact = student.DebtEdges.Where(d => d.Status != "cleared").Sum(d => d.Impact);
            var r = KnowledgeForecast.Forecast(new ForecastInput(current, velocity, Int(el, "daysAhead", 14), openImpact));
            return HttpResults.Success(new
            {
                studentId, daysAhead = Int(el, "daysAhead", 14),
                expectedScore = r.ExpectedScore, lowerBound = r.LowerBound, upperBound = r.UpperBound,
                band = r.Band, suppressed = r.Suppressed, note = r.Note,
                ethics = "仅区间与建议；不输出不及格概率，不作为处分或评价依据。"
            });
        });
    }
}

// ═══════════ §19.10 lab-bench-svc（内部）══════════
[ApiController]
public sealed class LabBenchController : ControllerBase
{
    [HttpPost("/internal/v1/lab/experiments")]
    public IResult Create([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var name = Str(el, "name", "");
        if (string.IsNullOrWhiteSpace(name))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "name 必填");
        return HttpResults.Created(new LabExperiment($"lab-{Guid.NewGuid():N}"[..12], name,
            Str(el, "candidateVersion", "candidate-v1"), Num(el, "tolerance", 1e-6)));
    }

    [HttpPost("/internal/v1/lab/experiments/{id}/run")]
    public IResult Run(string id, [FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var cases = new List<(double, double)>();
        if (Prop(el, "cases", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray()) cases.Add((Num(c, "expected", 0), Num(c, "actual", 0)));
        if (cases.Count == 0)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "cases 不能为空");
        return HttpResults.Success(LabBench.Run(id, cases, Num(el, "tolerance", 1e-6)));
    }

    [HttpGet("/internal/v1/lab/experiments/{id}/report")]
    public IResult Report(string id) => HttpResults.Success(new
    {
        experimentId = id,
        baselineFormulaVersion = LabBench.BaselineFormulaVersion,
        note = "演示版为请求级结果，未持久化；回归对照请以金样为准。"
    });

    [HttpPost("/internal/v1/lab/formula-versions")]
    public IResult Register([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var version = Str(el, "version", "");
        if (string.IsNullOrWhiteSpace(version))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "version 必填");
        return HttpResults.Created(new { version, proposer = Str(el, "proposer", ""), state = "registered" });
    }

    [HttpPost("/internal/v1/lab/formula-versions/{v}/promote")]
    public IResult Promote(string v, [FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var (ok, reason) = LabBench.Promote(v, Str(el, "proposer", ""), Str(el, "approver", ""));
        return ok ? HttpResults.Success(new { version = v, reason })
                  : HttpResults.Fail(422, ErrorCodes.ValidationError, reason);
    }

    private static bool Prop(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value);
    }

    private static string Str(JsonElement el, string name, string fallback = "")
        => Prop(el, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static double Num(JsonElement el, string name, double fallback = 0)
        => Prop(el, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
}
