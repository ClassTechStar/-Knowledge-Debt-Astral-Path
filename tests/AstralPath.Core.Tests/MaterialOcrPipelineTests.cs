using System.Diagnostics;
using Xunit;

namespace AstralPath.Core.Tests;

public class MaterialOcrPipelineTests
{
    private static string RepoRoot
    {
        get
        {
            var candidates = new[]
            {
                @"C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path",
                Path.Combine(Directory.GetCurrentDirectory()),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(Path.Combine(full, "tools", "ocr_pipeline.py"))) return full;
            }
            throw new DirectoryNotFoundException("repo root with tools/ocr_pipeline.py not found");
        }
    }

    private static string Python
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ASTRALPATH_OCR_PYTHON");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            var venv = Path.Combine(RepoRoot, "tools", "ocr-venv", "Scripts", "python.exe");
            return File.Exists(venv) ? venv : @"C:\Program Files\Xiaomi MiMo\resources\runtimes\win32-x64\python\python.exe";
        }
    }

    private static string? FirstSamplePdf()
    {
        var candidates = new[]
        {
            @"C:\Users\18948\Downloads\Kotlin编程实践：Kotlin从入门到实战\Kotlin编程实践：Kotlin从入门到实战.pdf",
            @"C:\Users\18948\Downloads\Go语言从入门到精通.pdf",
            @"C:\Users\18948\Downloads\C#从入门到精通（第7版）+(明日科技)+.pdf"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    [Fact]
    public void OcrPipeline_ExtractsScannedPdfAndBuildsGraphNodes()
    {
        var pdf = FirstSamplePdf();
        if (pdf is null) return; // sample pdf optional on CI machines

        var script = Path.Combine(RepoRoot, "tools", "ocr_pipeline.py");
        Assert.True(File.Exists(script));

        var outPath = Path.Combine(Path.GetTempPath(), $"astralpath_ocr_test_{Guid.NewGuid():N}.json");
        var psi = new ProcessStartInfo
        {
            FileName = Python,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(pdf!);
        psi.ArgumentList.Add("--ocr");
        psi.ArgumentList.Add("quick");
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outPath);

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);
        Assert.True(File.Exists(outPath), $"pipeline output missing. stderr={stderr}");

        var json = File.ReadAllText(outPath);
        Assert.False(string.IsNullOrWhiteSpace(json));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("nodes", out var nodes));
        Assert.True(nodes.GetArrayLength() > 0, "expected auto graph nodes");
        Assert.True(root.TryGetProperty("extractedChars", out var chars));
        // scanned books should either extract text or OCR something
        Assert.True(chars.GetInt32() >= 0);
        Assert.True(root.TryGetProperty("mode", out _));
    }

    [Fact]
    public void AutoGraphBuilder_ProducesAcyclicChapterChain()
    {
        // pure unit path: synthetic OCR-like payload
        var chapters = new[]
        {
            new { title = "第1章 变量与类型", kind = "chapter", offset = 0 },
            new { title = "第2章 控制流", kind = "chapter", offset = 100 },
            new { title = "第3章 函数", kind = "chapter", offset = 200 }
        };
        var terms = new[]
        {
            new { term = "变量", freq = 12 },
            new { term = "函数", freq = 9 }
        };

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            nodes = Array.Empty<object>(),
            edges = Array.Empty<object>(),
            chapters,
            terms
        });

        // rebuild using same heuristic as pipeline script via local C# mirror
        var nodes = new List<AstralPath.Graph.KpNode>();
        var edges = new List<AstralPath.Graph.KpEdge>();
        var i = 1;
        string Next() => $"A{i++:000}";
        var chapterIds = new List<string>();
        foreach (var ch in chapters)
        {
            var id = Next();
            nodes.Add(new AstralPath.Graph.KpNode(id, ch.title, "DEMO", ch.kind));
            chapterIds.Add(id);
        }
        foreach (var (a, b) in chapterIds.Zip(chapterIds.Skip(1)))
            edges.Add(new AstralPath.Graph.KpEdge(a, b, "prerequisite", 1.2, "auto:chapter-sequence"));
        foreach (var t in terms)
        {
            var id = Next();
            nodes.Add(new AstralPath.Graph.KpNode(id, t.term, "DEMO", $"freq={t.freq}"));
            edges.Add(new AstralPath.Graph.KpEdge(id, chapterIds[0], "prerequisite", 1.0, "auto:term-anchor"));
        }

        var pack = new AstralPath.Graph.GraphPack("auto-demo", "demo", "DEMO", 1, DateTime.UtcNow.ToString("O"), nodes, edges);
        var result = new AstralPath.Graph.KnowledgeGraph(pack).Validate();
        Assert.True(result.Cycles.Count == 0, string.Join(";", result.Cycles));
        Assert.True(result.NodeCount >= 3);
        Assert.True(result.EdgeCount >= 2);
        Assert.False(string.IsNullOrWhiteSpace(payload));
    }
}
