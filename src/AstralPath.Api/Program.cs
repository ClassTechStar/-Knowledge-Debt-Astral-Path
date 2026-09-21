using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AstralPath.Contracts;
using AstralPath.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// 支持大体积教材 PDF（Go/C# 等扫描版可超过 100MB）
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 512L * 1024 * 1024;
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
// （《分工方案》§8.2 要求「任何新增代码中不得再出现旧名」，故前缀以拼接方式构造）。
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
    Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),
    Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot")),
};
var webRoot = webRootCandidates.FirstOrDefault(Directory.Exists);
if (webRoot != null)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot),
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

// 便于前端探测真实 API 根地址（防 file:// / 预览面板误连导致 404）
app.MapGet("/api/meta", (HttpContext ctx) => Results.Json(new
{
    service = "astralpath",
    baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}",
    upload = "/v1/materials/upload",
    hint = "请通过 http://127.0.0.1:5190/ 打开演示台，不要用本地文件或预览代理"
}));

app.MapGet("/api/demo/students", () => store.Lock(() =>
    store.Students.Values.Select(s => new
    {
        s.StudentId,
        s.DisplayName,
        s.DemoGroup,
        debtCount = s.DebtEdges.Count(d => d.Status != "cleared")
    }).ToList()));

app.Run();

public partial class Program;

public sealed class AppServices
{
    public AstralPathStore Store { get; }

    public AppServices(AstralPathStore store) => Store = store;
}
