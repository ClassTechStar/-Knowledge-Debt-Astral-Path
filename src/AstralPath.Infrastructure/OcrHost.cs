using System.Diagnostics;
using System.Text.Json;

namespace AstralPath.Infrastructure;

/// <summary>
/// OCR 宿主执行器（自壳层下沉，P2 审计 M6）：python tools/ocr_pipeline.py
/// （文本层抽取 + tesseract OCR + 乱码检测）。任何宿主（Windows 壳 / 桌面端 / 内嵌 API）
/// 都可以直接调用，壳里只保留「接收页面消息 → 调用 → 回传」的薄桥。
///
/// 路径解析顺序：环境变量 → 安装目录 tools → 从安装目录向上回溯找仓库 tools（开发态）。
/// 不硬编码任何机器特定路径；找不到时用 ASTRALPATH_OCR_SCRIPT / ASTRALPATH_OCR_PYTHON 显式指定。
/// </summary>
public static class OcrHost
{
    /// <summary>从安装目录逐级向上回溯（含自身），覆盖「bin/…/net10.0 → 仓库根」的开发态布局。</summary>
    private static IEnumerable<string> WalkUp(int maxLevels = 8)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < maxLevels && !string.IsNullOrEmpty(dir); i++)
        {
            yield return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
    }

    /// <summary>在安装目录与上层仓库中定位 tools 下的文件（安装态第一层即命中 {app}\tools）。</summary>
    private static string? FindTool(string relative)
        => WalkUp().Select(d => Path.Combine(d, relative)).FirstOrDefault(File.Exists);

    private static string? FindToolDir(string relative)
        => WalkUp().Select(d => Path.Combine(d, relative)).FirstOrDefault(Directory.Exists);

    /// <summary>定位 tools\ocr_pipeline.py；环境变量 ASTRALPATH_OCR_SCRIPT 优先。公开供宿主启动时自检。</summary>
    public static string? ResolveScript()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_OCR_SCRIPT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        return FindTool(Path.Combine("tools", "ocr_pipeline.py"));
    }


    /// <summary>P0-5：OCR 超时按模式参数化——full 全书 OCR 需要长超时；
    /// ASTRALPATH_OCR_TIMEOUT_SECONDS 可整体覆盖（秒）。与 MaterialPipeline 保持一致。</summary>
    private static int ResolveTimeoutMinutes(string ocrMode)
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_OCR_TIMEOUT_SECONDS");
        if (double.TryParse(env, out var sec) && sec > 0)
            return Math.Max(1, (int)Math.Ceiling(sec / 60.0));
        return ocrMode switch
        {
            "full" => 40,
            "standard" => 20,
            _ => 3,
        };
    }

    /// <summary>定位 Python 解释器：环境变量 → tools 旁专用 venv → 系统 PATH 上的 python。</summary>
    public static string ResolvePython()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_OCR_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var venv = WalkUp().Select(d => Path.Combine(d, "tools", "ocr-venv", "Scripts", "python.exe"))
                           .FirstOrDefault(File.Exists);
        return venv ?? "python";
    }

    /// <summary>定位 tesseract.exe（可选；缺失时管线走纯文本层模式）。</summary>
    public static string? ResolveTesseract()
    {
        var env = Environment.GetEnvironmentVariable("ASTRALPATH_TESSERACT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var candidates = new[]
        {
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>定位 tessdata 目录；环境变量 TESSDATA_PREFIX 优先。</summary>
    public static string? ResolveTessdata()
    {
        var env = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        return FindToolDir(Path.Combine("tools", "tessdata"))
            ?? @"C:\Program Files\Tesseract-OCR\tessdata";
    }

    /// <summary>
    /// 执行 OCR 管线：写入临时 PDF → python 管线 → 读取 fullText JSON。
    /// 看门狗按模式参数化（ResolveTimeoutMinutes），超时杀进程树；临时文件在 finally 中清理。
    /// <paramref name="onProgress"/> 接收 stderr 中的 <c>PROGRESS …</c> 行（P0-2 流式进度）。
    /// </summary>
    public static async Task<string> RunAsync(string name, string mode, string? pages, string base64,
        Action<string>? onProgress = null)
    {
        var script = ResolveScript() ??
            throw new InvalidOperationException("未找到 tools\\ocr_pipeline.py（请完整安装，或设置 ASTRALPATH_OCR_SCRIPT）");
        var python = ResolvePython();

        var safe = string.Concat(name.Where(char.IsLetterOrDigit).Take(40));
        if (safe.Length == 0) safe = "material";
        var tmp = Path.Combine(Path.GetTempPath(), $"astralpath_mono_{Guid.NewGuid():N}_{safe}.pdf");
        var outFile = Path.Combine(Path.GetTempPath(), $"astralpath_mono_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllBytesAsync(tmp, Convert.FromBase64String(base64));

            var psi = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("--ocr");
            psi.ArgumentList.Add(mode is "quick" or "standard" or "full" or "none" ? mode : "standard");
            if (!string.IsNullOrWhiteSpace(pages))
            {
                psi.ArgumentList.Add("--pages");
                psi.ArgumentList.Add(pages!);
            }
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(outFile);

            var tess = ResolveTesseract();
            if (tess is not null)
            {
                psi.Environment["ASTRALPATH_TESSERACT"] = tess;
                var dir = Path.GetDirectoryName(tess);
                if (!string.IsNullOrEmpty(dir))
                    psi.Environment["PATH"] = dir + Path.PathSeparator + (psi.Environment["PATH"] ?? "");
            }
            var tessdata = ResolveTessdata();
            if (tessdata is not null) psi.Environment["TESSDATA_PREFIX"] = tessdata;

            using var proc = Start(python, psi);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            // P0-2：边读 stderr 边上抛 PROGRESS 行
            var stderrBuilder = new System.Text.StringBuilder();
            var stderrTask = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                {
                    stderrBuilder.AppendLine(line);
                    if (onProgress is not null && line.StartsWith("PROGRESS", StringComparison.Ordinal))
                        onProgress(line);
                }
            });
            // P0-5 对齐 MaterialPipeline：超时按模式（full 40min / standard 20min / 其他 3min），
            // ASTRALPATH_OCR_TIMEOUT_SECONDS 可整体覆盖；扫描书/图文书 standard 全页 OCR 需 >10 分钟
            var timeoutMinutes = ResolveTimeoutMinutes(mode);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new TimeoutException($"OCR 超时({timeoutMinutes}min)；可改用 quick 模式或拆分文件");
            }
            await stderrTask;
            var stderr = stderrBuilder.ToString();
            if (!File.Exists(outFile))
                throw new InvalidOperationException("OCR 输出缺失 exit=" + proc.ExitCode + " " + Truncate(stderr, 200));

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outFile));
            var text = doc.RootElement.TryGetProperty("fullText", out var ft) ? ft.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("OCR 未识别出文本 " + Truncate(stderr, 200));
            return text;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* ignore */ }
        }
    }

    private static Process Start(string python, ProcessStartInfo psi)
    {
        try { return Process.Start(psi) ?? throw new InvalidOperationException("python 启动失败：" + python); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new InvalidOperationException("未找到 Python（" + python + "）。请安装 Python，或设置 ASTRALPATH_OCR_PYTHON 指向 python.exe", ex);
        }
    }

    /// <summary>P3-4：清理本地 OCR 页缓存（~/.astralpath/ocr-cache）。</summary>
    public static int ClearOcrCache()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".astralpath", "ocr-cache");
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(f); n++; } catch { /* ignore */ }
        }
        return n;
    }

    private static string Truncate(string? s, int n)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n] + "…";
    }
}
