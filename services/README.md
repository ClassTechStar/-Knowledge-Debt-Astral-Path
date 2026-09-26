# services/ · 扩展微服务（工程现状说明）

> **如实标注（2026-09-26 审计结论，避免"假覆盖"误导）**：本目录 10 个服务
> （concept-diffusion / exam-impact / knowledge-forecast / lab-bench /
> learning-velocity / micro-lesson / peer-cohort / prerequisite-simulator /
> spaced-review / study-group）目前是**宿主壳**——每个 `Program.cs` 仅约 9 行，
> 复用 `src/AstralPath.Services/ExtensionServiceControllers.cs` 里的 11 个控制器
> （约 20 个端点）提供能力，**不是**按方案 §19 独立实现的领域服务。

| 项 | 现状 |
|---|---|
| 独立领域实现 | ❌ 未做（方案 §19 规格完整，实现收敛在共享控制器层） |
| 独立部署物 | 形态上可独立启动（各自 Dockerfile / 端口），但逻辑同源 |
| 测试 | `tests/AstralPath.Api.Tests/ExtensionServicesTests.cs` 覆盖共享控制器 |
| 重构计划 | 单体优先（§0.1 单体版 2.2 是当前交付形态）；仅当演示需要"多服务拓扑"时再拆分，拆分时按 §19 逐服务落领域逻辑并补金样 |

**不要**把本目录当作已完成微服务架构的证明；竞赛答辩口径请以「单体版 2.2（无微服务）」为准。
