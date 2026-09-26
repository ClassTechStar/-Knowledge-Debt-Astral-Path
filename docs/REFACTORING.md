# 重构评估落地状态板（2.1–2.4）

> 源自 2026-09-26 审计报告「二、重构评估」。本文回答：评估里的每一条，现在是什么状态。
> 完成即提交 GitHub；延期项写明启动条件。随重构推进更新本板。

## 2.1 可直接复用的资产（现状核对）

| 资产 | 状态 | 说明 |
|---|---|---|
| `AstralPath.Core`（纯函数库） | ✅ 在用 | score/impact/sale/K1-K5/图谱/OCR 后处理/画像/路由，112+ 金样锁死 |
| 金样体系 + `verify_constants.py` | ✅ 在用并扩展 | 新增 agent-intents 三方对拍、formula-cross 三端行为对拍 |
| `tools/*.py` 工具链 | ✅ 在用 | 13 本真实教材验证过 |
| 单文件 index.html | ✅ 作为行为规格 | 原生端重构的对照基准（ADR-0001） |
| `AstralPath.Persistence` / `Contracts` | ✅ 在用 | Postgres(知识库) + DTO 契约 |

## 2.2 跨平台适配风险（逐项消解状态）

| 风险 | 状态 | 处置 |
|---|---|---|
| Core 副本漂移 | ✅ 消除 | 离线 Core 改链接主源码（C1 修复），漂移结构性不可能 |
| 三种宿主形态 ×3 测试矩阵 | ◐ 缓解 | Monolith/Kotlin 壳为交付态（ADR-0001）；Desktop 子进程、AndroidApp 内嵌为遗留实验；`LocalApiHost` 下沉后内嵌形态可复用 |
| 无稳定仓储接口 | ✅ 消除 | `IAstralPathStore` 契约落地，控制器 7 文件 + Services 基类全部面向接口（本轮 2.3-②） |
| 公式三处实现仅常量对齐 | ✅ 消除 | `eval/golden/formula-cross.json`：Python 规范 × C# 测试 × JS(node) 三方行为对拍，tol 1e-9（本轮） |
| Android WebView 老旧设备 | ◐ 设计性缓解 | pdf.js 文本层优先 + WASM OCR 仅兜底；液态玻璃默认关闭 |

## 2.3 需抽象/下沉的公共模块

| # | 模块 | 状态 |
|---|---|---|
| ① | Core 单一化（消灭手工副本） | ✅ P0 完成（离线 Core 链接主源码） |
| ② | `IAstralPathStore` 仓储契约 | ✅ 本轮：接口落地 + 7 控制器/Services 基类/AstralPathModules 全部面向接口 + DI 注册；持久化形态=内存权威 + SQLite(WAL) 快照（`RuntimeSnapshotStore`，P1）+ Npgsql（知识库层）；Postgres 全量业务账本**延期**（需 schema 设计，启动条件=多实例部署需求） |
| ③ | OcrHost 下沉 | ✅ P2 完成（`Infrastructure/OcrHost.cs`，壳 277→128 行） |
| ④ | UI 单构建产物分发 | ✅ 等价达成：`sync-monolith-html.ps1`（同步+md5 门禁+C3 哨兵）+ `build-release.ps1` 一键管线（本轮加三道契约门禁前置）；Api/wwwroot 在线版标注为独立应用 |
| ⑤ | 意图词表单一事实源 | ✅ P2 完成（`tools/agent-intents.json` 三方对拍） |

## 2.4 分层与模块划分（现状）

```
L0 AstralPath.Core            ✅ 纯函数 + 金样（net10.0，被全端引用；离线工程链接同源）
L1 AstralPath.Infrastructure  ✅ SQLite(WAL) 快照 / OCR 宿主 / 认证 / 仓储契约 IAstralPathStore
L2 用例层                     ✅ 以「Api.Core 控制器（服务端）+ Offline LocalServices（离线端）」
                                 承担；独立 AstralPath.AppServices 程序集**延期**——启动条件=
                                 原生端出现第二个消费方（避免无消费者的空壳层）
L3a Windows 桌面              ✅ 近期形态=WebView2 壳（ADR-0001）；Avalonia+SkiaSharp=目标态（重构第三阶段）
L3b Android                   ✅ 交付态=Kotlin WebView 纯加载器壳（M6-b 后无语义注入）；原生页=第三阶段
L4 发布管线                   ✅ `release/build-release.ps1`：契约门禁（constants/agent-intents/formulas）
                                 → 三端同源+SHA256 守卫 → dotnet publish → Inno Setup/Gradle 产物
```

## 本轮（2.1–2.4 执行）新增物

- `src/AstralPath.Infrastructure/IAstralPathStore.cs` —— 仓储契约；`QuestionBankItem` 提升为命名空间级
- `eval/golden/formula-cross.json` + `tests/.../FormulaCrossTests.cs` + `scripts/verify_formulas.py` —— 公式三方行为对拍
- `release/build-release.ps1` 前置三道契约门禁
- Api.Tests `P15_Controllers_Resolve_IAstralPathStore`
