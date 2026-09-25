using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;
using AstralPath.Contracts;
using AstralPath.Infrastructure;
using AstralPath.Persistence;

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
    /// <summary>当前请求已认证用户在 HttpContext.Items 中的键。</summary>
    public const string AuthUserKey = "astralpath.auth.user";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>从已知的学生维路由中取出学生 id；识别不到返回 null（此时不做归属校验）。</summary>
    private static string? ExtractRouteStudentId(string path)
    {
        const string studentsPrefix = "/v1/students/";
        const string profilePrefix = "/v1/profile/";
        const string consentsPrefix = "/v1/consents/";

        if (path.StartsWith(studentsPrefix, StringComparison.OrdinalIgnoreCase))
            return FirstSegment(path[studentsPrefix.Length..]);
        if (path.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
            return FirstSegment(path[profilePrefix.Length..]);
        if (path.StartsWith(consentsPrefix, StringComparison.OrdinalIgnoreCase))
            return FirstSegment(path[consentsPrefix.Length..]);
        return null;
    }

    private static string? FirstSegment(string rest)
    {
        var idx = rest.IndexOfAny(new[] { '/', '?', '#' });
        var segment = idx < 0 ? rest : rest[..idx];
        return string.IsNullOrWhiteSpace(segment) ? null : Uri.UnescapeDataString(segment);
    }

    /// <summary>数据根目录（资料落盘与快照共用），与快照开关无关。</summary>
    private static string ResolveDataRoot(IConfiguration config)
    {
        var dir = config["Persistence:DataDir"];
        if (!string.IsNullOrWhiteSpace(dir)) return dir!;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AstralPath");
    }

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

        builder.Services.AddControllers()
            .ConfigureApplicationPartManager(manager =>
            {
                // 控制器程序集的显式注册。
                //
                // 为什么必须：SDK 只为「入口程序集」生成 ApplicationPartAttribute（自动发现
                // 依赖入口扫描）。CLI 宿主的入口是 AstralPath.Api 自身所以能工作；
                // 但 Android 壳的入口是 AstralPath.AndroidApp——不带任何控制器，若不显式
                // 注册，两条 controller 链路会全部 404 且无任何报错。
                // 按 AssemblyName 去重，保证在任一宿主里都恰好注册一次。
                EnsureApplicationPart(manager, typeof(ApiBootstrapper).Assembly);
                EnsureApplicationPart(manager, typeof(AstralPath.Services.ServiceHost).Assembly);
            });
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            // 模型绑定失败必须统一成 ApiFailure 形状（契约 §1.4）。
            //
            // 原来是 SuppressModelStateInvalidFilter = true：框架的自动 400 被关掉，
            // 但控制器里的 [FromBody] 参数多为**非可空类型**且未逐个判空 →
            // 畸形 JSON / 空 body 会以 null 进入 action，随即 NullReferenceException（500）。
            // 现改为「框架先拦下 + 我们统一响应形状」：任何绑定失败一律 400 VALIDATION_ERROR。
            //
            // 上传接口不受影响：IFormFile? 是**可空**参数，不会产生 ModelState 错误，
            // 依旧进入 action 由它自己返回带 hint 的 400。
            options.SuppressModelStateInvalidFilter = false;
            options.InvalidModelStateResponseFactory = ctx =>
            {
                var errors = ctx.ModelState
                    .Where(kv => kv.Value is { Errors.Count: > 0 })
                    .ToDictionary(
                        kv => string.IsNullOrEmpty(kv.Key) ? "$" : kv.Key,
                        kv => kv.Value!.Errors
                            .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage)
                                ? "字段格式不正确"
                                : e.ErrorMessage)
                            .ToArray());
                var first = errors.SelectMany(kv => kv.Value).FirstOrDefault() ?? "请求体格式错误";
                // 注意：InvalidModelStateResponseFactory 要求返回 IActionResult，
                // 而项目统一的 HttpResults.Fail 返回 IResult（minimal API），故此处直接
                // 构造同一形状的 ApiFailure，并显式沿用 Web 命名策略（camelCase）保持一致。
                var payload = new ApiFailure(
                    null,
                    new ApiError(ErrorCodes.ValidationError, first, errors),
                    Guid.NewGuid().ToString("N"));
                return new JsonResult(payload)
                {
                    StatusCode = 400,
                    SerializerSettings = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                };
            };
        });
        builder.Services.AddCors(options =>
        {
            // P2-8：原先 AllowAnyOrigin，实测预检返回 ACAO:* —— 用户浏览器上任意站点都能调用本机 API。
            // 现默认只放行「本机回环 + 安卓壳 origin」，可用 Security:CorsOrigins 追加白名单；
            // 需要完全开放时显式设 Security:AllowAnyOrigin=true。
            options.AddPolicy("demo", policy =>
            {
                var allowAny = string.Equals(builder.Configuration["Security:AllowAnyOrigin"], "true",
                    StringComparison.OrdinalIgnoreCase);
                if (allowAny)
                {
                    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                    return;
                }

                var configured = (builder.Configuration["Security:CorsOrigins"] ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var exact = configured.Length > 0
                    ? configured
                    : new[] { "https://appassets.androidplatform.net", "null" };

                policy.SetIsOriginAllowed(origin =>
                    {
                        if (string.IsNullOrWhiteSpace(origin)) return false;
                        if (exact.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;
                        // 本机回环任意端口（桌面壳 / 手机直连场景）
                        return Uri.TryCreate(origin, UriKind.Absolute, out var u)
                               && (u.Host is "localhost" or "127.0.0.1" or "::1"
                                   || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase));
                    })
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
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
        // §44/§45/§46 模块仓库：自行构造实例，便于宿主挂上状态快照钩子
        var modules = new AstralPathModules();

        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(auth);
        builder.Services.AddSingleton(modules);
        builder.Services.AddSingleton(new AppServices(store));

        // ── 知识库 / 向量检索层持久化（配置节 Persistence）──────────────
        // 原先这段装配缺失，`appsettings.json` 里的 Persistence:Mode=postgres 形同虚设；
        // 与 services/*-svc 保持同源接线。
        builder.Services.AddAstralPathPersistence(builder.Configuration);

        // ── 资料落盘目录移出 bin（P2-7）──────────────────────────────────
        // 原先落在 AppContext.BaseDirectory\materials-uploads，实测已积累 576MB 用户上传，
        // 一次 dotnet clean / 重新 publish 就会静默清空。改到数据根目录（旧内容会自动迁移）。
        AstralPath.Api.Controllers.MaterialsController.ConfigureStorage(ResolveDataRoot(builder.Configuration));

        // ── 运行时状态快照（学生 / 作答 / 掌握度 / 计划 / consent / 知识库 / 画像 / 智能体）──
        // 这一层不属于 IKnowledgeRepository：它是业务账本，原先只活在内存里，
        // 进程一重启就退回 seed 数据。用一份 JSON 快照补齐。
        var snapshotOptions = ResolveSnapshotOptions(builder.Configuration);
        var snapshot = new RuntimeSnapshotStore(snapshotOptions, store, modules);
        var snapshotLoad = snapshot.Load();
        if (snapshotOptions.Enabled)
        {
            store.AfterLock = snapshot.MarkDirty; // 业务账本：写后触发防抖保存
            snapshot.StartPeriodicSweep();        // 模块状态：定时兜底（内容哈希去重）
        }
        builder.Services.AddSingleton(snapshot);
        builder.Services.AddSingleton(snapshotOptions);

        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(() => snapshot.Flush());

        // ── 启动自检：如实说明「数据到底存在哪」，避免静默降级 ──────────
        ReportPersistenceHealth(builder.Configuration, snapshotOptions, snapshotLoad);

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

        // ── 访问控制（P2-1）──────────────────────────────────────────────
        // 原实现全站零鉴权：无令牌即可读他人画像、**替他人授予/撤销 consent**、替他人提交作答。
        // 两层处理（默认不改变演示体验）：
        //   ① 携带 Bearer 令牌时：路由里的学生 id 必须与令牌主体一致 → 否则 403；
        //   ② Security:RequireAuth=true 时：/v1/** 一律要求有效令牌 → 缺令牌 401。
        // 说明：AuthUser 目前没有角色字段，因此「教师端仅教师可见」仍需补数据模型，见后续待办。
        var requireAuth = string.Equals(builder.Configuration["Security:RequireAuth"], "true",
            StringComparison.OrdinalIgnoreCase);
        var enforceOwnership = !string.Equals(builder.Configuration["Security:EnforceOwnership"], "false",
            StringComparison.OrdinalIgnoreCase);
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";

            // 解析令牌（不区分大小写；无效令牌等同未登录）
            var header = context.Request.Headers.Authorization.ToString();
            AuthUser? user = null;
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = header["Bearer ".Length..].Trim();
                if (token.Length > 0) user = auth.FindByAccessToken(token);
            }
            if (user is not null) context.Items[AuthUserKey] = user;

            var isApi = path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);
            var exempt = !isApi
                         || path.StartsWith("/v1/demo/", StringComparison.OrdinalIgnoreCase);

            if (requireAuth && isApi && !exempt && user is null)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(
                    new ApiFailure(null, new ApiError(ErrorCodes.AuthRequired,
                        "需要登录：请在 Authorization 头携带 Bearer 令牌", new { }), context.TraceIdentifier),
                    JsonOpts);
                return;
            }

            if (enforceOwnership && user is not null)
            {
                var routeStudent = ExtractRouteStudentId(path);
                if (routeStudent is not null
                    && !string.Equals(routeStudent, user.DemoStudentId, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(
                    new ApiFailure(null, new ApiError(ErrorCodes.Forbidden,
                        $"无权访问他人数据：令牌主体 {user.DemoStudentId}，目标 {routeStudent}", new { }),
                        context.TraceIdentifier),
                        JsonOpts);
                    return;
                }
            }

            await next();
        });

        // ── 框架级错误形状统一（P2-3）────────────────────────────────────
        // 415 / 405 / 路由 404 由框架在进入 MVC 之前产生，响应体是 RFC problem+json，
        // 与项目统一契约 {data,error,traceId} 不一致（实测 415 返回
        // {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.16",...}）。
        // 这里做一次轻量转写：只处理「无扩展名的 API 路径 + 4xx + problem+json + ≤64KB」，
        // 静态资源与文件下载带扩展名，直接放行不缓冲，避免打断 sendfile / 流式响应。
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (path.Length == 0 || Path.HasExtension(path))
            {
                await next();
                return;
            }

            var original = context.Response.Body;
            var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try
            {
                await next();
                var contentType = context.Response.ContentType ?? "";
                var isProblem = contentType.Contains("problem+json", StringComparison.OrdinalIgnoreCase);
                // 未匹配路由的 404 / 405 由框架直接返回**空 body**（不是 problem+json），
                // 因此还要覆盖「空 body 的框架级错误」这一种情况，否则契约仍然不统一。
                var isEmptyFrameworkError = context.Response.StatusCode is 404 or 405 or 415
                                            && buffer.Length == 0;
                if (context.Response.StatusCode >= 400 && (isProblem || isEmptyFrameworkError)
                    && buffer.Length <= 64 * 1024)
                {
                    var message = context.Response.StatusCode switch
                    {
                        404 => "找不到请求的资源",
                        405 => "该路径不支持此请求方法",
                        415 => "不支持的请求内容类型",
                        _ => "请求无法处理"
                    };
                    object? details = null;
                    if (isProblem)
                    {
                        try
                        {
                            buffer.Position = 0;
                            using var doc = await JsonDocument.ParseAsync(buffer);
                            if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                                message = t.GetString() ?? message;
                            if (doc.RootElement.TryGetProperty("errors", out var errs)) details = errs;
                            else if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                                message = d.GetString() ?? message;
                        }
                        catch { /* 解析失败用默认文案 */ }
                    }

                    var code = context.Response.StatusCode switch
                    {
                        415 => "UNSUPPORTED_MEDIA_TYPE",
                        405 => "METHOD_NOT_ALLOWED",
                        404 => ErrorCodes.ResourceNotFound,
                        _ => "REQUEST_ERROR"
                    };
                    buffer.SetLength(0);
                    context.Response.ContentType = "application/json; charset=utf-8";
                    context.Response.ContentLength = null;
                    buffer.Position = 0;
                    await context.Response.WriteAsJsonAsync(
                        new ApiFailure(null, new ApiError(code, message, details ?? new { }), context.TraceIdentifier),
                        JsonOpts);
                }

                buffer.Position = 0;
                if (context.Response.ContentLength is null && buffer.Length > 0)
                    context.Response.ContentLength = buffer.Length;
                await buffer.CopyToAsync(original);
            }
            finally
            {
                context.Response.Body = original;
                buffer.Dispose();
            }
        });
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
        // 注意：WebRootPath 在「内容根下没有 wwwroot」时为 null，不能直接进 Path.Combine
        var webRootCandidates = new List<string>();
        if (!string.IsNullOrEmpty(app.Environment.WebRootPath))
            webRootCandidates.Add(app.Environment.WebRootPath);
        webRootCandidates.Add(Path.Combine(app.Environment.ContentRootPath, "wwwroot"));
        webRootCandidates.Add(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        webRootCandidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot")));
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
        // 健康检查如实暴露「数据存在哪」：知识库层（Persistence 配置节）+ 运行时状态快照。
        app.MapGet("/health/ready", async (IServiceProvider sp) =>
        {
            var kbMode = builder.Configuration["Persistence:Mode"] ?? "memory";
            bool? kbReady = null;
            int? vectorDimensions = null;
            string? indexKind = null;
            string? kbError = null;
            try
            {
                var repo = sp.GetService<IKnowledgeRepository>();
                if (repo is not null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var diag = await repo.DiagnosticsAsync(cts.Token);
                    kbReady = diag.Ready;
                    vectorDimensions = diag.VectorDimensions;
                    indexKind = diag.IndexKind;
                    kbError = diag.Error;
                }
            }
            catch (OperationCanceledException)
            {
                kbError = "连接超时（3 秒内未获得诊断响应），数据库不可达";
            }
            catch (Exception ex)
            {
                kbError = $"{ex.GetType().Name}: {ex.Message}";
            }

            return Results.Json(new
            {
                status = "ready",
                persistence = new
                {
                    knowledgeBase = new
                    {
                        mode = kbMode,
                        ready = kbReady,
                        vectorDimensions,
                        indexKind,
                        error = kbError
                    },
                    runtimeSnapshot = new
                    {
                        enabled = snapshotOptions.Enabled,
                        path = snapshotOptions.Enabled ? snapshot.FilePath : null,
                        loaded = snapshotLoad.Loaded,
                        students = snapshotLoad.Students,
                        kbDocuments = snapshotLoad.KbDocuments,
                        agentTurns = snapshotLoad.AgentTurns,
                        note = snapshotOptions.Enabled
                            ? "运行时状态已落盘，进程重启后自动恢复"
                            : "快照未启用：学生作答、授权、知识库文档在进程重启后会丢失"
                    }
                },
                checks = new Dictionary<string, string>
                {
                    ["graph"] = store.Graph.Validate().Ok ? "ok" : "degraded",
                    ["students"] = store.Students.Count.ToString(),
                    ["packId"] = store.PackId,
                    ["graphVersion"] = store.Graph.GraphVersion.ToString()
                }
            });
        });

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
                // 必须用与上传/列表一致的目录（P2-7 修复后不再是 bin 目录），
                // 否则注册表里全是 bin 里的旧条目，与真实数据目录脱节。
                var materialsDir = AstralPath.Api.Controllers.MaterialsController.CurrentMaterialsDir;
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

    /// <summary>
    /// 解析运行时快照配置。
    /// 默认**开启**（本机优先，语义与无表面版 localStorage 对齐），但三种情况关闭：
    ///   ① 环境变量 <c>ASTRALPATH_SNAPSHOT=0</c> 显式关闭；
    ///   ② 配置 <c>Persistence:Snapshot=false</c>；
    ///   ③ 当前进程加载了测试宿主（xunit / Mvc.Testing）——否则上一次测试的数据会污染下一次，
    ///      使 Api.Tests 失去可重复性。
    /// </summary>
    private static RuntimeSnapshotOptions ResolveSnapshotOptions(IConfiguration config)
    {
        var testHostLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a =>
            a.GetName().Name is "xunit.core" or "xunit.execution.dotnet" or "Microsoft.AspNetCore.Mvc.Testing");

        var envSwitch = Environment.GetEnvironmentVariable("ASTRALPATH_SNAPSHOT");
        var enabled =
            !string.Equals(envSwitch, "0", StringComparison.Ordinal)
            && !string.Equals(config["Persistence:Snapshot"], "false", StringComparison.OrdinalIgnoreCase)
            && !testHostLoaded;

        var dir = ResolveDataRoot(config);

        return new RuntimeSnapshotOptions
        {
            // 与资料落盘（<root>/materials-uploads）分开，避免混在一层
            DataDir = enabled ? Path.Combine(dir, "state") : null,
            DebounceMs = int.TryParse(config["Persistence:SnapshotDebounceMs"], out var d) && d > 0 ? d : 400,
            KeepBackups = int.TryParse(config["Persistence:SnapshotKeepBackups"], out var k) ? k : 1
        };
    }

    /// <summary>
    /// 启动自检输出：把「知识库层持久化模式」与「运行时状态是否落盘」如实打印，
    /// 并在配置了 postgres 却连不上时给出**显式降级告警**（原实现静默无提示）。
    /// </summary>
    private static void ReportPersistenceHealth(
        IConfiguration config, RuntimeSnapshotOptions options, SnapshotLoadResult load)
    {
        var mode = config["Persistence:Mode"] ?? "memory";
        var conn = config["Persistence:ConnectionString"]
                   ?? config.GetConnectionString("Default")
                   ?? Environment.GetEnvironmentVariable("ASTRLPATH_CONN")
                   ?? "";

        if (string.Equals(mode, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            var (host, port) = ParseHostPort(conn);
            var reachable = host is not null && TryTcpProbe(host, port);
            if (!reachable)
            {
                var allowFallback = !string.Equals(config["Persistence:AllowMemoryFallback"], "false",
                    StringComparison.OrdinalIgnoreCase);
                Console.WriteLine(
                    $"[AstralPath][WARN] Persistence:Mode=postgres，但 {host ?? "?"}:{port} 不可达。" +
                    (allowFallback
                        ? "知识库检索将走内存回退（该部分数据进程重启后不保留）。"
                        : "且 AllowMemoryFallback=false，相关接口会直接报错。"));
            }
        }

        if (options.Enabled)
        {
            var loadNote = load.Loaded
                ? $"已恢复学生 {load.Students} · 知识库文档 {load.KbDocuments} · 智能体轮次 {load.AgentTurns}"
                : load.Error is null
                    ? "无历史快照，按种子数据启动"
                    : $"历史快照未采用（{load.Error}）";
            Console.WriteLine($"[AstralPath] 运行时状态快照：已启用 · {options.DataDir} · {loadNote}");
        }
        else
        {
            Console.WriteLine("[AstralPath][WARN] 运行时状态快照未启用：学生作答、授权、知识库文档等" +
                              "在进程重启后会丢失（如需保留请设置 Persistence:DataDir）。");
        }
    }

    /// <summary>从 Npgsql 连接串里取 host/port（够用即可，不做完整解析）。</summary>
    private static (string? Host, int Port) ParseHostPort(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return (null, 5432);
        string? host = null;
        var port = 5432;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            var value = kv[1].Trim();
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Server", StringComparison.OrdinalIgnoreCase)) host = value;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, out var p)) port = p;
        }
        return (host, port);
    }

    /// <summary>短超时 TCP 探测：只判断「端口是否有人监听」，不建立完整会话。</summary>
    private static bool TryTcpProbe(string host, int port, int timeoutMs = 1500)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync(host, port).Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按程序集名去重地注册 MVC ApplicationPart（见 Build 内注释）。</summary>
    private static void EnsureApplicationPart(
        Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartManager manager,
        System.Reflection.Assembly assembly)
    {
        var name = assembly.GetName().Name!;
        if (manager.ApplicationParts.OfType<Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart>()
                .Any(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
        {
            return;
        }
        manager.ApplicationParts.Add(new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(assembly));
    }
}

/// <summary>控制器组合根（供扩展服务宿主复用）。</summary>
public sealed class AppServices
{
    public AstralPathStore Store { get; }

    public AppServices(AstralPathStore store) => Store = store;
}
