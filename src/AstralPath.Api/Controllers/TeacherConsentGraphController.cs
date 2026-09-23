using Microsoft.AspNetCore.Mvc;
using AstralPath.Contracts;
using AstralPath.Core.Algorithms;
using AstralPath.Core.Models;
using AstralPath.Infrastructure;

namespace AstralPath.Api.Controllers;

[ApiController]
public sealed class TeacherConsentGraphController : ControllerBase
{
    private readonly AstralPathStore _store;

    public TeacherConsentGraphController(AstralPathStore store) => _store = store;

    [HttpGet("/v1/teachers/{id}/hotspots")]
    public IResult Hotspots(string id, [FromQuery(Name = "only_consent")] bool onlyConsent = true)
    {
        return _store.Lock(() =>
        {
            // C6: teacher API always goes through consent
            var authorizedStudents = _store.Students.Values
                .Where(s =>
                {
                    var c = _store.Consents.GetValueOrDefault($"{s.StudentId}:{id}:teacher_hotspots");
                    return c != null && c.State == "granted" && c.AllowTeacher;
                })
                .Select(s => s.StudentId)
                .ToList();

            if (authorizedStudents.Count == 0)
            {
                var empty = new HotspotsResponse(id, "default", 0, true, new List<HotspotDto>(),
                    "无有效授权，教师端不可见任何班级热点（默认不可见 / fail-closed）");
                return HttpResults.Success(empty);
            }

            _store.RebuildTeacherCache(id);
            var cached = _store.TeacherCache.GetValueOrDefault(id) ?? new List<HotspotDtoHolder>();
            var hotspots = cached
                .Select(h => new HotspotDto(h.FromKp, h.ToKp, h.FromKpName, h.ToKpName, h.StudentCount, h.AvgImpact))
                .ToList();

            var suppressed = hotspots.Count > 0 && hotspots.Min(h => h.StudentCount) < 3 && hotspots.Count > 5;
            var response = new HotspotsResponse(id, "default", authorizedStudents.Count, suppressed, hotspots,
                hotspots.Count == 0 ? "已授权学生暂无开放债边" : string.Empty);
            return HttpResults.Success(response);
        });
    }

    [HttpPost("/v1/consents/{studentId}/grant")]
    public IResult Grant(string studentId, [FromBody] ConsentGrantRequest request)
    {
        return _store.Lock(() =>
        {
            var actor = string.IsNullOrWhiteSpace(request.ActorId) ? studentId : request.ActorId!;
            if (!string.Equals(actor, studentId, StringComparison.OrdinalIgnoreCase))
                return HttpResults.Fail(403, ErrorCodes.Forbidden, "仅学生本人可授权");

            var now = DateTime.UtcNow;
            var auditId = Guid.NewGuid().ToString("N");
            var key = $"{studentId}:{request.TeacherId}:{request.Purpose}";
            var consent = new Consent(studentId, request.TeacherId, "granted", true, now, null, request.Purpose, auditId, now);
            _store.Consents[key] = consent;
            _store.ConsentAudits.Add(new ConsentAudit(auditId, studentId, actor, "student", "grant", request.Purpose, now, Guid.NewGuid().ToString("N")));
            _store.RebuildTeacherCache(request.TeacherId);

            var dto = new ConsentDto(consent.StudentId, consent.TeacherId, consent.State, consent.AllowTeacher,
                consent.GrantedAt, consent.RevokedAt, consent.Purpose, consent.AuditId, consent.UpdatedAt);
            return HttpResults.Created(new ApiSuccess<ConsentDto>(dto, new { }, Guid.NewGuid().ToString("N")));
        });
    }

    [HttpPost("/v1/consents/{studentId}/revoke")]
    public IResult Revoke(string studentId, [FromBody] ConsentRevokeRequest request)
    {
        return _store.Lock(() =>
        {
            var actor = string.IsNullOrWhiteSpace(request.ActorId) ? studentId : request.ActorId!;
            if (!string.Equals(actor, studentId, StringComparison.OrdinalIgnoreCase))
                return HttpResults.Fail(403, ErrorCodes.Forbidden, "仅学生本人可撤销授权");

            var key = $"{studentId}:{request.TeacherId}:{request.Purpose}";
            if (!_store.Consents.TryGetValue(key, out var old))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "授权记录不存在");

            var now = DateTime.UtcNow;
            var auditId = Guid.NewGuid().ToString("N");
            var consent = old with { State = "revoked", AllowTeacher = false, RevokedAt = now, UpdatedAt = now, AuditId = auditId };
            _store.Consents[key] = consent;
            _store.ConsentAudits.Add(new ConsentAudit(auditId, studentId, actor, "student", "revoke", request.Purpose, now, Guid.NewGuid().ToString("N")));

            // immediate purge of teacher cache
            _store.PurgeTeacherCacheForStudent(studentId);

            var dto = new ConsentDto(consent.StudentId, consent.TeacherId, consent.State, consent.AllowTeacher,
                consent.GrantedAt, consent.RevokedAt, consent.Purpose, consent.AuditId, consent.UpdatedAt);
            return HttpResults.Success(dto);
        });
    }

    [HttpGet("/v1/consents/{studentId}")]
    public IResult Get(string studentId)
    {
        return _store.Lock(() =>
        {
            var list = _store.Consents.Values
                .Where(c => c.StudentId == studentId)
                .Select(c => new ConsentDto(c.StudentId, c.TeacherId, c.State, c.AllowTeacher,
                    c.GrantedAt, c.RevokedAt, c.Purpose, c.AuditId, c.UpdatedAt))
                .ToList();
            return HttpResults.Success(list);
        });
    }

    [HttpPost("/v1/graphs/{packId}/validate")]
    public IResult Validate(string packId)
    {
        return _store.Lock(() =>
        {
            if (!string.Equals(packId, _store.PackId, StringComparison.OrdinalIgnoreCase))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "图包不存在");
            var result = _store.Graph.Validate();
            if (!result.Ok && result.Cycles.Count > 0)
            {
                return HttpResults.Fail(422, ErrorCodes.GraphWouldCycle, "先修图存在环", new { result.Cycles, result.Issues });
            }
            var dto = new GraphValidateResultDto(result.Ok, result.NodeCount, result.EdgeCount, result.Cycles.ToList(), result.Issues.ToList());
            return HttpResults.Success(dto);
        });
    }

    [HttpPost("/v1/course-packs/{id}/publish")]
    public IResult Publish(string id)
    {
        return _store.Lock(() =>
        {
            var result = _store.Graph.Validate();
            if (result.Cycles.Count > 0)
                return HttpResults.Fail(422, ErrorCodes.GraphWouldCycle, "先修图存在环，禁止 publish", new { result.Cycles });

            var dto = new GraphVersionDto(_store.PackId, _store.Graph.GraphVersion, DateTime.UtcNow.ToString("O"),
                result.NodeCount, result.EdgeCount, result.Ok);
            return HttpResults.Created(new ApiSuccess<GraphVersionDto>(dto, new { }, Guid.NewGuid().ToString("N")));
        });
    }

    [HttpPost("/v1/what-if")]
    public IResult WhatIf([FromBody] WhatIfRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.StudentId))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "studentId 必填");
        if (string.IsNullOrWhiteSpace(request.FromKp) || string.IsNullOrWhiteSpace(request.ToKp))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "fromKp 与 toKp 必填（可传学生债边上的知识点 ID）");
        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(request.StudentId, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");

            var edge = student.DebtEdges.FirstOrDefault(d => d.FromKp == request.FromKp && d.ToKp == request.ToKp);
            var scoreFrom = request.OverrideScoreFrom ?? edge?.ScoreFrom ?? (student.Mastery.TryGetValue(request.FromKp, out var m1) ? m1.Score : 0);
            var scoreTo = request.OverrideScoreTo ?? edge?.ScoreTo ?? (student.Mastery.TryGetValue(request.ToKp, out var m2) ? m2.Score : 0);
            var freq = request.OverrideFreq ?? edge?.Freq ?? 3;
            var days = request.OverrideDays ?? edge?.DaysSinceLastError ?? 0;
            var weight = edge?.Weight ?? 1.0;

            var impact = DebtScanner.ComputeImpact(new ImpactInput(scoreFrom, scoreTo, freq, days, weight));
            var response = new WhatIfResponse(
                Math.Round(scoreFrom, 6), Math.Round(scoreTo, 6), freq, days,
                impact.Impact, impact.Detected, impact.Recency);
            return HttpResults.Success(response);
        });
    }
}


