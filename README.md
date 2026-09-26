# 知债：星穹学途（Knowledge Debt: Astral Path）

跨课程**知识债**诊断与修复智能体：用确定性公式找出你「欠」了哪门课的先修债，用约束满足生成可完成的还债计划，用受约束智能体陪伴销账，并守住教育伦理底线。

> **2026 iCAN AI / DuMate 竞赛实现** · 版本 **2.2.0**（contract-v2.2.0-algo-v2） · Windows 10/11 · Android 8.0+
>
> **项目名称规范**：全称「知债：星穹学途（Knowledge Debt: Astral Path）」；中文简称「知债：星穹学途」；代码标识 **AstralPath**。禁止旧名「知债图 / ZhiZhaiTu」。

---

## 目录

1. [产品简介](#1-产品简介)
2. [交付物一览](#2-交付物一览)
3. [快速开始](#3-快速开始)
4. [功能模块](#4-功能模块)
5. [核心公式（v2）](#5-核心公式v2)
6. [系统架构](#6-系统架构)
7. [测试与金样](#7-测试与金样)
8. [开发者指南](#8-开发者指南)
9. [四人 Vibe Coding 提示词](#9-四人-vibe-coding-提示词)
10. [演示账号与伦理](#10-演示账号与伦理)
11. [变更日志](#11-变更日志)

---

## 1. 产品简介

多门课交叉学习时，卡住往往不是「这一章没听懂」，而是**前面某门课的先修概念没打好**——这就是「知识债」。

知债：星穹学途会：

| 步骤 | 说明 | 类比 |
|------|------|------|
| **诊断** | 找出「会计等式 → 借贷记账法」这类红边 | 体检找病灶 |
| **计划** | 14 天补课表（每日 ≤40 分钟，0/2/6 间隔） | 开药方 |
| **练习** | 真题 + 信心校准 + 自适应选题 | 吃药复查 |
| **销账** | 连续 2 次达标且累计 ≥3 次 → 已还清 | 痊愈出院 |

**三端 UI 同源**：Web / Windows / Android 共用同一套设计令牌与业务语义；**无微服务单体版**可完全离线运行。

---

## 2. 交付物一览

| 产物 | 路径 | 说明 |
|------|------|------|
| **Web 单体版** | `deploy/monolith-web/index.html` | 全功能、无服务器、浏览器直接打开（2.2，73 KB） |
| **Windows 单体安装包** | `deploy/win-install/dist/AstralPath-Monolith-Setup-2.2.0.exe` | WebView2 壳 + 同一 HTML；与旧版并存 |
| **Windows 桌面安装包** | `deploy/win-install/dist/AstralPath-Setup-1.5.0-Desktop.exe` | 全功能桌面端（可选本地 API） |
| **Android APK** | `src/AstralPath.Native/dist/AstralPath-Android-2.2.0-Store.apk` | WebView 同构 + 离线核心 |
| **100 题库** | `eval/question_bank.json` | 覆盖 34 个知识点（难度 1:21 / 2:51 / 3:28） |
| **34 点图包** | `graph-packs/astralpath-v2/graph_pack.json` | 34 节点 / 74 边（先修 50 + 迁移缺口 24），无环，三门课 |
| **13 本教材思维导图** | `kg-deep-test/*.mindmap.{json,md}` | 6,720 页 / 2,519 节点 / 2,662 边 |
| **图谱 + OCR 工具链** | `tools/*.py`（8 个脚本，4,291 行） | OCR → 章节 → 构图 → 思维导图 |
| **项目方案（可复刻）** | `docs/03-知债星穹学途-合并版(TDS+最终版技术方案).md` | 含 §2.0 算法 v2 + **§29–§33 工程现状与完美复刻篇** |
| **四人分工** | `docs/知债星穹学途-四人团队分工方案.md` | v3.0.0 零基础通俗版 |
| **分工详细附录** | `docs/附录-四人分工详细矩阵(RACI-模块-风险)-v2.1.0.md` | RACI / 模块治理 / 风险应急 |
| **Vibe 提示词** | `docs/vibe-prompts/提示词-P{1,2,3,4}-*.md` | v3，可直接粘贴给 AI 开发 |

---

## 3. 快速开始

### 3.1 无微服务单体版（推荐 · 零依赖）

```powershell
# Web：双击打开
deploy\monolith-web\index.html

# Windows：运行安装包后桌面图标启动
deploy\win-install\dist\AstralPath-Monolith-Setup-2.2.0.exe
```

功能：起点/藏书阁/识网/知债/今日/智能体/画像/账户 全部在本机计算，数据存 localStorage，可导出 JSON。

### 3.2 全功能桌面版（可选本地 API）

```powershell
deploy\win-install\dist\AstralPath-Setup-1.5.0-Desktop.exe
```

### 3.3 Android

安装 `src/AstralPath.Native/dist/AstralPath-Android-2.2.0-Store.apk`（商店签名）。无电脑、无 adb 亦可离线使用核心功能。

### 3.4 源码构建

```powershell
# 环境：.NET 10 · Android workload · JDK17 · Gradle 9.7.1
dotnet test tests/AstralPath.Core.Tests -c Release      # 金样 11
dotnet test tests/AstralPath.Persistence.Tests -c Release
dotnet test tests/AstralPath.Desktop.Tests -c Release   # Avalonia 47
dotnet test tests/AstralPath.Api.Tests -c Release       # 53
dotnet test tests/AstralPath.Eval.Tests -c Release
```

---

## 4. 功能模块

| 模块 | 做什么 |
|------|--------|
| **藏书阁** | PDF/文本本机解析 → 章节 + 关键词 + 出题 |
| **识网** | 知识图谱 DAG（TextRank/PMI/依赖句式）、瓶颈/关键路径、分层布局 |
| **知债** | impact-v2 红边 · What-if · 14 天计划（K1–K5） |
| **今日** | ≤3 任务 + 真题 + 信心 1–5 + 判分 |
| **智能体** | 意图路由 · 危机转人工 · 敏感词脱敏 · 读真实学情 |
| **画像** | Brier 信心校准 · 风格五轴 · k-匿名 · opt-out |
| **账户** | 登录 · consent · 导出/重置 |

---

## 5. 核心公式（v2）

```text
score = min( 0.50·know + 0.30·retention + 0.20·prereqSupport , 0.55+0.45·min(prereq) )
know   = 0.85·Beta(成功,失败) + 0.15·(conf/5)
retention = exp(-age/S);  S = 2.5·(1+0.45·streak)·(1+0.25·ln(1+reps))

impact = 0.55 · σ((sf-0.55)·8) · σ((0.50-st)·8) · w · cross · (1+0.12·ln(1+下游)) · (1+0.08·ln(1+freq))
cross  = 1.0 | 1.15 (transfer_gap)；命中 freq>0 ∧ impact≥0.08

销账 ⇔ 近期连续 2 次达标 ∧ 累计 ≥3 次；Weighted=0.7·acc+0.3·(conf/5)≥0.65
```

- 实现：`src/AstralPath.Core/`（纯函数，1e-6 金样）
- **禁止 LLM 改数字**；`cleared` 只能由 `SaleStateMachine` 写入

---

## 6. 系统架构

```text
AstralPath.Core/          纯函数：公式 / 图 / OCR / 画像 / 销账 / 计划
AstralPath.Api/           ASP.NET Core（Web/Windows 共用）+ wwwroot/index.html
AstralPath.Desktop/       WebView2 壳
AstralPath.Native/        Android WebView 壳（~2.8MB）
AstralPath.Monolith/      无微服务 Windows 壳（Setup-2.2.0）
AstralPath.Mobile.Offline/Avalonia 11 + SQLite 离线单体
AstralPath.Persistence/  Postgres（默认）+ memory 回退
tests/                    Core 28 · Persistence 8 · Desktop 47 · API 66 · Eval 2 · **MobileCore 84（图谱/OCR）**
```

**持久化**：默认 **PostgreSQL + pgvector**（`Persistence:Mode=postgres`）；连接串可用 `Persistence__ConnectionString` 覆盖；连不上且 `AllowMemoryFallback=true` 时降级 memory。

---

## 7. 测试与金样

| 套件 | 通过 | 说明 |
|------|------|------|
| Core.Tests | **28/28** | score/impact/sale/计划/图/走读金样（1e-6）+ 画像/智能体 v3 回归 |
| Persistence.Tests | **8/8** | Postgres/InMemory 双模式 |
| Desktop.Tests | **47/47** | Avalonia 原生 UI 绑定与导航 |
| Api.Tests | **66/66** | 上传/解析/诊断/计划/agent + P0/P1 回归 |
| Eval.Tests | **2/2** | 合成数据回归 |
| MobileCore.Tests | **84/84** | 图谱构图/布局/学习路径 + OCR 全流程（v3 算法回归） |
| 单体 HTML 自测 | **41 项** | 结构/算法/页面流转 |

> `dotnet test` 一次只能接一个项目。全量请用一键脚本：
>
> ```powershell
> powershell -File scripts\verify_all.ps1     # 235 项 → ALL GREEN
> ```

---

## 8. 开发者指南

```powershell
# 环境
dotnet --version        # ≥ 10
dotnet workload list    # android
java -version           # 17（打包 APK）

# 跑 API（可选全功能模式）
dotnet run --project src/AstralPath.Api --urls http://127.0.0.1:5190

# 打 Android APK（纯 ASCII 路径，否则 AAPT2265）
$env:JAVA_HOME="C:\Temp\jdk-17"
& "C:\Gradle\bin\gradle.bat" -p src/AstralPath.Native assembleRelease

# 打 Windows 单体安装包
dotnet publish src/AstralPath.Monolith -c Release -r win-x64 --self-contained -o src/AstralPath.Monolith/publish
& "C:\Program Files\Inno Setup 7\ISCC.exe" src/AstralPath.Monolith/setup-monolith.iss
```

**NuGet 排障**：若报 `path1=null`，补齐 `PROGRAMDATA` / `PROGRAMFILES(X86)` / `ALLUSERSPROFILE`，并设 `CheckEolWorkloads=false`。

---

## 9. 四人 Vibe Coding 提示词

零基础队友可直接把提示词（v3）粘贴给 Cursor / Claude Code：

| 角色 | 文件 | 负责 |
|------|------|------|
| P1 | `docs/vibe-prompts/提示词-P1-产品与教育伦理.md` | 文案、禁词、危机词、隐私、演示脚本 |
| P2 | `docs/vibe-prompts/提示词-P2-算法与后端.md` | 公式、Core、API、金样 |
| P3 | `docs/vibe-prompts/提示词-P3-数据与评测.md` | 图包、题库、金样、工具链、验收脚本 |
| P4 | `docs/vibe-prompts/提示词-P4-客户端与体验.md` | 单文件 UI、安卓壳、Windows 安装包 |

分工边界与交接见 `docs/知债星穹学途-四人团队分工方案.md`（v3.0.0）。

---

## 10. 演示账号与伦理

- 账号：`demo@astralpath.local` / `demo123456`（或 `demo-student-a`）
- **红线**：数字只能算出来；AI 不许改分；不发明先修边；`cleared` 只由状态机写
- **隐私**：教师侧 k-匿名（&lt;3 抑制）；危机/敏感域禁入画像；可随时 opt-out
- **危机词**：只安抚 + 转人工，不评价学生

---

## 11. 变更日志

### 2.2.0 · 审计修复与重构落地（2026-09-26）
- **安全**：图谱先修链 XSS 转义（三端同源）；清除全部机器硬编码路径；鉴权默认开启 + 教师角色门禁；
  CORS 去 `null`；上传限流 60/min、请求体 2GB→512MB
- **持久化**：运行时快照换 SQLite（WAL）单行幂等 upsert，旧 JSON 自动迁移；教材正文迁 IndexedDB，
  静默截断改显式报警
- **架构**：离线 Core 链接主源码（副本漂移清零）；OcrHost / LocalApiHost 下沉（壳 277→128 行）；
  `IAstralPathStore` 仓储契约，控制器/微服务/模块全部接口化；`tools/agent-intents.json` 意图词表单一事实源
- **性能**：`scanDebts` 修订号缓存（7 处调用经 `save()` 统一失效）
- **体验**：GalReview 半径/缓动/负字距对齐、对比度达 WCAG AA、触控 44px、方向路由动效、液态玻璃可选开关
- **门禁**：三端 md5 + XSS 哨兵、agent-intents 三方对拍、公式三方行为对拍（Python/C#/JS，tol 1e-9）、
  发布管线前置契约门禁
- 测试 **277 项全绿**；详见 `docs/REFACTORING.md`、`docs/adr/ADR-0001-打包路线对齐.md`

### 2.2.0 · P2/P3 修复（2026-09-25）
- **P2-1 鉴权与越权**：新增访问控制中间件（Bearer 解析 + 学生维路由归属校验 403 + consent 主体以令牌为准，
  杜绝「替他人授权」）；`Security:RequireAuth` 可开启强制登录
- **P2-2 Swagger**：去掉 `[FromForm] IFormFile` 触发的问题，`swagger.json` 500 → **200**
- **P2-3 错误形状统一**：415/405/404 框架级错误改写为统一 `{data,error,traceId}`
- **P2-4 前端负例对齐 v3**：「好的，帮我生成计划」不再被吞成兜底话术；纯客套仍正确兜底（三端同步）
- **P2-5 opt-out 生效**：关闭后今日改按章节顺序、隐藏推断风格轴（原只翻转布尔值）
- **P2-6 上传白名单**：扩展名 + 魔数 + 可读率三级校验（原 `.exe` 也收且解析报 ready）
- **P2-7 上传目录**：移出 `bin/` 到 `%LOCALAPPDATA%\AstralPath\uploads`，旧内容自动迁移
- **P2-8 CORS**：`AllowAnyOrigin` → 回环任意端口 + 安卓壳白名单
- **P3**：练习选项可键盘操作（radio 语义）/ 销账检查改内联反馈 / `save()` 失败顶栏提示 /
  入参长度上限 / 不存在学生 404 / 注册冲突 409 / 镜像 tag 参数化 / 内联 favicon / 首访示例数据引导
- 详见 `docs/验收报告-全面功能与稳定性-2026-09-25.md` 第十一节
- **画像 v3**：波动率按正确率归一（超额波动，不再误判 acc≈0.5 的学生）、新增 ECE 校准曲线、
  兴趣时间衰减、真 k-匿名（逐个标签判样本量）、修复伪 Laplace（噪声不再由 value 驱动）
- **智能体 v3**：启用三个从未使用的常量（TauExec/TauClarify/MaxClarify）、多锚点饱和累加 + 覆盖率、
  修复"好的"二字吞掉整句请求、槽位填充可收敛、支持话题切换与省略指代
- 详见 `docs/算法v3-图谱与OCR优化说明.md`、`docs/算法v3-画像与智能体优化说明.md`

### 2.2.0 · 算法 v3（2026-09-25）
- **知识图谱 v3**：TextRank 稀疏化（O(V²)→O(V+E)，词表上限）；修复「所有术语挂同一章」的锚定 bug；
  依赖句式正则一次编译；PMI → 平滑 + 归一化 NPMI；子词去重；DAG 收尾；布局改多趟重心 + 交叉数择优
- **OCR v3**：混淆修复增加标识符保护（不再把 `Win10` 改成 `WinlO`）；新增跨页页眉页脚去除、
  英文断字还原、断行合并的列表/标题保护；TSV 行聚类改间隙聚类（抗基线漂移）；投票改模糊匹配
- 新增 **39 项** v3 回归测试（图谱/OCR 22 + 画像/智能体 17）→ **222 项全绿**
  （Core 28 + Persistence 8 + Desktop 47 + API 53 + Eval 2 + MobileCore 84），已纳入 `verify_all.ps1`

### 2.2.0 · 验收修复（2026-09-25）
- **P0 修复**：补完 `Api → Api.Core` 重构（引用链 + slnx 登记 + 隐式 using + Android 引用指向 Api.Core）→ 全量编译 0 错误
- **P1 修复 · 畸形输入不再 500**：`InvalidModelStateResponseFactory` 统一 400 + 9 个 `[FromBody]` 接口补判空
  （修复前 4 个接口含登录接口收到畸形 JSON 直接 `NullReferenceException`）
- **P1 修复 · 运行时状态可持久化**：接入 `AddAstralPathPersistence`；
  新增 `RuntimeSnapshot`（学生/作答/掌握度/计划/consent/知识库/画像/智能体轮次 JSON 快照，
  原子写 + 防抖 + 周期兜底 + 优雅关闭 Flush + 版本与图包校验，**测试宿主自动关闭**）
- **P1 修复 · 降级可见性**：启动显式 WARN；`/health/ready` 报告持久化模式与快照状态
- 新增 13 项回归测试 → **235 项全绿**；详见 `docs/验收报告-全面功能与稳定性-2026-09-25.md` 第十节

### 2.2.0（2026-09-25）
- **知识图谱 + OCR 工具链**：`tools/` 8 个脚本（4,291 行），OCR → 章节 → 构图 → 思维导图全链路
- **13 本教材全量验证**：6,720 页 / 4,015,232 字符 / 2,519 节点 / 2,662 边，**13/13 全部 ready**
- **13 份思维导图产物**：`kg-deep-test/*.mindmap.{json,md}` + `kg_report.json`
- **修复 2.2.0**：解析 → 构图 → 识网全链路、藏书阁/智能体回归、稳健性与三端包
- 三端安装包同步至 2.2.0（Web HTML / Windows Setup / Android Store APK）
- 文档：主方案新增 **§29–§33 工程现状与完美复刻篇**；分工方案重写为 v3.0.0 零基础通俗版；四份提示词升级 v3

### 2.1.0（2026-09-24）
- 发布无微服务三端安装包（Web HTML + Windows Setup + Android APK）

### 2.0.0-algo-v2（2026-09-24）
- 算法 v2：score-v2 / impact-v2 / sale-v2 / PlannerBuilder / QuestionScheduler
- 知识图谱 v2：TextRank/PMI/依赖句式、瓶颈/关键路径、Sugiyama 布局
- OCR v2：预处理、三 PSM 投票、TSV 置信度、阅读顺序
- 画像 v2：Brier、风格五轴、k-匿名、Laplace 轻量噪声
- **100 题 question_bank.db** + **34/74 无环图包**
- **Postgres 默认持久化**（可回退 memory）
- **Avalonia 原生 UI**（12 axaml + VM）
- **无微服务单体版**：index.html + Setup-2.0.0.exe
- 121 项测试全绿

### 1.4.x–1.5.0
- 智能体接真学情；启动预热；上传 2GB/分片；WebView 同构壳；badge 全绿；商店签名

---

© 2026 知债：星穹学途四人团队
