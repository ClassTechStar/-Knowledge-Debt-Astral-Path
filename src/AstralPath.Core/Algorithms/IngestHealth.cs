namespace AstralPath.Core.Algorithms;

public sealed record IngestRow(string KpId, double RecentAcc, double Sev, double SelfConf);

public sealed record IngestIssue(int RowIndex, string Code, string Field, string Message);

public sealed record IngestHealthReport(int Accepted, int Rejected, IReadOnlyList<IngestIssue> Issues, int GraphVersion);

public static class IngestHealth
{
    public static IngestHealthReport Validate(IReadOnlyList<IngestRow> rows, int graphVersion, ISet<string> knownKps)
    {
        var issues = new List<IngestIssue>();
        var accepted = 0;

        if (rows.Count == 0)
        {
            issues.Add(new IngestIssue(0, "MISSING_FIELD", "rows", "导入行不能为空"));
            return new IngestHealthReport(0, 0, issues, graphVersion);
        }

        if (rows.Count > 400)
            issues.Add(new IngestIssue(-1, "OUT_OF_RANGE", "rows", "rows 数量超过 400"));

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rejected = false;

            if (string.IsNullOrWhiteSpace(row.KpId))
            {
                issues.Add(new IngestIssue(i, "MISSING_FIELD", "kpId", "kpId 缺失"));
                rejected = true;
            }
            else if (!knownKps.Contains(row.KpId))
            {
                issues.Add(new IngestIssue(i, "UNKNOWN_KP", "kpId", $"kpId={row.KpId} 不在 graph_ver={graphVersion}"));
                rejected = true;
            }

            if (row.RecentAcc is < 0 or > 1)
            {
                issues.Add(new IngestIssue(i, "OUT_OF_RANGE", "recentAcc", $"recentAcc={row.RecentAcc} 超出 [0,1]"));
                rejected = true;
            }

            if (row.Sev is < 0 or > 1)
            {
                issues.Add(new IngestIssue(i, "OUT_OF_RANGE", "sev", $"sev={row.Sev} 超出 [0,1]"));
                rejected = true;
            }

            if (row.SelfConf is < 1 or > 5 || Math.Abs(row.SelfConf - Math.Round(row.SelfConf)) > 1e-9)
            {
                issues.Add(new IngestIssue(i, "OUT_OF_RANGE", "selfConf", $"selfConf={row.SelfConf} 必须为 1..5 整数"));
                rejected = true;
            }

            if (!rejected) accepted++;
        }

        var rejectedCount = rows.Count - accepted;
        return new IngestHealthReport(accepted, rejectedCount, issues, graphVersion);
    }
}
