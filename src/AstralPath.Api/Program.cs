using AstralPath.Api;

// CLI 宿主（方案 §14）：管线与路由的唯一事实源在 ApiBootstrapper，
// 这里只负责「解析参数 → 构建 → 运行」，与 Windows / Android 壳共用同一份配置。
var app = ApiBootstrapper.Build(args);
app.Run();

// 供 WebApplicationFactory<Program>（tests/AstralPath.Api.Tests）定位入口点，
// 不得删除。
public partial class Program;
