using System.Text.Json;
using AstralPath.Core.Algorithms;
using AstralPath.Core.Models;
using Xunit;

namespace AstralPath.Core.Tests;

/// <summary>
/// 公式跨端行为对拍金样（重构评估 2.2）：eval/golden/formula-cross.json 的期望值
/// 由 Python 按契约公式独立计算（第三实现），本测试断言 **C#** 实现；
/// **JS**（index.html 内联 scoreV2/impactV2/saleStep）由 scripts/verify_formulas.py
/// 经 node 求值断言。三端任一漂移即红。
/// </summary>
public class FormulaCrossTests
{
    private static (JsonElement Root, double Tol) Load()
    {
        var path = Path.Combine(GoldenDir, "formula-cross.json");
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        return (doc.RootElement, doc.RootElement.GetProperty("tolerance").GetDouble());
    }

    private static string GoldenDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ASTRALPATH_GOLDEN_DIR");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "eval", "golden"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "eval", "golden"),
                Path.Combine(Directory.GetCurrentDirectory(), "eval", "golden")
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (Directory.Exists(full)) return full;
            }
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                var full = Path.Combine(dir, "eval", "golden");
                if (Directory.Exists(full)) return Path.GetFullPath(full);
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            throw new DirectoryNotFoundException("eval/golden not found");
        }
    }

    public static TheoryData<string> ScoreIds() => Ids("score");
    public static TheoryData<string> ImpactIds() => Ids("impact");
    public static TheoryData<string> SaleIds() => Ids("sale");

    private static TheoryData<string> Ids(string section)
    {
        var (root, _) = Load();
        var data = new TheoryData<string>();
        foreach (var c in root.GetProperty(section).EnumerateArray())
            data.Add(c.GetProperty("id").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScoreIds))]
    public void Score_Cross_Check(string id)
    {
        var (root, tol) = Load();
        var c = root.GetProperty("score").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        var input = new MasteryInput(
            c.GetProperty("att").GetInt32(),
            c.GetProperty("ok").GetInt32(),
            c.GetProperty("conf").GetDouble(),
            c.GetProperty("age").GetDouble(),
            c.GetProperty("streak").GetInt32(),
            c.GetProperty("prereq").EnumerateArray().Select(v => v.GetDouble()).ToList());
        var actual = MasteryCalculator.ComputeScore(input);
        Assert.True(Math.Abs(actual - c.GetProperty("expect").GetDouble()) <= tol,
            $"{id}: actual={actual} expect={c.GetProperty("expect").GetDouble()}");
    }

    [Theory]
    [MemberData(nameof(ImpactIds))]
    public void Impact_Cross_Check(string id)
    {
        var (root, tol) = Load();
        var c = root.GetProperty("impact").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        var input = new DebtInput(
            c.GetProperty("sf").GetDouble(),
            c.GetProperty("st").GetDouble(),
            c.GetProperty("w").GetDouble(),
            c.GetProperty("tg").GetInt32() == 1 ? EdgeType.TransferGap : EdgeType.Prerequisite,
            c.GetProperty("freq").GetInt32(),
            c.GetProperty("down").GetInt32());
        var actual = DebtScanner.ComputeImpact(input);
        Assert.True(Math.Abs(actual - c.GetProperty("expect").GetDouble()) <= tol,
            $"{id}: actual={actual} expect={c.GetProperty("expect").GetDouble()}");
    }

    [Theory]
    [MemberData(nameof(SaleIds))]
    public void Sale_Cross_Check(string id)
    {
        var (root, _) = Load();
        var c = root.GetProperty("sale").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        var state = SaleStateMachine.Create();
        foreach (var step in c.GetProperty("steps").EnumerateArray())
        {
            state = SaleStateMachine.Transition(state, new SaleAttempt(
                Correct: true,
                step[0].GetDouble(),
                step[1].GetInt32()));
        }
        var expect = c.GetProperty("expect");
        var expectStatus = expect.GetProperty("status").GetString();
        var mapped = state.Status switch
        {
            SaleStatus.Cleared => "cleared",
            SaleStatus.Repairing => "repairing",
            _ => "open"
        };
        Assert.True(mapped == expectStatus
                    && state.Streak == expect.GetProperty("streak").GetInt32()
                    && state.Attempts == expect.GetProperty("attempts").GetInt32(),
            $"{id}: actual={mapped}/{state.Streak}/{state.Attempts} expect={expectStatus}/{expect.GetProperty("streak").GetInt32()}/{expect.GetProperty("attempts").GetInt32()}");
    }
}
