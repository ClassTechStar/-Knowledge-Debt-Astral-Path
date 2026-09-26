using System.Text.Json;
using AstralPath.Core.Algorithms;
using AstralPath.Core.Formula;
using AstralPath.Core.Models;
using AstralPath.Graph;

namespace AstralPath.Infrastructure;

public sealed class StudentState
{
    public required string StudentId { get; init; }
    public required string DisplayName { get; init; }
    public required string DemoGroup { get; init; } // A | B
    public Dictionary<string, IngestRow> MasteryInputs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, MasteryRecord> Mastery { get; } = new(StringComparer.Ordinal);
    public List<DebtEdge> DebtEdges { get; } = new();
    public List<Attempt> Attempts { get; } = new();
    public PlanDtoHolder? ActivePlan { get; set; }
    public int CurrentDay { get; set; } = 1;
    public Dictionary<string, int> SaleStreak { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<SaleProbe>> SaleHistory { get; } = new(StringComparer.Ordinal);
}

public sealed record PlanDtoHolder
{
    public required string Id { get; init; }
    public required string StudentId { get; init; }
    public required int GraphVersion { get; init; }
    public required int DayBudgetMin { get; init; }
    public required bool ConstraintsChecked { get; init; }
    public required IReadOnlyList<Core.Planner.ConstraintViolation> Violations { get; init; }
    public required IReadOnlyList<Core.Planner.PlanDayInput> Days { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}

/// <summary>竞赛演示用内存仓库；单权威写入路径按服务职责划分。</summary>
public sealed class AstralPathStore : IAstralPathStore
{
    private readonly object _gate = new();
    public KnowledgeGraph Graph { get; private set; }
    public Dictionary<string, StudentState> Students { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Consent> Consents { get; } = new(StringComparer.Ordinal);
    public List<ConsentAudit> ConsentAudits { get; } = new();
    public Dictionary<string, List<HotspotDtoHolder>> TeacherCache { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, QuestionBankItem> Questions { get; } = new(StringComparer.Ordinal);
    public string PackId { get; private set; } = "accounting-v1";

    public AstralPathStore(string? graphPackDirectory = null)
    {
        var dir = graphPackDirectory ?? FindGraphPack();
        var pack = KnowledgeGraph.LoadFromDirectory(dir);
        Graph = new KnowledgeGraph(pack);
        PackId = pack.PackId;
        SeedQuestions();
        SeedDemoStudents();
    }

    public static string FindGraphPack()
    {
        // 从安装目录逐级向上回溯（含自身）：安装态第一层命中 {app}\graph-packs，
        // 开发态覆盖「bin/…/net10.0 → 仓库根」。不硬编码任何机器特定路径（审计 C4 同源修复）。
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var full = Path.GetFullPath(Path.Combine(dir, "graph-packs", "accounting-v1"));
            if (File.Exists(Path.Combine(full, "nodes.json"))) return full;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        var cwd = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "graph-packs", "accounting-v1"));
        if (File.Exists(Path.Combine(cwd, "nodes.json"))) return cwd;
        throw new DirectoryNotFoundException("graph-packs/accounting-v1 not found");
    }

    /// <summary>
    /// 每次 <see cref="Lock(Action)"/> / <see cref="Lock{T}(Func{T})"/> 退出后回调（在锁**外**执行）。
    /// 宿主用它挂上「状态已变更 → 安排快照保存」，从而不必改任何控制器的写路径。
    /// </summary>
    public Action? AfterLock { get; set; }

    public void Lock(Action action)
    {
        lock (_gate) action();
        AfterLock?.Invoke();
    }

    public T Lock<T>(Func<T> fn)
    {
        T result;
        lock (_gate) result = fn();
        AfterLock?.Invoke();
        return result;
    }

    // ── 运行时状态快照（P1：重启不丢）────────────────────
    /// <summary>
    /// 导出可变业务状态。刻意只包含**需要持久化**的部分：
    /// 图与题库来自图包/种子（可重建），教师热点缓存是派生数据。
    /// </summary>
    public StoreStateDto ExportState() => Lock(() => new StoreStateDto
    {
        Students = Students.Values.Select(s => new StudentStateDto
        {
            StudentId = s.StudentId,
            DisplayName = s.DisplayName,
            DemoGroup = s.DemoGroup,
            MasteryInputs = new Dictionary<string, IngestRow>(s.MasteryInputs, StringComparer.Ordinal),
            Mastery = new Dictionary<string, MasteryRecord>(s.Mastery, StringComparer.Ordinal),
            DebtEdges = s.DebtEdges.ToList(),
            Attempts = s.Attempts.ToList(),
            ActivePlan = s.ActivePlan,
            CurrentDay = s.CurrentDay,
            SaleStreak = new Dictionary<string, int>(s.SaleStreak, StringComparer.Ordinal),
            SaleHistory = s.SaleHistory.ToDictionary(
                kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal)
        }).ToList(),
        Consents = Consents.Values.ToList(),
        ConsentAudits = ConsentAudits.ToList()
    });

    /// <summary>
    /// 合并导入（upsert，不清空现有数据）：这样 seed 出来的演示学生与演示账号
    /// 不会因为「快照里没有」而被删掉。
    /// </summary>
    public void ImportState(StoreStateDto? state)
    {
        if (state is null) return;
        Lock(() =>
        {
            foreach (var dto in state.Students)
            {
                if (string.IsNullOrWhiteSpace(dto.StudentId)) continue;
                var target = EnsureStudent(dto.StudentId);
                // 注意：DisplayName / DemoGroup 是 init-only（构造后不可改），
                // 演示学生的取值由 SeedDemoStudents 决定；这里只回填可变的学习状态。

                target.MasteryInputs.Clear();
                foreach (var kv in dto.MasteryInputs) target.MasteryInputs[kv.Key] = kv.Value;
                target.Mastery.Clear();
                foreach (var kv in dto.Mastery) target.Mastery[kv.Key] = kv.Value;

                target.DebtEdges.Clear();
                target.DebtEdges.AddRange(dto.DebtEdges);
                target.Attempts.Clear();
                target.Attempts.AddRange(dto.Attempts);

                target.ActivePlan = dto.ActivePlan;
                target.CurrentDay = dto.CurrentDay > 0 ? dto.CurrentDay : target.CurrentDay;

                target.SaleStreak.Clear();
                foreach (var kv in dto.SaleStreak) target.SaleStreak[kv.Key] = kv.Value;
                target.SaleHistory.Clear();
                foreach (var kv in dto.SaleHistory) target.SaleHistory[kv.Key] = kv.Value.ToList();
            }

            foreach (var c in state.Consents)
                Consents[ConsentKey(c.StudentId, c.TeacherId, c.Purpose)] = c;

            ConsentAudits.Clear();
            ConsentAudits.AddRange(state.ConsentAudits);
        });
    }

    /// <summary>与控制器既有约定保持一致：{studentId}:{teacherId}:{purpose}</summary>
    private static string ConsentKey(string studentId, string teacherId, string purpose)
        => $"{studentId}:{teacherId}:{purpose}";

    public void RecomputeMastery(string studentId)
    {
        var student = Students[studentId];
        var now = DateTime.UtcNow;
        foreach (var (kp, row) in student.MasteryInputs)
        {
            var result = ScoreCalculator.Compute(new ScoreInput(row.RecentAcc, row.Sev, row.SelfConf));
            student.Mastery[kp] = new MasteryRecord(
                studentId, kp, Graph.GraphVersion,
                row.RecentAcc, row.Sev, row.SelfConf,
                result.Score, result.Band,
                student.Attempts.Count(a => a.KpId == kp),
                student.Attempts.Where(a => a.KpId == kp).Select(a => (DateTime?)a.OccurredAt).Max(),
                FormulaWeights.ScoreVersion,
                now);
        }
    }

    public IReadOnlyList<ScannedDebtEdge> ScanDebts(string studentId, int topN = 5)
    {
        var student = Students[studentId];
        var inputs = new List<(string, string, string, string, double, double, int, int, double)>();
        foreach (var edge in Graph.Edges)
        {
            if (!student.Mastery.TryGetValue(edge.From, out var fromM)) continue;
            if (!student.Mastery.TryGetValue(edge.To, out var toM)) continue;

            var freq = student.DebtEdges
                .Where(d => d.FromKp == edge.From && d.ToKp == edge.To)
                .Select(d => d.Freq)
                .DefaultIfEmpty(0)
                .Max();

            // derive freq from low toKp accuracy if not stored
            if (freq == 0)
            {
                var toRow = student.MasteryInputs.GetValueOrDefault(edge.To);
                if (toRow != null && toRow.RecentAcc < 0.5 && fromM.Score < 40)
                    freq = Math.Max(1, (int)Math.Round((0.5 - toRow.RecentAcc) * 12));
            }

            var days = 0;
            inputs.Add((
                edge.From, edge.To,
                Graph.TryGetNode(edge.From, out var fn) ? fn.Name : edge.From,
                Graph.TryGetNode(edge.To, out var tn) ? tn.Name : edge.To,
                fromM.Score, toM.Score, freq, days, edge.Weight));
        }

        var scanned = DebtScannerV1.Scan(inputs, topN);
        // persist
        var now = DateTime.UtcNow;
        foreach (var s in scanned)
        {
            var existing = student.DebtEdges.FirstOrDefault(d => d.FromKp == s.FromKp && d.ToKp == s.ToKp);
            var status = existing?.Status ?? "open";
            if (existing != null) student.DebtEdges.Remove(existing);
            student.DebtEdges.Add(new DebtEdge(
                studentId, s.FromKp, s.ToKp, s.FromKpName, s.ToKpName,
                Graph.GraphVersion, FormulaWeights.WeightVersion,
                s.ScoreFrom, s.ScoreTo, s.Freq, s.DaysSinceLastError,
                s.Recency, s.Weight, s.Impact, status,
                existing?.SaleStreak ?? 0,
                existing?.DetectedAt ?? now,
                now,
                FormulaWeights.ImpactVersion));
        }

        // also keep non-detected edges out of active list for cleanliness
        student.DebtEdges.RemoveAll(d => d.Impact <= 0);
        return scanned;
    }

    public StudentState EnsureStudent(string studentId)
    {
        if (Students.TryGetValue(studentId, out var s)) return s;
        var state = new StudentState
        {
            StudentId = studentId,
            DisplayName = studentId,
            DemoGroup = "A"
        };
        Students[studentId] = state;
        return state;
    }

    private void SeedQuestions()
    {
        var items = new (string KpId, string Stem, string[] Options, int Correct)[]
        {
            ("K03", "借贷记账法的记账规则是？", new[] { "有借必有贷，借贷必相等", "有收必有付", "先借后贷", "只记借方" }, 0),
            ("K05", "下列属于复合会计分录的是？", new[] { "一借一贷", "一借多贷", "只借不贷", "无借贷" }, 1),
            ("K06", "试算平衡的依据是？", new[] { "资产=负债+所有者权益", "收入-费用=利润", "借贷记账规则", "权责发生制" }, 2),
            ("K02", "会计等式是？", new[] { "资产=负债+所有者权益", "收入=费用", "资产=费用", "负债=收入" }, 0),
            ("K33", "权责发生制强调？", new[] { "收付实现", "收入费用归属期", "现金收付", "票据日期" }, 1),
            ("K12", "财产清查的目的是？", new[] { "调整账实差异", "增加利润", "减少负债", "编制预算" }, 0),
            ("K17", "存货计价方法不包括？", new[] { "先进先出", "加权平均", "个别计价", "现金流量法" }, 3),
            ("K20", "累计折旧属于？", new[] { "资产类备抵账户", "负债类", "所有者权益", "损益类" }, 0),
            ("K30", "资产负债表反映？", new[] { "某一日期财务状况", "一定期间经营成果", "现金流量", "预算执行" }, 0),
            ("K31", "利润表反映？", new[] { "一定期间经营成果", "某一日期财务状况", "所有者权益变动", "现金流量" }, 0),
        };

        for (var i = 0; i < items.Length; i++)
        {
            var q = items[i];
            var id = $"q-{q.KpId.ToLowerInvariant()}-{i}";
            Questions[id] = new QuestionBankItem(
                id, q.KpId, q.Stem, q.Options.ToList(), q.Correct,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(q.Stem))));
        }
    }

    private void SeedDemoStudents()
    {
        var studentAId = "demo-student-a";
        var studentBId = "demo-student-b";

        var a = new StudentState { StudentId = studentAId, DisplayName = "王小明（有债）", DemoGroup = "A" };
        var b = new StudentState { StudentId = studentBId, DisplayName = "李华（对照）", DemoGroup = "B" };

        foreach (var node in Graph.Nodes)
        {
            // Student A: weak prereqs that create debt edges
            a.MasteryInputs[node.Id] = node.Id switch
            {
                "K03" => new IngestRow(node.Id, 0.30, 0.80, 2), // 借贷记账法 weak
                "K05" => new IngestRow(node.Id, 0.35, 0.70, 2), // 会计分录 weak
                "K06" => new IngestRow(node.Id, 0.40, 0.60, 3), // 试算平衡 weakish
                "K02" => new IngestRow(node.Id, 0.28, 0.80, 2),
                "K33" => new IngestRow(node.Id, 0.42, 0.55, 3),
                "K12" => new IngestRow(node.Id, 0.38, 0.65, 2),
                "K17" => new IngestRow(node.Id, 0.45, 0.50, 3),
                _ => new IngestRow(node.Id, 0.55 + (node.Id.GetHashCode() % 20) / 100.0, 0.35, 3)
            };

            // Student B: healthy
            b.MasteryInputs[node.Id] = new IngestRow(node.Id, 0.82, 0.15, 4);
        }

        // Pre-seed freq for A on known weak edges
        var freqMap = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["K02->K03"] = 5,
            ["K03->K05"] = 6,
            ["K05->K06"] = 5,
            ["K02->K05"] = 4,
            ["K33->K30"] = 3,
            ["K03->K06"] = 4,
            ["K12->K06"] = 3,
            ["K17->K18"] = 3,
        };

        foreach (var (key, freq) in freqMap)
        {
            var parts = key.Split("->");
            a.DebtEdges.Add(new DebtEdge(
                studentAId, parts[0], parts[1],
                Graph.TryGetNode(parts[0], out var n1) ? n1.Name : parts[0],
                Graph.TryGetNode(parts[1], out var n2) ? n2.Name : parts[1],
                1, FormulaWeights.WeightVersion,
                0, 0, freq, 0, 1.0, 1.2, 0, "open", 0,
                DateTime.UtcNow, DateTime.UtcNow, FormulaWeights.ImpactVersion));
        }

        Students[studentAId] = a;
        Students[studentBId] = b;
        RecomputeMastery(studentAId);
        RecomputeMastery(studentBId);
        ScanDebts(studentAId);
        ScanDebts(studentBId);

        // Consent: A grants teacher, B does not
        var now = DateTime.UtcNow;
        var teacherId = "demo-teacher";
        var auditId = Guid.NewGuid().ToString("N");
        Consents[$"{studentAId}:{teacherId}:teacher_hotspots"] = new Consent(
            studentAId, teacherId, "granted", true, now, null, "teacher_hotspots", auditId, now);
        ConsentAudits.Add(new ConsentAudit(auditId, studentAId, studentAId, "student", "grant", "teacher_hotspots", now, Guid.NewGuid().ToString("N")));

        RebuildTeacherCache(teacherId);
    }

    public void RebuildTeacherCache(string teacherId)
    {
        var hotspots = new Dictionary<string, (string FromKp, string ToKp, string FromName, string ToName, int Count, double ImpactSum)>(StringComparer.Ordinal);
        foreach (var student in Students.Values)
        {
            var consent = Consents.GetValueOrDefault($"{student.StudentId}:{teacherId}:teacher_hotspots");
            if (consent is null || !consent.AllowTeacher || consent.State != "granted") continue;

            ScanDebts(student.StudentId);
            foreach (var edge in student.DebtEdges.Where(e => e.Status != "cleared"))
            {
                var key = $"{edge.FromKp}->{edge.ToKp}";
                if (!hotspots.TryGetValue(key, out var h))
                    h = (edge.FromKp, edge.ToKp, edge.FromKpName, edge.ToKpName, 0, 0);
                hotspots[key] = (h.FromKp, h.ToKp, h.FromName, h.ToName, h.Count + 1, h.ImpactSum + edge.Impact);
            }
        }

        TeacherCache[teacherId] = hotspots.Values
            .Where(h => h.Count > 0)
            .Select(h => new HotspotDtoHolder(h.FromKp, h.ToKp, h.FromName, h.ToName, h.Count,
                Math.Round(h.ImpactSum / h.Count, 6), h.ImpactSum))
            .OrderByDescending(h => h.AvgImpact)
            .ToList();
    }

    public void PurgeTeacherCacheForStudent(string studentId)
    {
        foreach (var key in TeacherCache.Keys.ToList())
            RebuildTeacherCache(key);
    }

}

/// <summary>题库条目（原 AstralPathStore 嵌套类型，2.3-② 接口化时提升到命名空间层）。</summary>
public sealed record QuestionBankItem(
    string Id, string KpId, string Stem, List<string> Options, int CorrectIndex, string StemHash);

public sealed record HotspotDtoHolder(
    string FromKp,
    string ToKp,
    string FromKpName,
    string ToKpName,
    int StudentCount,
    double AvgImpact,
    double ImpactSum = 0);
