using System.Text.Json;
using AstralPath.Core.Algorithms;
using AstralPath.Core.Formatting;
using AstralPath.Core.Models;
using AstralPath.Mobile.Data;

namespace AstralPath.Mobile.Services;

/// <summary>
/// 本地 SQLite 仓库：图包/题库种子 + 掌握度/债边/计划/作答/销账全量读写。
/// 全部参数化 SQL；数值只来自 Core 纯函数。
/// </summary>
public sealed class LocalStore : IDisposable
{
    private readonly AppDbContext _db;
    private readonly object _gate = new();

    public LocalStore(string dbPath)
    {
        _db = new AppDbContext(dbPath);
        SeedIfEmpty();
    }

    // ── 种子 ──────────────────────────────────────────────
    private void SeedIfEmpty()
    {
        lock (_gate)
        {
            if (CountNodes() > 0) return;
            var nodes = new (string Id, string Title, int Diff)[]
            {
                ("N1", "会计要素", 1), ("N2", "会计等式", 2), ("N3", "借贷记账法", 3),
                ("N4", "会计分录", 3), ("N5", "试算平衡", 2), ("N6", "账户结构", 2),
                ("N7", "财产清查", 2), ("N8", "财务报表", 4)
            };
            foreach (var n in nodes)
            {
                using var c = _db.CreateCommand(
                    "INSERT INTO local_kp_node(kp_id,title,course,difficulty) VALUES ($id,$t,$c,$d)");
                c.Parameters.AddWithValue("$id", n.Id);
                c.Parameters.AddWithValue("$t", n.Title);
                c.Parameters.AddWithValue("$c", "会计基础");
                c.Parameters.AddWithValue("$d", n.Diff);
                c.ExecuteNonQuery();
            }

            var edges = new (string From, string To, string Type, double W)[]
            {
                ("N1","N2","prerequisite",1.0), ("N2","N3","prerequisite",1.2),
                ("N3","N4","prerequisite",1.1), ("N4","N5","prerequisite",1.0),
                ("N2","N6","prerequisite",0.9), ("N6","N4","transfer_gap",0.8),
                ("N5","N7","prerequisite",0.9), ("N7","N8","prerequisite",1.0),
                ("N3","N5","transfer_gap",0.7)
            };
            foreach (var e in edges)
            {
                using var c = _db.CreateCommand(
                    "INSERT INTO local_kp_edge(from_kp,to_kp,edge_type,weight) VALUES ($f,$t,$ty,$w)");
                c.Parameters.AddWithValue("$f", e.From);
                c.Parameters.AddWithValue("$t", e.To);
                c.Parameters.AddWithValue("$ty", e.Type);
                c.Parameters.AddWithValue("$w", e.W);
                c.ExecuteNonQuery();
            }

            // 初始掌握：N2 前置好、N3 弱 → 命中债边
            SeedMastery("N1", 85, 1, 2);
            SeedMastery("N2", 80, 1, 2);
            SeedMastery("N3", 30, 1, 0);
            SeedMastery("N4", 35, 1, 0);
            SeedMastery("N5", 40, 1, 1);
            SeedMastery("N6", 55, 1, 1);
            SeedMastery("N7", 50, 1, 1);
            SeedMastery("N8", 45, 1, 0);

            SeedQuestion("Q01", "N2", "会计等式是：", "choice", "资产=负债+所有者权益", 2);
            SeedQuestion("Q02", "N2", "下列属于会计要素的是：", "choice", "资产", 1);
            SeedQuestion("Q03", "N3", "借贷记账法借方登记增加的是：", "choice", "资产", 2);
            SeedQuestion("Q04", "N3", "负债增加记：", "choice", "贷方", 2);
            SeedQuestion("Q05", "N4", "会计分录不包括：", "choice", "颜色", 2);
            SeedQuestion("Q06", "N4", "分录需要的三要素是：", "choice", "账户+方向+金额", 3);
            SeedQuestion("Q07", "N5", "试算平衡检查：", "choice", "借方合计=贷方合计", 2);
            SeedQuestion("Q08", "N1", "会计要素共几类：", "choice", "六类", 1);
            SeedQuestion("Q09", "N6", "账户结构通常包括：", "choice", "期初+本期发生+期末", 2);
            SeedQuestion("Q10", "N7", "财产清查属于：", "choice", "会计核算方法", 2);
            SeedQuestion("Q11", "N8", "资产负债表反映：", "choice", "财务状况", 3);
            SeedQuestion("Q12", "N3", "复式记账的记账符号是：", "choice", "借贷", 2);

            RefreshDebtsFromGraph();
        }
    }

    private int CountNodes()
    {
        using var c = _db.CreateCommand("SELECT COUNT(*) FROM local_kp_node");
        return Convert.ToInt32(c.ExecuteScalar());
    }

    private void SeedMastery(string kp, double raw, double age, int streak)
    {
        var score = MasteryCalculator.ComputeScore(raw, age, streak, PrereqScores(kp));
        using var c = _db.CreateCommand(
            "INSERT INTO local_mastery(kp_id,raw,age_days,streak,score) VALUES ($k,$r,$a,$s,$sc)");
        c.Parameters.AddWithValue("$k", kp);
        c.Parameters.AddWithValue("$r", raw);
        c.Parameters.AddWithValue("$a", age);
        c.Parameters.AddWithValue("$s", streak);
        c.Parameters.AddWithValue("$sc", score);
        c.ExecuteNonQuery();
    }

    private void SeedQuestion(string id, string kp, string stem, string kind, string correct, int diff)
    {
        using var c = _db.CreateCommand(
            "INSERT INTO local_question(id,kp_id,stem,answer_kind,correct_payload,difficulty) VALUES ($i,$k,$s,$a,$c,$d)");
        c.Parameters.AddWithValue("$i", id);
        c.Parameters.AddWithValue("$k", kp);
        c.Parameters.AddWithValue("$s", stem);
        c.Parameters.AddWithValue("$a", kind);
        c.Parameters.AddWithValue("$c", correct);
        c.Parameters.AddWithValue("$d", diff);
        c.ExecuteNonQuery();
    }

    // ── 图 / 题 ────────────────────────────────────────────
    public List<(string KpId, string Title, int Difficulty)> GetNodes()
    {
        lock (_gate)
        {
            var list = new List<(string, string, int)>();
            using var c = _db.CreateCommand("SELECT kp_id,title,difficulty FROM local_kp_node ORDER BY kp_id");
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
            return list;
        }
    }

    public List<(string From, string To, string Type, double Weight)> GetEdges()
    {
        lock (_gate)
        {
            var list = new List<(string, string, string, double)>();
            using var c = _db.CreateCommand("SELECT from_kp,to_kp,edge_type,weight FROM local_kp_edge");
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3)));
            return list;
        }
    }

    public string TitleOf(string kpId)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand("SELECT title FROM local_kp_node WHERE kp_id=$k");
            c.Parameters.AddWithValue("$k", kpId);
            return c.ExecuteScalar() as string ?? kpId;
        }
    }

    public QuestionRow? NextQuestion(string kpId, int afterIndex = 0)
    {
        lock (_gate)
        {
            var list = new List<QuestionRow>();
            using (var c = _db.CreateCommand(
                "SELECT id,kp_id,stem,answer_kind,correct_payload,difficulty FROM local_question WHERE kp_id=$k ORDER BY id"))
            {
                c.Parameters.AddWithValue("$k", kpId);
                using var r = c.ExecuteReader();
                while (r.Read())
                    list.Add(new QuestionRow(r.GetString(0), r.GetString(1), r.GetString(2),
                        r.GetString(3), r.GetString(4), r.GetInt32(5)));
            }
            if (list.Count == 0) return null;
            return list[afterIndex % list.Count];
        }
    }

    // ── 掌握度 ────────────────────────────────────────────
    public MasteryRow GetMastery(string kpId)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand(
                "SELECT raw,age_days,streak,score FROM local_mastery WHERE kp_id=$k");
            c.Parameters.AddWithValue("$k", kpId);
            using var r = c.ExecuteReader();
            if (r.Read())
                return new MasteryRow(kpId, r.GetDouble(0), r.GetDouble(1), r.GetInt32(2), r.GetDouble(3));
            return new MasteryRow(kpId, 0, 0, 0, 0);
        }
    }

    public List<MasteryRow> GetAllMastery()
        => GetNodes().Select(n => GetMastery(n.KpId)).ToList();

    private List<double> PrereqScores(string kpId)
        => GetEdges()
            .Where(e => e.To == kpId && e.Type == "prerequisite")
            .Select(e => GetMastery(e.From).Score)
            .ToList();

    public void UpsertMastery(MasteryRow m)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand(
                "INSERT INTO local_mastery(kp_id,raw,age_days,streak,score) VALUES ($k,$r,$a,$s,$sc) " +
                "ON CONFLICT(kp_id) DO UPDATE SET raw=$r,age_days=$a,streak=$s,score=$sc");
            c.Parameters.AddWithValue("$k", m.KpId);
            c.Parameters.AddWithValue("$r", m.Raw);
            c.Parameters.AddWithValue("$a", m.AgeDays);
            c.Parameters.AddWithValue("$s", m.Streak);
            c.Parameters.AddWithValue("$sc", m.Score);
            c.ExecuteNonQuery();
        }
    }

    // ── 债边 ──────────────────────────────────────────────
    public void RefreshDebtsFromGraph()
    {
        lock (_gate)
        {
            foreach (var e in GetEdges())
            {
                var from = GetMastery(e.From);
                var to = GetMastery(e.To);
                var et = e.Type == "transfer_gap" ? EdgeType.TransferGap : EdgeType.Prerequisite;
                var impact = DebtScanner.ComputeImpact(from.Score, to.Score, e.Weight, et, 3);
                var status = DebtScanner.IsHit(from.Score, to.Score, 3) ? "open" : "cleared";
                using var c = _db.CreateCommand(
                    "INSERT INTO local_debt_edge(from_kp,to_kp,impact,status,streak) VALUES ($f,$t,$i,$s,0) " +
                    "ON CONFLICT(from_kp,to_kp) DO UPDATE SET impact=$i, status=CASE WHEN local_debt_edge.status='cleared' AND $s='cleared' THEN 'cleared' ELSE $s END");
                c.Parameters.AddWithValue("$f", e.From);
                c.Parameters.AddWithValue("$t", e.To);
                c.Parameters.AddWithValue("$i", impact);
                c.Parameters.AddWithValue("$s", status);
                c.ExecuteNonQuery();
            }
        }
    }

    public List<DebtEdgeRow> GetDebts()
    {
        lock (_gate)
        {
            var list = new List<DebtEdgeRow>();
            using var c = _db.CreateCommand(
                "SELECT from_kp,to_kp,impact,status,streak FROM local_debt_edge WHERE status<>'cleared' ORDER BY impact DESC");
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new DebtEdgeRow(r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetString(3), r.GetInt32(4)));
            return list;
        }
    }

    public void SetDebtStreak(string from, string to, int streak, string status)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand(
                "UPDATE local_debt_edge SET streak=$s,status=$st WHERE from_kp=$f AND to_kp=$t");
            c.Parameters.AddWithValue("$s", streak);
            c.Parameters.AddWithValue("$st", status);
            c.Parameters.AddWithValue("$f", from);
            c.Parameters.AddWithValue("$t", to);
            c.ExecuteNonQuery();
        }
    }

    public SaleState GetSaleState(string from, string to)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand(
                "SELECT status,streak FROM local_debt_edge WHERE from_kp=$f AND to_kp=$t");
            c.Parameters.AddWithValue("$f", from);
            c.Parameters.AddWithValue("$t", to);
            using var r = c.ExecuteReader();
            if (r.Read())
            {
                var st = r.GetString(0) switch
                {
                    "cleared" => SaleStatus.Cleared,
                    "repairing" => SaleStatus.Repairing,
                    _ => SaleStatus.Open
                };
                return new SaleState(st, r.GetInt32(1), r.GetInt32(1));
            }
            return SaleStateMachine.Create();
        }
    }

    // ── 计划 ──────────────────────────────────────────────
    public List<PlanItem> GetPlan(int day = 0)
    {
        lock (_gate)
        {
            var list = new List<PlanItem>();
            using var c = _db.CreateCommand(
                "SELECT id,day,kp_id,layer,minutes,status FROM local_plan_item " +
                (day > 0 ? "WHERE day=$d " : "") + "ORDER BY day,id");
            if (day > 0) c.Parameters.AddWithValue("$d", day);
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new PlanItem(r.GetInt32(0), r.GetInt32(1), r.GetString(2),
                    r.GetString(3), r.GetInt32(4), r.GetString(5)));
            return list;
        }
    }

    public void ReplacePlan(IReadOnlyList<PlanItem> items)
    {
        lock (_gate)
        {
            using (var del = _db.CreateCommand("DELETE FROM local_plan_item"))
                del.ExecuteNonQuery();
            foreach (var i in items)
            {
                using var c = _db.CreateCommand(
                    "INSERT INTO local_plan_item(day,kp_id,layer,minutes,status) VALUES ($d,$k,$l,$m,$s)");
                c.Parameters.AddWithValue("$d", i.Day);
                c.Parameters.AddWithValue("$k", i.KpId);
                c.Parameters.AddWithValue("$l", i.Layer);
                c.Parameters.AddWithValue("$m", i.Minutes);
                c.Parameters.AddWithValue("$s", i.Status);
                c.ExecuteNonQuery();
            }
        }
    }

    public void CompletePlanItem(int id)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand("UPDATE local_plan_item SET status='done' WHERE id=$i");
            c.Parameters.AddWithValue("$i", id);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>生成计划：先修 layer=core 且日更早，K1–K5 由 Core 校验。</summary>
    public IReadOnlyList<PlanItem> BuildAndSavePlan(int horizon = 14, int dayBudget = 40)
    {
        var debts = GetDebts()
            .Where(d => d.Status != "cleared")
            .Select(d => (d.FromKp, d.ToKp, d.Impact))
            .ToList();
        var titles = GetNodes().ToDictionary(n => n.KpId, n => n.Title);
        var plan = new LocalPlanService().Build(debts, titles, horizon, dayBudget);
        // 补齐覆盖：每个 open 债边两端至少各有一条
        var open = GetDebts().Select(d => (d.FromKp, d.ToKp)).ToList();
        var chk = new LocalPlanService().Validate(plan, open);
        if (!chk.ConstraintsChecked)
        {
            // 最小修复：按 open 债边补 core/challenge 交错任务
            var extra = new List<PlanItem>();
            var nextId = plan.Count + 1;
            var day = 1;
            foreach (var (f, t) in open)
            {
                if (!plan.Any(p => p.KpId == f))
                    extra.Add(new PlanItem(nextId++, day, f, "core", 20, "todo"));
                if (!plan.Any(p => p.KpId == t))
                    extra.Add(new PlanItem(nextId++, Math.Min(horizon, day + 1), t, "challenge", 15, "todo"));
                day = Math.Min(horizon, day + 2);
            }
            plan = plan.Concat(extra).ToList();
        }
        ReplacePlan(plan);
        return plan;
    }

    // ── 作答 + 销账 ────────────────────────────────────────
    public sealed record AttemptResult(
        string KpId, bool Correct, int SelfConf, double ScoreBefore, double ScoreAfter,
        int SaleStreak, string SaleStatus, bool Cleared, string Feedback);

    public AttemptResult SubmitAttempt(string kpId, bool correct, int selfConf)
    {
        lock (_gate)
        {
            var before = GetMastery(kpId);
            var prereq = PrereqScores(kpId);
            var (next, _) = new LocalAttemptService().Apply(
                before, SaleStateMachine.Create(), prereq, correct, selfConf, DateTime.UtcNow);
            UpsertMastery(next);

            using (var ins = _db.CreateCommand(
                "INSERT INTO local_attempt(id,kp_id,correct,self_conf,created_at) VALUES ($i,$k,$c,$f,$t)"))
            {
                ins.Parameters.AddWithValue("$i", Guid.NewGuid().ToString("N"));
                ins.Parameters.AddWithValue("$k", kpId);
                ins.Parameters.AddWithValue("$c", correct ? 1 : 0);
                ins.Parameters.AddWithValue("$f", selfConf);
                ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                ins.ExecuteNonQuery();
            }

            // 推进相关债边销账状态机
            var cleared = false;
            var saleStreak = 0;
            var saleStatus = "open";
            foreach (var e in GetEdges().Where(e => e.To == kpId || e.From == kpId))
            {
                var st = GetSaleState(e.From, e.To);
                var attempt = new SaleAttempt(correct, correct ? 1.0 : 0.0, selfConf);
                var nextSt = SaleStateMachine.Transition(st, attempt);
                SetDebtStreak(e.From, e.To, nextSt.Streak, nextSt.Status.ToString().ToLowerInvariant());
                if (nextSt.Status == SaleStatus.Cleared) cleared = true;
                if (e.To == kpId)
                {
                    saleStreak = nextSt.Streak;
                    saleStatus = nextSt.Status.ToString().ToLowerInvariant();
                }
            }

            RefreshDebtsFromGraph();
            var fb = correct
                ? (cleared ? "已销账" : $"正确 · 掌握度 {NumberFormatter.FormatScore(next.Score)}")
                : (saleStreak > 0
                    ? $"错误 · 再完成 1 次达标小测（acc≥0.7 且 conf≥3）"
                    : $"错误 · 掌握度 {NumberFormatter.FormatScore(next.Score)}");
            return new AttemptResult(kpId, correct, selfConf, before.Score, next.Score,
                saleStreak, saleStatus, cleared, fb);
        }
    }

    // ── 导出 / 重置 ────────────────────────────────────────
    public string ExportJson(string path)
    {
        lock (_gate)
        {
            var payload = new
            {
                nodes = GetNodes().Select(n => new { n.KpId, n.Title, n.Difficulty }),
                edges = GetEdges(),
                mastery = GetAllMastery(),
                debts = GetDebts(),
                plan = GetPlan(),
                exportedAt = DateTime.UtcNow
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return path;
        }
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            foreach (var t in new[] { "local_attempt", "local_plan_item", "local_debt_edge", "local_mastery",
                         "local_question", "local_kp_edge", "local_kp_node" })
            {
                using var c = _db.CreateCommand($"DELETE FROM {t}");
                c.ExecuteNonQuery();
            }
            SeedIfEmpty();
        }
    }

    public void Dispose() => _db.Dispose();
}
