using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AstralPath.Contracts;
using AstralPath.Core.Extensions;
using AstralPath.Infrastructure;

namespace AstralPath.Api.Controllers;

/// <summary>
/// §19 十个扩展微服务的对外接口（演示版：单进程内以纯函数服务实现，
/// 与方案 §19.11 部署清单的"独立服务"形态保持同样的**契约与拥有边界**）。
///
/// 拥有/不拥有边界严格遵循 §19.0：
///   · 只产出模拟/建议/聚合结果，不改写 mastery、debt_edges、cleared 等生产权威数据；
///   · peer-cohort / study-group / forecast 为**强伦理**服务：k-匿名、不用明文分数排名、
///     不输出可当处分依据的定性，越界即抑制（fail-closed）。
/// </summary>
[ApiController]
public sealed class ExtensionServicesController : ControllerBase
{
    private readonly AstralPathStore _store;

    public ExtensionServicesController(AstralPathStore store) => _store = store;

    private static bool TryGetProp(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        return el.TryGetProperty(name, out value);
    }

    private static double Num(JsonElement el, string name, double fallback = 0)
        => TryGetProp(el, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static int Int(JsonElement el, string name, int fallback = 0)
        => TryGetProp(el, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static string Str(JsonElement el, string name, string fallback = "")
        => TryGetProp(el, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    // ═══════════════ §19.1 concept-diffusion-svc ═══════════════

    /// <summary>
    /// 知识点传播模拟。body：{ "studentId": "demo-student-a", "intervention": {"K02": 20}, "alpha": 0.55, "depth": 6 }
    /// 不传 studentId 则使用默认演示学生；不传 intervention 则空干预（仅返回基线）。
    /// </summary>
    [HttpPost("/v1/diffusion/simulate")]
    public IResult DiffusionSimulate([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var studentId = Str(el, "studentId", "demo-student-a");
        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(studentId, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");

            var nodes = _store.Graph.Nodes.Select(n => n.Id).ToList();
            var edges = _store.Graph.Edges.Select(e => (e.From, e.To, e.EdgeType)).ToList();
            var score = student.Mastery.ToDictionary(m => m.Key, m => m.Value.Score, StringComparer.Ordinal);

            var intervention = new Dictionary<string, double>(StringComparer.Ordinal);
            if (TryGetProp(el, "intervention", out var iv) && iv.ValueKind == JsonValueKind.Object)
                foreach (var p in iv.EnumerateObject())
                    intervention[p.Name] = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetDouble() : 0;

            var debts = student.DebtEdges.Select(d => (d.FromKp, d.ToKp, d.Impact)).ToList();
            var input = new DiffusionInput(nodes, edges.Select(e => (e.From, e.To, 1.0)).ToList(), score, intervention, debts,
                Num(el, "alpha", 0.55), Int(el, "depth", 6));
            var result = ConceptDiffusion.Run(input);
            var predicted = ConceptDiffusion.PredictImpact(input, result);
            return HttpResults.Success(new
            {
                algoVersion = result.AlgoVersion,
                alpha = result.Alpha,
                depth = result.Depth,
                nodeCount = result.NodeCount,
                truncationBound = result.TruncationBound,
                gains = result.Gains,
                repairRanking = result.RepairRanking.Take(10).ToList(),
                impactPrediction = predicted
            });
        });
    }

    // ═══════════════ §19.2 exam-impact-svc ═══════════════

    /// <summary>考试影响预测 + 考前计划。body：{"daysToExam":7,"dayBudgetMin":30}</summary>
    [HttpPost("/v1/exams/{examId}/impact")]
    public IResult ExamImpactForecast(string examId, [FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var studentId = Str(el, "studentId", "demo-student-a");
        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(studentId, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");
            // ExamImpactInput 需要 (KpId, Score, Impact, Weight)：分数取自该学生掌握度快照
            var debts = student.DebtEdges
                .Where(d => d.Status != "cleared")
                .Select(d => (d.ToKp,
                    Score: student.Mastery.TryGetValue(d.ToKp, out var mv) ? mv.Score : 0.0,
                    d.Impact, d.Weight))
                .ToList();
            var input = new ExamImpactInput(examId, Int(el, "daysToExam", 7), Int(el, "dayBudgetMin", 30), debts);
            return HttpResults.Success(ExamImpact.Run(input));
        });
    }

    /// <summary>考前计划：复用 §19.2 时间盒结果，按天展开（任一天 ≤35 分钟）。</summary>
    [HttpPost("/v1/exams/{examId}/preexam-plan")]
    public IResult PreExamPlan(string examId, [FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var studentId = Str(el, "studentId", "demo-student-a");
        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(studentId, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");
            var debts = student.DebtEdges.Where(d => d.Status != "cleared")
                .Select(d => (d.ToKp,
                    Score: student.Mastery.TryGetValue(d.ToKp, out var mv) ? mv.Score : 0.0,
                    d.Impact, d.Weight)).ToList();
            var input = new ExamImpactInput(examId, Int(el, "daysToExam", 7), Int(el, "dayBudgetMin", 30), debts);
            var impact = ExamImpact.Run(input);

            // 按天展开：每天最多 35 分钟（BASELINE 约束），高优先级优先
            var days = new List<object>();
            var queue = impact.Plan.Where(p => p.SuggestedMin > 0).ToList();
            for (var day = 1; day <= impact.DaysToExam && queue.Count > 0; day++)
            {
                var remain = Math.Min(impact.DaysToExam <= 0 ? 30 : 35, 35);
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
            return HttpResults.Success(new { examId, studentId, days, note = "任一天 ≤35 分钟（BASELINE 约束）；仅建议，不改写权威计划。" });
        });
    }

    // ═══════════════ §19.3 peer-cohort-svc（强伦理）═══════════════

    /// <summary>
    /// 同辈对照聚合。body：{"k":5,"scores":[62,71,58,80,66]} 或 {"k":5,"studentId":"..."} 用演示集合。
    /// 强伦理：样本 &lt; k 即抑制，不产出分布与个体。
    /// </summary>
    [HttpPost("/v1/cohorts/stats")]
    public IResult CohortStats([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var k = Int(el, "k", PeerCohort.DefaultK);
        var members = new List<(string, double)>();

        if (TryGetProp(el, "scores", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.Number) members.Add(($"s{++i}", v.GetDouble()));
        }
        else
        {
            var course = Str(el, "course", "");
            var snapshot = _store.Lock(() => _store.Students.Values
                .Select(s => (s.StudentId, Score: s.Mastery.Values.Count == 0
                    ? 0 : s.Mastery.Values.Average(m => m.Score)))
                .ToList());
            members.AddRange(snapshot.Select((x, i) => ($"s{i + 1}", x.Score)));
        }

        var result = PeerCohort.Run(new CohortInput(k, members));
        return HttpResults.Success(new
        {
            k,
            sampleCount = result.SampleCount,
            suppressed = result.Suppressed,
            p25 = result.P25, p50 = result.P50, p75 = result.P75,
            note = result.Note,
            ethics = "仅聚合；不输出个体、不排名、不做能力定性。"
        });
    }

    // ═══════════════ §19.4 study-group-svc（强伦理）═══════════════

    /// <summary>学习小组匹配（互补优先，3–5 人）。body：{"size":4,"members":[{"studentId":"a","tags":["线代"]}]}</summary>
    [HttpPost("/v1/study-groups/match")]
    public IResult GroupMatch([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var members = new List<GroupMemberInput>();
        if (TryGetProp(el, "members", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var id = Str(m, "studentId", "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var tags = new List<string>();
                if (TryGetProp(m, "tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
                    foreach (var t in tg.EnumerateArray())
                        if (t.ValueKind == JsonValueKind.String) tags.Add(t.GetString() ?? "");
                members.Add(new GroupMemberInput(id, tags));
            }
        }
        else
        {
            // 无输入时用演示学生的债边课程作为标签（聚合标签，不用分数）
            members = _store.Lock(() => _store.Students.Values.Select(s =>
                new GroupMemberInput(s.StudentId,
                    s.DebtEdges.Select(d => d.ToKpName).Distinct(StringComparer.Ordinal).Take(5).ToList())).ToList());
        }

        if (members.Count < 2)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "成员不足 2 人，无法成组");

        var result = StudyGroup.Match(members, Int(el, "size", 4));
        return HttpResults.Success(new
        {
            groups = result.Groups,
            groupCount = result.Groups.Count,
            note = result.Note,
            ethics = "仅用聚合标签互补成组；不使用分数明文与排名。"
        });
    }

    // ═══════════════ §19.5 micro-lesson-svc ═══════════════

    /// <summary>微课装配（不产出数字结论）。body：{"kpId":"K02","kpName":"矩阵乘法","minutes":10,"resources":["…"]}</summary>
    [HttpPost("/v1/micro-lessons/assemble")]
    public IResult MicroLessonAssemble([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var kpId = Str(el, "kpId", "");
        if (string.IsNullOrWhiteSpace(kpId))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "kpId 必填");
        var resources = new List<string>();
        if (TryGetProp(el, "resources", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String) resources.Add(r.GetString() ?? "");
        var kpName = _store.Lock(() => _store.Graph.TryGetNode(kpId, out var n) ? n.Name : kpId);

        var result = MicroLesson.Assemble(new MicroLessonInput(kpId, kpName, resources, Int(el, "minutes", 10)));
        return HttpResults.Created(result);
    }

    /// <summary>微课草稿（演示版直接走本地模板，不经 LLM，避免编造数字）。</summary>
    [HttpPost("/v1/micro-lessons/draft")]
    public IResult MicroLessonDraft([FromBody] JsonElement? body) => MicroLessonAssemble(body);

    /// <summary>读取已装配微课（演示版为内存态，重启即失效）。</summary>
    [HttpGet("/v1/micro-lessons/{id}")]
    public IResult MicroLessonGet(string id)
        => HttpResults.Fail(404, ErrorCodes.ResourceNotFound,
            "演示版微课为请求级装配结果，未持久化；请调用 POST /v1/micro-lessons/assemble 重新生成。");

    // ═══════════════ §19.6 learning-velocity-svc ═══════════════

    /// <summary>学习速度建模（指数平滑 + 最小二乘斜率）。body：{"series":[{"at":"2026-09-01","score":60}, …]}</summary>
    [HttpPost("/v1/velocity/fit")]
    public IResult VelocityFit([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var series = new List<(DateTime, double)>();
        if (TryGetProp(el, "series", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in arr.EnumerateArray())
            {
                var atStr = Str(p, "at", "");
                var score = Num(p, "score", 0);
                if (DateTime.TryParse(atStr, out var at)) series.Add((at, score));
            }
        }
        else
        {
            var studentId = Str(el, "studentId", "demo-student-a");
            series = _store.Lock(() =>
            {
                if (!_store.Students.TryGetValue(studentId, out var s)) return new List<(DateTime, double)>();
                return s.Attempts.OrderBy(a => a.OccurredAt)
                    .Select(a => (a.OccurredAt, a.Correct ? 85.0 : 45.0)).ToList();
            });
            if (series.Count == 0)
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "无可用的尝试序列");
        }

        if (series.Count == 0)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "series 不能为空");

        return HttpResults.Success(LearningVelocity.Fit(new VelocityInput(series, Num(el, "alpha", 0.3))));
    }

    // ═══════════════ §19.7 spaced-review-svc ═══════════════

    /// <summary>间隔重复调度（建议态）。body：{"items":[{"kpId":"K02","lapses":0,"lastScore":70}]}</summary>
    [HttpPost("/v1/spaced-review/schedule")]
    public IResult SpacedReviewSchedule([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var items = new List<ReviewItem>();
        if (TryGetProp(el, "items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                var kp = Str(it, "kpId", "");
                if (string.IsNullOrWhiteSpace(kp)) continue;
                DateTime? last = null;
                if (TryGetProp(it, "lastReviewAt", out var lr) && lr.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(lr.GetString(), out var parsed)) last = parsed;
                items.Add(new ReviewItem(kp, Int(it, "lapses", 0), Num(it, "lastScore", 60), last));
            }
        }
        else
        {
            var studentId = Str(el, "studentId", "demo-student-a");
            items = _store.Lock(() =>
            {
                if (!_store.Students.TryGetValue(studentId, out var s)) return new List<ReviewItem>();
                return s.Mastery.Values.Select(m =>
                    new ReviewItem(m.KpId, 0, m.Score, m.LastAttemptAt)).ToList();
            });
        }

        if (items.Count == 0)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "items 不能为空且需能解析出知识点");

        var plan = SpacedReview.Schedule(items, DateTime.UtcNow);
        return HttpResults.Success(new
        {
            count = plan.Count,
            plan,
            note = "仅建议队列；不直接写 mastery / cleared。"
        });
    }

    // ═══════════════ §19.8 prerequisite-simulator-svc ═══════════════

    /// <summary>先修补全推演。body：{"targetKp":"K02","targetScore":75}</summary>
    [HttpPost("/v1/prereq-simulator/simulate")]
    public IResult PrereqSimulate([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var target = Str(el, "targetKp", "");
        if (string.IsNullOrWhiteSpace(target))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "targetKp 必填");
        var studentId = Str(el, "studentId", "demo-student-a");

        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(studentId, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");
            var edges = _store.Graph.Edges.Select(e => (e.From, e.To, e.Weight)).ToList();
            var score = student.Mastery.ToDictionary(m => m.Key, m => m.Value.Score, StringComparer.Ordinal);
            var debts = student.DebtEdges.Select(d => (d.FromKp, d.ToKp, d.Impact)).ToList();
            var input = new PrereqSimInput(edges, score, debts, target, Num(el, "targetScore", 75));
            return HttpResults.Success(PrerequisiteSimulator.Simulate(input));
        });
    }

    // ═══════════════ §19.9 knowledge-forecast-svc（强伦理）═══════════════

    /// <summary>
    /// 学业预警预测（区间 + 建议）。body：{"studentId":"demo-student-a","daysAhead":14}
    /// 强伦理：&gt;30 天外推即抑制；不输出不及格概率；不作为处分依据。
    /// </summary>
    [HttpPost("/v1/forecast/student")]
    public IResult ForecastStudent([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var studentId = Str(el, "studentId", "demo-student-a");
        var daysAhead = Int(el, "daysAhead", 14);

        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(studentId, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");

            var current = student.Mastery.Values.Count == 0 ? 0 : student.Mastery.Values.Average(m => m.Score);
            var series = student.Attempts.OrderBy(a => a.OccurredAt)
                .Select(a => (a.OccurredAt, a.Correct ? 85.0 : 45.0)).ToList();
            var velocity = series.Count >= 2
                ? LearningVelocity.Fit(new VelocityInput(series, 0.3)).SlopePerDay
                : 0;
            var openImpact = student.DebtEdges.Where(d => d.Status != "cleared").Sum(d => d.Impact);

            var result = KnowledgeForecast.Forecast(new ForecastInput(current, velocity, daysAhead, openImpact));
            return HttpResults.Success(new
            {
                studentId,
                daysAhead,
                expectedScore = result.ExpectedScore,
                lowerBound = result.LowerBound,
                upperBound = result.UpperBound,
                band = result.Band,
                suppressed = result.Suppressed,
                note = result.Note,
                ethics = "仅区间与建议；不输出不及格概率，不作为处分或评价依据。"
            });
        });
    }

    // ═══════════════ §19.10 lab-bench-svc（内部）══════════════

    /// <summary>注册实验（内部）。body：{"name":"score 候选 v2","candidateVersion":"score-v2","tolerance":1e-6}</summary>
    [HttpPost("/internal/v1/lab/experiments")]
    public IResult LabCreate([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var name = Str(el, "name", "");
        if (string.IsNullOrWhiteSpace(name))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "name 必填");
        var exp = new LabExperiment($"lab-{Guid.NewGuid():N}"[..12], name,
            Str(el, "candidateVersion", "candidate-v1"), Num(el, "tolerance", 1e-6));
        return HttpResults.Created(exp);
    }

    /// <summary>跑实验回归（内部）。body：{"cases":[{"expected":1,"actual":1.0000001}]}</summary>
    [HttpPost("/internal/v1/lab/experiments/{id}/run")]
    public IResult LabRun(string id, [FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var cases = new List<(double, double)>();
        if (TryGetProp(el, "cases", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray())
                cases.Add((Num(c, "expected", 0), Num(c, "actual", 0)));

        if (cases.Count == 0)
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "cases 不能为空");

        var result = LabBench.Run(id, cases, Num(el, "tolerance", 1e-6));
        return HttpResults.Success(result);
    }

    /// <summary>实验报告（内部）。演示版为内存态，返回 BASELINE 权重版本以便对照。</summary>
    [HttpGet("/internal/v1/lab/experiments/{id}/report")]
    public IResult LabReport(string id)
        => HttpResults.Success(new
        {
            experimentId = id,
            baselineFormulaVersion = LabBench.BaselineFormulaVersion,
            note = "演示版为请求级结果，未持久化；回归对照请以金样为准。"
        });

    /// <summary>注册候选公式版本（内部）。body：{"version":"score-v2","proposer":"P2"}</summary>
    [HttpPost("/internal/v1/lab/formula-versions")]
    public IResult LabRegisterFormula([FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var version = Str(el, "version", "");
        if (string.IsNullOrWhiteSpace(version))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "version 必填");
        return HttpResults.Created(new { version, proposer = Str(el, "proposer", ""), state = "registered" });
    }

    /// <summary>公式版本晋升（双人审批，提议人 ≠ 审批人）。body：{"version":"score-v2","proposer":"P2","approver":"P1"}</summary>
    [HttpPost("/internal/v1/lab/formula-versions/{v}/promote")]
    public IResult LabPromote(string v, [FromBody] JsonElement? body)
    {
        var el = body ?? default;
        var (ok, reason) = LabBench.Promote(v, Str(el, "proposer", ""), Str(el, "approver", ""));
        return ok ? HttpResults.Success(new { version = v, reason }) : HttpResults.Fail(422, ErrorCodes.ValidationError, reason);
    }
}
