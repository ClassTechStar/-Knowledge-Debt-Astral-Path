using AstralPath.Persistence;
using AstralPath.Services;

// prerequisite-simulator-svc —— 先修补全模拟器（§19.8）
// 独立进程/容器部署（§19.11）：
//   · 只映射本服务的控制器，路由与契约与单体模式一致（同源 AstralPath.Services）；
//   · 独立配置（appsettings.json + ASTRALPATH_ 环境变量）与独立数据库（ConnectionStrings:Default）；
//   · 持久化模式可切换：Persistence:Mode = memory（默认，本地调试）| postgres（生产）。
ServiceHost.Run<PrerequisiteSimulatorController>(args, "prerequisite-simulator-svc", b => b.Services.AddAstralPathPersistence(b.Configuration));
