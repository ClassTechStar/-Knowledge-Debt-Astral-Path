using Microsoft.AspNetCore.Mvc;
using AstralPath.Contracts;
using AstralPath.Core.Algorithms;
using AstralPath.Core.Formula;
using AstralPath.Core.Narrative;
using AstralPath.Core.Planner;
using AstralPath.Infrastructure;

namespace AstralPath.Api.Controllers;

[ApiController]
public sealed class StudentsController : ControllerBase
{
    private readonly AstralPathStore _store;
    private readonly AppServices _services;

    public StudentsController(AstralPathStore store, AppServices services)
    {
        _store = store;
        _services = services;
    }

    [HttpPost("/v1/students/{id}/ingest/scores")]
    public IResult Ingest(string id, [FromBody] IngestScoresRequest request)
    {
        return _store.Lock(() =>
        {
            if (request.Rows is null || request.Rows.Count == 0)
                return HttpResults.Fail(400, ErrorCodes.ValidationError, "rows 不能为空");

            var known = _store.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            if (request.GraphVersion != _store.Graph.GraphVersion)
            {
                // still validate rows against current graph but flag mismatch
            }

            var rows = request.Rows.Select(r => new IngestRow(r.KpId, r.RecentAcc, r.Sev, r.SelfConf)).ToList();
            var report = IngestHealth.Validate(rows, _store.Graph.GraphVersion, known);
            var issues = report.Issues
                .Select(i => new { rowIndex = i.RowIndex, code = i.Code, field = i.Field, message = i.Message })
                .ToList();

            if (report.Rejected > 0 && report.Accepted == 0)
            {
                return HttpResults.Fail(422, ErrorCodes.IngestHealthcheckFailed, "导入数据未通过体检",
                    new { report.Rejected, issues });
            }

            var student = _store.EnsureStudent(id);
            for (var i = 0; i < rows.Count; i++)
            {
                if (report.Issues.Any(x => x.RowIndex == i)) continue;
                student.MasteryInputs[rows[i].KpId] = rows[i];
            }
            _store.RecomputeMastery(id);
            _store.ScanDebts(id);

            var reportDto = new
            {
                accepted = report.Accepted,
                rejected = report.Rejected,
                issues,
                graphVersion = _store.Graph.GraphVersion
            };
            return HttpResults.Success(new { ok = report.Accepted > 0, report = reportDto, studentId = id });
        });
    }

    [HttpPost("/v1/students/{id}/diagnose")]
    [HttpGet("/v1/students/{id}/diagnose")]
    public IResult Diagnose(string id, [FromQuery(Name = "graph_ver")] int? graphVer = null, [FromQuery(Name = "top_n")] int topN = 5)
    {
        return _store.Lock(() =>
        {
            if (!_store.Students.ContainsKey(id))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");
            if (graphVer is null)
                return HttpResults.Fail(400, ErrorCodes.ValidationError, "graph_ver 必填");
            if (graphVer != _store.Graph.GraphVersion)
                return HttpResults.Fail(404, ErrorCodes.GraphVersionNotFound, "图版本不存在");

            topN = Math.Clamp(topN, 1, 20);
            var scanned = _store.ScanDebts(id, topN);
            var student = _store.Students[id];
            var topDebts = scanned.Select(s => new DebtTopItem(
                s.FromKp, s.ToKp, s.FromKpName, s.ToKpName,
                s.ScoreFrom, s.ScoreTo, s.Freq, s.Impact, "open")).ToList();

            var narratives = new List<NarrativeDto>();
            foreach (var debt in topDebts)
            {
                var slots = new NarrativeSlots(debt.FromKpName, debt.ToKpName, debt.Impact, debt.ScoreFrom, debt.ScoreTo);
                var narrative = NarrativeGuard.Build(slots);
                narratives.Add(new NarrativeDto(
                    debt.FromKp, debt.ToKp, narrative.Story, narrative.Actions.ToList(), narrative.Tone,
                    new SafetyFlagsDto(narrative.BannedHit, narrative.SlotFailed, narrative.DegradedTemplate),
                    narrative.ModelVer));
            }

            var payload = new DiagnoseResponse(
                id,
                _store.Graph.GraphVersion,
                FormulaWeights.WeightVersion,
                FormulaInfo.Default,
                topDebts,
                narratives);
            return HttpResults.Success(payload);
        });
    }

    [HttpGet("/v1/students/{id}/graph-view")]
    public IResult GraphView(string id, [FromQuery(Name = "graph_ver")] int? graphVer = null)
    {
        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(id, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");
            if (graphVer is null)
                return HttpResults.Fail(400, ErrorCodes.ValidationError, "graph_ver 必填");
            if (graphVer != _store.Graph.GraphVersion)
                return HttpResults.Fail(404, ErrorCodes.GraphVersionNotFound, "图版本不存在");

            var debts = student.DebtEdges.Where(d => d.Status != "cleared").OrderByDescending(d => d.Impact).ToList();
            var involved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in debts)
            {
                involved.Add(d.FromKp);
                involved.Add(d.ToKp);
                foreach (var n in _store.Graph.OutNeighbors(d.FromKp)) involved.Add(n);
                foreach (var e in _store.Graph.EdgesOfNode(d.FromKp).SelectMany(x => new[] { x.From, x.To })) involved.Add(e);
            }
            if (involved.Count == 0)
            {
                foreach (var n in _store.Graph.Nodes.Take(20)) involved.Add(n.Id);
            }

            var nodes = new List<GraphViewNodeDto>();
            foreach (var kpId in involved.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!_store.Graph.TryGetNode(kpId, out var node)) continue;
                student.Mastery.TryGetValue(kpId, out var mastery);
                var score = mastery?.Score ?? 0;
                nodes.Add(new GraphViewNodeDto(node.Id, node.Name, node.Course, score, ScoreCalculator.BandOf(score)));
            }

            var edges = debts.Select(d => new GraphViewEdgeDto(
                d.FromKp, d.ToKp, d.Impact, d.Status, d.Weight,
                d.FromKpName, d.ToKpName, d.ScoreFrom, d.ScoreTo, d.Freq)).ToList();

            var dto = new GraphViewDto(id, _store.Graph.GraphVersion, FormulaWeights.WeightVersion,
                nodes, edges, edges.Count, DateTime.UtcNow);
            return HttpResults.Success(dto);
        });
    }

    [HttpGet("/v1/students/{id}/mastery")]
    public IResult Mastery(string id)
    {
        return _store.Lock(() =>
        {
            if (!_store.Students.TryGetValue(id, out var student))
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "学生不存在");
            var list = student.Mastery.Values
                .OrderBy(m => m.KpId, StringComparer.Ordinal)
                .Select(m => new
                {
                    m.KpId,
                    name = _store.Graph.TryGetNode(m.KpId, out var n) ? n.Name : m.KpId,
                    m.RecentAcc,
                    m.Sev,
                    m.SelfConf,
                    m.Score,
                    m.Band
                })
                .ToList();
            return HttpResults.Success(list);
        });
    }
}

