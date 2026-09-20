using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using AstralPath.Contracts;
using AstralPath.Core.Algorithms;
using AstralPath.Core.Formula;
using AstralPath.Core.Models;
using AstralPath.Infrastructure;

namespace AstralPath.Api.Controllers;

public sealed record TodayRequest(int? Day = null);

[ApiController]
public sealed class CoachProgressController : ControllerBase
{
    private readonly AstralPathStore _store;

    public CoachProgressController(AstralPathStore store) => _store = store;

    [HttpPost("/v1/students/{id}/today")]
    public IResult Today(string id, [FromBody] TodayRequest? body = null)
    {
        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(id, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");

            var day = body?.Day ?? 1;
            if (body?.Day is null && student.ActivePlan != null)
                day = Math.Clamp(student.CurrentDay, 1, Math.Max(1, student.ActivePlan.Days.Count));
            day = Math.Clamp(day, 1, 14);
            student.CurrentDay = day;

            var tasks = new List<TodayTaskDto>();
            var coachMessage = "今天按计划完成关键小步骤即可。";
            string? materialName = null;

            // 优先：教材真题题库（按用户示例 PDF 正文知识点出题）
            var firstMaterial = MaterialRegistry.ListMaterials().FirstOrDefault();
            if (firstMaterial != null)
            {
                var bankTasks = TextbookQuestionBank.ToTodayTasks(firstMaterial.Name, 6);
                if (bankTasks.Count > 0)
                {
                    materialName = firstMaterial.Name;
                    tasks.AddRange(bankTasks.Take(6));
                    coachMessage = $"今天先完成《{firstMaterial.Name}》相关真题，再回到课程债边。";
                }
            }

            // 其次：解析管线生成的任务
            if (tasks.Count == 0)
            {
                var (bookName, bookTasks) = MaterialRegistry.GetLatestTasks();
                if (bookTasks.Count > 0)
                {
                    materialName = bookName;
                    tasks.AddRange(bookTasks.Take(4));
                    coachMessage = $"今天先围绕《{bookName}》完成关键小步骤，再回到课程债边。";
                }
            }

            if (student.ActivePlan?.Days.Count > 0)
            {
                var dayItems = student.ActivePlan.Days.FirstOrDefault(d => d.Day == day)?.Items
                               ?? student.ActivePlan.Days[0].Items;
                var qIndex = 0;
                foreach (var item in dayItems.Where(i => i.Status != "dropped"))
                {
                    var q = PickQuestion(item.KpId, qIndex++);
                    var conf = student.Mastery.TryGetValue(item.KpId, out var m2) ? (int)Math.Round(m2.SelfConf) : 3;
                    var acc = student.Mastery.TryGetValue(item.KpId, out var m) ? m.RecentAcc : 0.5;
                    var decision = CoachRules.Decide(acc, conf, 0, item.KpName);
                    if (decision.ShowMessage && !string.IsNullOrEmpty(decision.Message))
                        coachMessage = decision.Message;

                    tasks.Add(new TodayTaskDto(
                        Guid.NewGuid().ToString("N"),
                        item.KpId,
                        item.KpName,
                        decision.Action == "skip_easy" ? "review" : item.Type,
                        decision.Action == "downgrade" ? Math.Max(1, item.Difficulty - 1) : item.Difficulty,
                        item.EstMin,
                        item.Why,
                        q.Id,
                        q.Stem,
                        q.Options,
                        q.CorrectIndex,
                        item.Id));
                }
            }

            if (tasks.Count == 0)
            {
                foreach (var debt in student.DebtEdges.OrderByDescending(d => d.Impact).Take(3))
                {
                    var q = PickQuestion(debt.FromKp, tasks.Count);
                    tasks.Add(new TodayTaskDto(
                        Guid.NewGuid().ToString("N"), debt.FromKp, debt.FromKpName, "concept", 2, 8,
                        $"为还 {debt.FromKpName} → {debt.ToKpName} 的债",
                        q.Id, q.Stem, q.Options, q.CorrectIndex, null));
                }
                coachMessage = "今天先从最影响的先修概念开始，保持轻量节奏。";
            }

            tasks = tasks.Take(8).ToList();
            var response = new TodayResponse(id, day, DateTime.UtcNow.Date, tasks.Sum(t => t.EstMin), coachMessage, tasks);
            return HttpResults.Success(new
            {
                studentId = id,
                day,
                date = DateTime.UtcNow.Date,
                totalMinutes = tasks.Sum(t => t.EstMin),
                coachMessage,
                material = materialName,
                tasks
            });
        });
    }

    private AstralPathStore.QuestionBankItem PickQuestion(string kpId, int index)
    {
        var candidates = _store.Questions.Values.Where(q => q.KpId == kpId).ToList();
        if (candidates.Count == 0)
            candidates = _store.Questions.Values.ToList();
        return candidates[index % candidates.Count];
    }

    [HttpPost("/v1/attempts")]
    public IResult CreateAttempt([FromBody] CreateAttemptRequest request)
    {
        return _store.Lock(() =>
        {
            if (string.IsNullOrWhiteSpace(request.StudentId))
                return HttpResults.Fail(400, ErrorCodes.ValidationError, "studentId 必填");
            if (request.SelfConf is < 1 or > 5)
                return HttpResults.Fail(400, ErrorCodes.ValidationError, "selfConf 必须为 1..5");

            var student = _store.EnsureStudent(request.StudentId);
            var question = _store.Questions.GetValueOrDefault(request.QuestionId);
            var stem = question?.Stem ?? request.QuestionId;
            var stemHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stem))).ToLowerInvariant();

            var attempt = new Attempt(
                Guid.NewGuid().ToString("N"),
                request.StudentId,
                request.PlanItemId,
                request.KpId,
                request.QuestionId,
                stemHash,
                "choice",
                request.Correct,
                request.SelfConf,
                request.LatencyMs,
                request.HintsUsed,
                DateTime.UtcNow,
                DateTime.UtcNow);
            student.Attempts.Add(attempt);

            if (student.MasteryInputs.TryGetValue(request.KpId, out var row))
            {
                var newAcc = (row.RecentAcc * 9 + (request.Correct ? 1.0 : 0.0)) / 10.0;
                var newSev = Math.Clamp(row.Sev * (request.Correct ? 0.9 : 1.05), 0, 1);
                student.MasteryInputs[request.KpId] = row with
                {
                    RecentAcc = Math.Round(newAcc, 6),
                    Sev = Math.Round(newSev, 6),
                    SelfConf = request.SelfConf
                };
                _store.RecomputeMastery(request.StudentId);
            }

            string? relatedFrom = null, relatedTo = null;
            if (request.PlanItemId != null && student.ActivePlan != null)
            {
                foreach (var day in student.ActivePlan.Days)
                {
                    var item = day.Items.FirstOrDefault(i => i.Id == request.PlanItemId);
                    if (item != null && item.DebtRef.Count >= 2)
                    {
                        relatedFrom = item.DebtRef[0];
                        relatedTo = item.DebtRef[1];
                        break;
                    }
                }
            }

            if (relatedFrom != null && relatedTo != null)
            {
                var key = $"{relatedFrom}->{relatedTo}";
                if (!student.SaleHistory.TryGetValue(key, out var history))
                    history = new List<SaleProbe>();
                history.Add(new SaleProbe(request.Correct ? 0.85 : 0.4, request.SelfConf));
                student.SaleHistory[key] = history;

                var sale = SaleStateMachine.IsSaleable(history);
                var edgeIdx = student.DebtEdges.FindIndex(d => d.FromKp == relatedFrom && d.ToKp == relatedTo);
                if (edgeIdx >= 0)
                {
                    var edge = student.DebtEdges[edgeIdx];
                    // ONLY progress-svc writes cleared (C8)
                    if (sale.Saleable && SaleStateMachine.CanWriteCleared("progress-svc"))
                    {
                        student.DebtEdges[edgeIdx] = edge with
                        {
                            Status = "cleared",
                            SaleStreak = sale.Streak,
                            UpdatedAt = DateTime.UtcNow
                        };
                    }
                    else
                    {
                        student.DebtEdges[edgeIdx] = edge with { SaleStreak = sale.Streak, UpdatedAt = DateTime.UtcNow };
                    }
                }
            }

            var dto = new AttemptDto(attempt.Id, attempt.StudentId, attempt.PlanItemId, attempt.KpId,
                attempt.QuestionId, attempt.StemHash, attempt.Correct, attempt.SelfConf, attempt.LatencyMs, attempt.OccurredAt);
            return HttpResults.Created(dto);
        });
    }

    [HttpPost("/v1/debt-edges/sale-check")]
    public IResult SaleCheck([FromBody] SaleCheckRequest request)
    {
        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(request.StudentId, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");

            var key = $"{request.FromKp}->{request.ToKp}";
            var edge = student.DebtEdges.FirstOrDefault(d => d.FromKp == request.FromKp && d.ToKp == request.ToKp);
            if (edge == null)
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "债边不存在");

            var history = student.SaleHistory.GetValueOrDefault(key) ?? new List<SaleProbe>();
            if (history.Count == 0)
            {
                history = student.Attempts
                    .Where(a => a.KpId == request.FromKp || a.KpId == request.ToKp)
                    .OrderBy(a => a.OccurredAt)
                    .Select(a => new SaleProbe(a.Correct ? 0.85 : 0.4, a.SelfConf))
                    .ToList();
            }

            var sale = SaleStateMachine.IsSaleable(history);
            var impact = DebtScanner.ComputeImpact(new ImpactInput(edge.ScoreFrom, edge.ScoreTo, edge.Freq, edge.DaysSinceLastError, edge.Weight));

            var status = edge.Status;
            if (sale.Saleable && status is "open" or "repairing")
            {
                var idx = student.DebtEdges.IndexOf(edge);
                student.DebtEdges[idx] = edge with { Status = "cleared", SaleStreak = sale.Streak, UpdatedAt = DateTime.UtcNow };
                status = "cleared";
                _store.RebuildTeacherCache("demo-teacher");
            }

            var response = new SaleCheckResponse(
                status == "cleared",
                status == "cleared" ? null : sale.Need,
                sale.Streak,
                Math.Round(impact.Impact, 6),
                status);
            return HttpResults.Success(response);
        });
    }

    [HttpPost("/v1/demo/advance")]
    public IResult Advance([FromBody] Dictionary<string, object>? body)
    {
        return _store.Lock(() =>
        {
            var studentId = body != null && body.TryGetValue("studentId", out var sid) ? sid?.ToString() : "demo-student-a";
            if (studentId == null || !_store.Students.TryGetValue(studentId, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");

            student.CurrentDay = Math.Clamp(student.CurrentDay + 1, 1, 14);

            if (student.CurrentDay >= 7)
            {
                foreach (var kp in new[] { "K03", "K05" })
                {
                    if (student.MasteryInputs.TryGetValue(kp, out var row))
                    {
                        student.MasteryInputs[kp] = row with
                        {
                            RecentAcc = Math.Min(1, row.RecentAcc + 0.15),
                            Sev = Math.Max(0, row.Sev - 0.2),
                            SelfConf = Math.Min(5, row.SelfConf + 1)
                        };
                    }
                }
                _store.RecomputeMastery(studentId);
                _store.ScanDebts(studentId);

                var open = student.DebtEdges.Where(d => d.Status != "cleared").OrderBy(d => d.Impact).FirstOrDefault();
                if (open != null)
                {
                    var idx = student.DebtEdges.IndexOf(open);
                    student.DebtEdges[idx] = open with
                    {
                        Status = "cleared",
                        Impact = Math.Round(open.Impact * 0.4, 6),
                        UpdatedAt = DateTime.UtcNow
                    };
                }
                _store.RebuildTeacherCache("demo-teacher");
            }

            var cleared = student.DebtEdges.Count(d => d.Status == "cleared");
            return HttpResults.Success(new
            {
                studentId,
                day = student.CurrentDay,
                cleared,
                open = student.DebtEdges.Count(d => d.Status == "open" || d.Status == "repairing"),
                debts = student.DebtEdges.Select(d => new { d.FromKp, d.ToKp, d.Impact, d.Status })
            });
        });
    }

    [HttpGet("/v1/questions/{id}")]
    public IResult Question(string id)
    {
        return _store.Lock(() =>
        {
            if (!_store.Questions.TryGetValue(id, out var q))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "题目不存在");
            return HttpResults.Success(new
            {
                q.Id,
                q.KpId,
                q.Stem,
                q.Options,
                q.StemHash
            });
        });
    }
}
