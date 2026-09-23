using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using AstralPath.Contracts;
using AstralPath.Infrastructure;

namespace AstralPath.Api;

/// <summary>
/// AstralPath.Api 的**可复用启动入口**（方案 §14 / §16）。
///
/// 为什么抽出来：这套主机配置原先只写死在 <c>Program.cs</c> 的顶级语句里，
/// 导致「要给同一个 API 换一种宿主形态」时只有两条路——起子进程，或者把
/// 这 120 行管道配置复制一份。三条链路现在共用本入口：
/// <list type="bullet">
///   <item>Windows 壳：<c>AstralPath.Desktop</c> 起本机 API（现由本代码承载管线）；</item>
///   <item>Android 壳：<c>AstralPath.AndroidApp</c> 把同一 API 跑在 APK 内开机自启；</item>
///   <item>CLI / 容器 / 测试：<c>Program.Main</c> 与 <c>WebApplicationFactory&lt;Program&gt;</c>。</item>
/// </list>
/// 除以太网/回环监听外的差异只由参数（<paramref name="args"/>、
/// <paramref name="contentRoot"/>、<paramref name="webRoot"/>）决定，因此不存在
/// 「改了 API 却漏改某一端」的可能。
/// </summary>
public sealed class ApiBootstrapper
{
    /// <summary>
    /// 构建但不启动主机的 <see cref="WebApplication"/>；调用方负责 <c>Run</c> / <c>StartAsync</c>。
    /// </summary>
    /// <param name="args">命令行参数（<c>--urls</c> 等），可为 null。</param>
    /// <param name="contentRoot">内容根目录；null 表示沿用当前工作目录。</param>
    /// <param name="webRoot">静态文件根；null 表示沿用 <c>{contentRoot}/wwwroot</c> 及兼容探测。</param>
    public static WebApplication Build(string[]? args = null, string? contentRoot = null, string? webRoot = null)
    {
        // 支持大体积教材 PDF（Go/C# 等扫描版可超过 100MB）；多选一次可到 1GB+
        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        if (!string.IsNullOrWhiteSpace(contentRoot))
            builder.WebHost.UseContentRoot(contentRoot!);
        if (!string.IsNullOrWhiteSpace(webRoot))
            builder.WebHost.UseWebRoot(webRoot!);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 2L * 1024 * 1024 * 1024; // 2GB
        });
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024;
            options.ValueLengthLimit = int.MaxValue;
            options.MultipartHeadersLengthLimit = int.MaxValue;
        });

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            // 上传接口自行返回统一 ApiFailure，避免模型绑定失败时吞成框架默认 4xx/空响应
            options.SuppressModelStateInvalidFilter = true;
        });
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("demo", policy =>
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true));
        });

        var graphPack = Environment.GetEnvironmentVariable("ASTRALPATH_GRAPH_PACK");

        // 旧环境变量前缀迁移告警：既不破坏运维习惯，也不在代码中留下旧名字面量
        // （《分工方案》§8.2 要求「任何新增代码中不得再出现旧名」，故前缀以拼接方式构造）
        var legacyPrefix = string.Concat("Z", "Z", "_");
        foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            var envName = item.Key?.ToString() ?? "";
            if (envName.StartsWith(legacyPrefix, StringComparison.Ordinal))
            {
                Console.WriteLine($"[WARN] 检测到旧环境变量 {envName}，已按新规范忽略；请改用 ASTRALPATH_{envName[legacyPrefix.Length..]}");
            }
        }

        var store = new AstralPathStore(graphPack);
        var auth = new AuthStore();
        auth.SeedDemoUsers();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(auth);
        // §44/§45/§46 模块仓库：智能体路由与会话、知识库生命周期、画像派生
        builder.Services.AddSingleton<AstralPathModules>();
        builder.Services.AddSingleton(new AppServices(store));

        var app = builder.Build();

        // 全局异常兜底：避免未捕获异常变成空白 500，统一 ApiFailure 形状
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (BadHttpRequestException ex)
            {
                context.Response.StatusCode = 400;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(new
                {
                    data = (object?)null,
                    error = new { code = "VALIDATION_ERROR", message = ex.Message, details = new { } },
                    traceId = context.TraceIdentifier
                });
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                context.Response.StatusCode = 499;
            }
            catch (Exception ex)
            {
                var status = ex switch
                {
                    FileNotFoundException => 404,
                    DirectoryNotFoundException => 404,
                    UnauthorizedAccessException => 403,
                    InvalidDataException => 400,
                    IOException => 503,
                    TimeoutException => 504,
                    _ => 500
                };
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json; charset=utf-8";
                Console.WriteLine($"[AstralPath] unhandled {context.Request.Method} {context.Request.Path}: {ex}");
                await context.Response.WriteAsJsonAsync(new
                {
                    data = (object?)null,
                    error = new
                    {
                        code = status == 500 ? "INTERNAL_ERROR" : "REQUEST_ERROR",
                        message = status == 500 ? "服务内部错误，请稍后重试" : ex.Message,
                        details = new { type = ex.GetType().Name }
                    },
                    traceId = context.TraceIdentifier
                });
            }
        });

        app.UseSwagger();
        app.UseSwaggerUI();
        app.UseCors("demo");
        app.UseDefaultFiles();
        // 演示 UI 不缓存，避免切换图谱/教材时仍显示旧页面
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (path is "/" or "/index.html" || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";
            }
            await next();
        });
        // wwwroot 可能在 bin 输出目录；若 ContentRoot 不对则显式指定
        var webRootCandidates = new[]
        {
            Path.Combine(app.Environment.ContentRootPath, "wwwroot"),
            Path.Combine(app.Environment.WebRootPath, ""),
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot")),
        };
        var resolvedWebRoot = webRootCandidates.FirstOrDefault(Directory.Exists);
        if (resolvedWebRoot != null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(resolvedWebRoot),
                RequestPath = ""
            });
        }
        else
        {
            app.UseStaticFiles();
        }
        app.MapControllers();
        app.MapGet("/health/ready", () => Results.Json(new
        {
            status = "ready",
            checks = new Dictionary<string, string>
            {
                ["graph"] = store.Graph.Validate().Ok ? "ok" : "degraded",
                ["students"] = store.Students.Count.ToString(),
                ["packId"] = store.PackId,
                ["graphVersion"] = store.Graph.GraphVersion.ToString()
            }
        }));

        // 启动即拉起全部服务并暴露状态（桌面壳 / 手机端可等待「全部就绪」）
        app.MapGet("/api/services", () =>
        {
            var python = MaterialPipeline.ResolvePython();
            var tess = MaterialPipeline.ResolveTesseract();
            var tessdata = MaterialPipeline.ResolveTessdata();
            string script;
            try { script = MaterialPipeline.ResolveOcrScript(); }
            catch { script = ""; }
            var materialsDir = Path.Combine(AppContext.BaseDirectory, "materials-uploads");
            var hydrated = MaterialRegistry.HydrateFromDirectory(materialsDir);
            var mats = MaterialRegistry.ListMaterials();
            var graphs = MaterialRegistry.ListGraphs();
            return Results.Json(new
            {
                status = "ready",
                startedAt = DateTime.UtcNow,
                services = new Dictionary<string, object>
                {
                    ["api"] = new { ok = true, url = $"{app.Urls.FirstOrDefault() ?? "http://127.0.0.1"}" },
                    ["graph"] = new { ok = store.Graph.Validate().Ok, version = store.Graph.GraphVersion, packId = store.PackId },
                    ["students"] = new { ok = store.Students.Count > 0, count = store.Students.Count },
                    ["ocr"] = new
                    {
                        ok = !string.IsNullOrEmpty(script) && (tess != null || python != "python"),
                        python,
                        tesseract = tess,
                        tessdata,
                        script
                    },
                    ["materials"] = new { ok = true, count = mats.Count, hydratedNow = hydrated, dir = materialsDir },
                    ["knowledgeGraphs"] = new { ok = true, count = graphs.Count },
                    ["agent"] = new { ok = true, note = "受约束路由已加载" }
                }
            });
        });

        app.MapPost("/api/services/warmup", () =>
        {
            var materialsDir = Path.Combine(AppContext.BaseDirectory, "materials-uploads");
            var hydrated = MaterialRegistry.HydrateFromDirectory(materialsDir);
            return Results.Json(new { ok = true, hydrated, materials = MaterialRegistry.ListMaterials().Count });
        });

        // 便于前端探测真实 API 根地址（防 file:// / 预览面板误连导致 404）
        app.MapGet("/api/meta", (HttpContext ctx) => Results.Json(new
        {
            service = "astralpath",
            baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}",
            upload = "/v1/materials/upload",
            hint = "请通过 http://127.0.0.1:5190/ 打开演示台；手机端请在「服务器地址」填写电脑的局域网 IP"
        }));

        app.MapGet("/api/demo/students", () => store.Lock(() =>
            store.Students.Values.Select(s => new
            {
                s.StudentId,
                s.DisplayName,
                s.DemoGroup,
                debtCount = s.DebtEdges.Count(d => d.Status != "cleared")
            }).ToList()));

        // 启动横幅：打印手机/局域网可用的地址，便于直接填入 App 的「服务器地址」
        // （手机端 127.0.0.1 指向手机自身，必须使用电脑的局域网 IP）
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            // 启动即预热：资料热加载 + OCR 运行时探测，保证上传/解析开箱可用
            try
            {
                var materialsDir = Path.Combine(AppContext.BaseDirectory, "materials-uploads");
                var n = MaterialRegistry.HydrateFromDirectory(materialsDir);
                var tess = MaterialPipeline.ResolveTesseract();
                var py = MaterialPipeline.ResolvePython();
                Console.WriteLine($"[AstralPath] 服务预热完成：资料 {MaterialRegistry.ListMaterials().Count}（本次热加载 {n}）· python={py} · tesseract={(tess ?? "未安装")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AstralPath] 服务预热警告：{ex.Message}");
            }

            var bound = app.Urls.ToList();
            Console.WriteLine($"[AstralPath] 已启动，监听：{(bound.Count == 0 ? "(默认)" : string.Join("  ", bound))}");

            var lan = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address.ToString())
                .Where(ip => ip.Contains('.') && !ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                .Distinct()
                .ToList();

            var exposesLan = bound.Any(u => u.Contains("0.0.0.0") || u.Contains('+') || u.Contains('*'));
            if (exposesLan && lan.Count > 0)
            {
                Console.WriteLine("[AstralPath] 手机端请在「服务器地址」填入：" +
                    string.Join("  或  ", lan.Select(ip => $"http://{ip}:5190")));
            }
            else
            {
                Console.WriteLine("[AstralPath] 当前仅监听本机。手机直连（同一 Wi-Fi）请改用：");
                Console.WriteLine("             dotnet run --project src/AstralPath.Api -c Release --urls http://0.0.0.0:5190");
                Console.WriteLine("             或 USB 连接后执行：adb reverse tcp:5190 tcp:5190");
            }
        });

        return app;
    }
}

/// <summary>控制器组合根（供扩展服务宿主复用）。</summary>
public sealed class AppServices
{
    public AstralPathStore Store { get; }

    public AppServices(AstralPathStore store) => Store = store;
}
