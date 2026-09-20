using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AstralPath.Infrastructure;

namespace AstralPath.Services;

/// <summary>
/// 独立服务宿主（§19.11）。
/// 每个扩展服务一个进程/容器：只映射**属于自己的那一个控制器**，
/// 路由与契约与单体模式完全相同（同源 <c>AstralPath.Services</c>）。
/// <para>
/// 设计要点：整个进程内**只有一个 WebApplicationBuilder**（由本类创建），
/// 宿主通过 <c>configure</c> 回调追加自己的服务（如持久化注册），避免"两个 builder 导致注册丢失"。
/// </para>
/// </summary>
public static class ServiceHost
{
    public static WebApplication Build<TController>(string[] args, string serviceName,
        Action<WebApplicationBuilder>? configure = null)
        where TController : ControllerBase
    {
        var builder = WebApplication.CreateBuilder(args);

        // 每个服务拥有自己的配置（环境变量以 ASTRALPATH_ 前缀覆盖）
        builder.Configuration.AddEnvironmentVariables("ASTRALPATH_");

        builder.Services.AddSingleton(sp =>
        {
            var pack = Environment.GetEnvironmentVariable("ASTRALPATH_GRAPH_PACK");
            if (string.IsNullOrWhiteSpace(pack)) return new AstralPathStore(null);
            // 启动期显式校验：图包目录缺失时给出可读错误，避免运行到一半才抛文件异常（排障友好）
            if (!Directory.Exists(pack))
                throw new InvalidOperationException(
                    $"ASTRALPATH_GRAPH_PACK 指向的目录不存在：{pack}。请检查配置或使用本仓库的 graph-packs/accounting-v1。");
            return new AstralPathStore(pack);
        });

        configure?.Invoke(builder);

        builder.Services.AddControllers()
            .ConfigureApplicationPartManager(manager =>
            {
                // 只暴露本服务的控制器，避免把其他服务的路由一起带出来
                manager.FeatureProviders.Clear();
                manager.FeatureProviders.Add(new SingleControllerFeatureProvider(typeof(TController)));
            });

        var app = builder.Build();

        app.MapGet("/health/live", () => Results.Json(new { status = "alive", service = serviceName }));
        app.MapGet("/health/ready", (AstralPathStore store) => Results.Json(new
        {
            status = "ready",
            service = serviceName,
            graph = store.Graph.Validate().Ok ? "ok" : "degraded",
            graphVersion = store.Graph.GraphVersion
        }));

        app.MapControllers();
        return app;
    }

    public static void Run<TController>(string[] args, string serviceName,
        Action<WebApplicationBuilder>? configure = null) where TController : ControllerBase
        => Build<TController>(args, serviceName, configure).Run();

    private sealed class SingleControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        private readonly Type _controllerType;
        public SingleControllerFeatureProvider(Type controllerType) => _controllerType = controllerType;

        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            if (feature.Controllers.Contains(_controllerType.GetTypeInfo())) return;
            feature.Controllers.Add(_controllerType.GetTypeInfo());
        }
    }
}
