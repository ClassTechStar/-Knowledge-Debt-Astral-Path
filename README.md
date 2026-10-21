# 知债：星穹学途（Knowledge Debt: Astral Path）

跨课程的知识债诊断与修复智能体。用确定性公式找出你欠了哪门课的先修债，用约束满足生成能做完的还债计划，由受约束智能体陪着销账，教育伦理的红线由代码守。

> 2026 iCAN AI / DuMate 竞赛实现，版本 2.6.0（contract-v2.2.0-algo-v2），支持 Windows 10/11 与 Android 8.0 以上。
>
> 名称规范：全称「知债：星穹学途（Knowledge Debt: Astral Path）」，中文简称「知债：星穹学途」，代码标识 AstralPath。旧名「知债图 / ZhiZhaiTu」已废弃。

---

## 目录

1. [产品简介](#1-产品简介)
2. [交付物](#2-交付物)
3. [快速开始](#3-快速开始)
4. [功能模块](#4-功能模块)
5. [核心公式](#5-核心公式)
6. [系统架构](#6-系统架构)
7. [测试与门禁](#7-测试与门禁)
8. [开发者指南](#8-开发者指南)
9. [四人提示词与分工](#9-四人提示词与分工)
10. [演示账号与伦理](#10-演示账号与伦理)
11. [变更记录](#11-变更记录)

---

## 1. 产品简介

多门课交叉学习时，卡住你的往往不是「这一章没听懂」，而是前面某门课的先修概念没打好。这就是知识债。

| 步骤 | 说明 | 类比 |
|---|---|---|
| 诊断 | 找出「会计等式到借贷记账法」这类红边 | 体检找病灶 |
| 计划 | 14 天补课表，每日不超过 40 分钟，按 0/2/6 天间隔复习 | 开药方 |
| 练习 | 真题加信心校准，按状态自适应选题 | 吃药复查 |
| 销账 | 连续 2 次达标且累计作答不少于 3 次即结清 | 痊愈出院 |

三端 UI 同源，Web、Windows 与 Android 共用同一套设计令牌与业务语义。无微服务单体版可以完全离线运行，数据只落在本机。

## 2. 交付物

| 产物 | 路径 | 说明 |
|---|---|---|
| Web 单体版 | `deploy/monolith-web/index.html` | 599,264 字节，浏览器直接打开，全部功能可用 |
| 自包含单文件 | `deploy/monolith-web/astralpath-monolith.html` | 18,187,662 字节，OCR 全栈内联，零外部资源 |
| Windows 安装包 | `release/AstralPath-Monolith-Setup-2.6.0.exe` | WebView2 壳加载同一份 HTML，本机安装验收通过 |
| Windows 便携版 | `release/AstralPath-Portable-2.6.0/`（及同名 zip） | 免安装解压即用，.NET 10 自包含 |
| Android APK | `release/AstralPath-Android-2.6.0-Store.apk` | WebView 同构加离线核心，versionCode 36，商店签名 |
| 题库 | `eval/question_bank.json` | 100 题，覆盖 34 个知识点，难度 1:21 / 2:51 / 3:28 |
| 图包 | `graph-packs/astralpath-v2/graph_pack.json` | 34 节点 74 边，先修 50 条加迁移缺口 24 条，无环，涉及三门课 |
| 教材思维导图 | `kg-deep-test/*.mindmap.{json,md}` | 13 本教材 6,720 页，2,519 节点 2,662 边 |
| 工具链 | `tools/*.py` | OCR、章节、构图、思维导图全链路 |
| 项目方案 | `docs/03-知债星穹学途-合并版(TDS+最终版技术方案).md` | 含算法 v2 与第 29 至 33 节工程现状 |
| 四人分工 | `docs/知债星穹学途-四人团队分工方案.md` | 零基础通俗版 v3.0.0 |
| 分工详细附录 | `docs/附录-四人分工详细矩阵(RACI-模块-风险)-v2.1.0.md` | RACI、模块治理与风险应急 |
| 四人提示词 | `docs/vibe-prompts/提示词-P{1,2,3,4}-*.md` | v3，可直接粘贴给 AI 开发助手 |
| 阶段改造报告 | `docs/T20_阶段一安全网改造报告.md`、`docs/T21_阶段二加固与减熵改造报告.md` | 两阶段工程化改造的完整记录 |

## 3. 快速开始

### 3.1 单体版（零依赖，推荐）

```powershell
# Web：双击打开
deploy\monolith-web\index.html

# Windows：安装包或便携版
release\AstralPath-Monolith-Setup-2.6.0.exe
# 或者解压 release\AstralPath-Portable-2.6.0.zip 后运行 AstralPath.Monolith.exe
```

起点、藏书阁、识网、知债、今日、智能体、画像、账户各页的计算都在本机完成，数据存 localStorage，可以导出 JSON。

### 3.2 Android

安装 `release/AstralPath-Android-2.6.0-Store.apk`，商店签名。没有电脑、没有 adb 也能离线使用核心功能。

### 3.3 源码构建

```powershell
# 环境：.NET 10 · Android workload · JDK 17 · Gradle
dotnet test tests/AstralPath.Core.Tests -c Release
dotnet test tests/AstralPath.Persistence.Tests -c Release
dotnet test tests/AstralPath.Desktop.Tests -c Release
dotnet test tests/AstralPath.Api.Tests -c Release
dotnet test tests/AstralPath.Eval.Tests -c Release
```

`dotnet test` 一次只能接一个项目。

## 4. 功能模块

| 模块 | 做什么 |
|---|---|
| 起点 | 总览、快捷入口、资料数与开放债边统计 |
| 藏书阁 | PDF 与文本本机解析，转章节、关键词并出题；扫描版走离线 OCR |
| 识网 | 知识图谱 DAG（TextRank、PMI、依赖句式），瓶颈与关键路径，分层布局 |
| 知债 | impact-v2 红边、What-if 模拟、14 天计划（K1 至 K5 合约） |
| 今日 | 每天不超过 3 项任务，真题、信心 1 至 5、判分 |
| 智能体 | 意图路由、危机转人工、敏感词脱敏、读真实学情 |
| 试卷作业 | 拍照、截图、图片粘贴、文件导入，识别后自动结构化并生成知识点层级图谱 |
| 画像 | Brier 信心校准、风格五轴、k-匿名、opt-out |
| 教师 | 只读学情视图，需学生授权，含薄弱热点、红边清单与近七日趋势 |
| 账户 | 登录、consent、导出与重置 |

## 5. 核心公式

```text
score = min( 0.50·know + 0.30·retention + 0.20·prereqSupport , 0.55+0.45·min(prereq) )
know   = 0.85·Beta(成功,失败) + 0.15·(conf/5)
retention = exp(-age/S);  S = 2.5·(1+0.45·streak)·(1+0.25·ln(1+reps))

impact = 0.55 · σ((sf-0.55)·8) · σ((0.50-st)·8) · w · cross · (1+0.12·ln(1+下游)) · (1+0.08·ln(1+freq))
cross  = 1.0 | 1.15 (transfer_gap)；命中 freq>0 ∧ impact≥0.08

销账 ⇔ 近期连续 2 次达标 ∧ 累计 ≥3 次；Weighted=0.7·acc+0.3·(conf/5)≥0.65
```

实现位于 `src/AstralPath.Core/`，纯函数，1e-6 金样对拍。这几个数字只能由公式算出，LLM 不得改写；`cleared` 只能由 `SaleStateMachine` 写入。

## 6. 系统架构

```text
AstralPath.Core/          纯函数：公式 / 图 / OCR / 画像 / 销账 / 计划
AstralPath.Api/           ASP.NET Core（Web 与 Windows 共用）+ wwwroot/index.html
AstralPath.Desktop/       WebView2 壳
AstralPath.Native/        Android WebView 壳
AstralPath.Monolith/      无微服务 Windows 壳（Setup-2.6.0）
AstralPath.Mobile.Offline/ Avalonia 11 + SQLite 离线单体
AstralPath.Persistence/   Postgres（默认）+ memory 回退
tests/                    Core 114 · Persistence 8 · Desktop 51 · Api 70 · Eval 2 · MobileCore 84
```

持久化默认走 PostgreSQL 加 pgvector（`Persistence:Mode=postgres`），连接串可用 `Persistence__ConnectionString` 覆盖，连不上且 `AllowMemoryFallback=true` 时降级到内存实现。

## 7. 测试与门禁

| 套件 | 通过 | 说明 |
|---|---|---|
| Core.Tests | 114/114 | score、impact、sale、计划、图谱、走读金样（1e-6）与画像、智能体 v3 回归 |
| Persistence.Tests | 8/8 | Postgres 与 InMemory 双模式 |
| Desktop.Tests | 51/51 | Avalonia 原生 UI 绑定与导航 |
| Api.Tests | 70/70 | 上传、解析、诊断、计划、agent 与 P0/P1 回归 |
| Eval.Tests | 2/2 | 合成数据回归 |
| MobileCore.Tests | 84/84 | 图谱构图、布局、学习路径与 OCR 全流程 |
| 合计 | 329/329 | 零回归 |

一键验收：

```powershell
pwsh -NoProfile -File scripts\verify_all.ps1     # 17 步 ALL GREEN
```

脚本覆盖常量对齐、意图表六方对拍（26 意图）、公式跨语言对拍、前端脚本语法、XSS 黄金用例（14 例）、共享内核双跑等价、生成区一致性（5 区）、状态分片黄金用例（10 例）、零空 catch（181 个 .cs 文件）、命名规范扫描与三端 HTML 同源。

持续集成在 `.github/workflows/ci.yml`，每次 push 与 PR 自动跑构建、测试与上述门禁，另有覆盖率报告作业。首次启用时 CI 红了三处，均已修复：OcrHost 的 tessdata 兜底路径缺存在性检查、Windows runner 控制台 cp1252 导致中文输出崩溃、生成块比对未归一换行。

## 8. 开发者指南

```powershell
# 环境
dotnet --version        # ≥ 10
dotnet workload list    # android
java -version           # 17（打包 APK）

# 跑 API（可选全功能模式）
dotnet run --project src/AstralPath.Api --urls http://127.0.0.1:5190

# 打 Android APK（路径必须纯 ASCII，否则 AAPT2265）
$env:JAVA_HOME="C:\Temp\jdk-17"
& "C:\Gradle\bin\gradle.bat" -p src/AstralPath.Native assembleRelease

# 打 Windows 单体安装包
dotnet publish src/AstralPath.Monolith -c Release -r win-x64 --self-contained -o src/AstralPath.Monolith/publish
& "C:\Program Files\Inno Setup 7\ISCC.exe" src/AstralPath.Monolith/setup-monolith.iss
```

NuGet 报 `path1=null` 时，补齐 `PROGRAMDATA`、`PROGRAMFILES(X86)`、`ALLUSERSPROFILE` 三个环境变量，并设 `CheckEolWorkloads=false`。

前端共享代码的单一来源在 `shared/`，页面内联副本由 `python scripts/gen_frontend_regions.py` 刷进 `deploy/monolith-web/index.html` 的生成区，`--check` 是 CI 门禁。改完生成区要跑 `scripts/sync-monolith-html.ps1` 同步三端镜像，并重打 `scripts/build-single.py` 里的 `EXPECTED_MD5`（必须小写）。

## 9. 四人提示词与分工

零基础队友可以把提示词 v3 直接粘贴给 Cursor 或 Claude Code：

| 角色 | 文件 | 负责 |
|---|---|---|
| P1 | `docs/vibe-prompts/提示词-P1-产品与教育伦理.md` | 文案、禁词、危机词、隐私、演示脚本 |
| P2 | `docs/vibe-prompts/提示词-P2-算法与后端.md` | 公式、Core、API、金样 |
| P3 | `docs/vibe-prompts/提示词-P3-数据与评测.md` | 图包、题库、金样、工具链、验收脚本 |
| P4 | `docs/vibe-prompts/提示词-P4-客户端与体验.md` | 单文件 UI、安卓壳、Windows 安装包 |

分工边界与交接方式见 `docs/知债星穹学途-四人团队分工方案.md`。

## 10. 演示账号与伦理

演示账号是 `demo@astralpath.local` / `demo123456`，另有 `demo-student-a`。

有三条红线：数字只能算出来，AI 不许改分；不发明先修边；`cleared` 只由状态机写入。

隐私上，教师侧做 k-匿名，样本少于 3 人一律抑制；危机域与敏感域不进画像；用户随时可以 opt-out。危机词命中时只做安抚与转人工，不评价学生。

## 11. 变更记录

| 版本 | 日期 | 要点 |
|---|---|---|
| 2.6.0 | 2026-10-17 | 版本号全量统一（前端、C# 程序集、gradle、iss、微服务 tag、文档口径）；双端安装包经 GitHub Release v2.6.0 发布；构建链加签名环境自动加载与 tessdata 自动保障，双端打包可零人工干预自愈 |
| 2.6.0 工程化阶段一 | 2026-10-18 | CI 流水线与静态门禁（脚本解析级检查、跨站脚本黄金用例、零空 catch）；前端共享代码收敛到 `shared/` 单一来源；编排意图对拍由三方扩到六方，26 意图一致 |
| 2.6.0 工程化阶段二 | 2026-10-18 至 19 | 密钥信封 v2（轮换与吊销）；本地状态五分片存储，旧数据自动迁移留底；意图路由器 479 行拆到 281 行；运行期诊断环形缓冲与 `/health/ready`；前端构建合并，生成区加一键构建 |
| 2.6.0 纯前端同步 | 2026-10-19 | 主仓库前端改造反向移植到纯前端源目录，两树的 `index.html` 与自包含单文件产物逐字节一致 |
| 2.6.0 CI 修复 | 2026-10-19 | 修 OcrHost tessdata 兜底存在性检查、Windows runner 强制 Python UTF-8、生成块比对归一换行，CI 恢复全绿 |
| 2.5.0 | 2026-10-10 至 15 | AI 计划官与债务解读，K1 至 K5 合约引擎；试卷作业错题直拍识别与多格式导入；教师只读学情视图与作业单互传；内置演示密钥；思维链流式呈现与垫题秒出 |
| 2.2.0 | 2026-09-25 至 28 | RapidOCR 生产管线接入，三端 MLP 抽样验收 12 本 PDF 通过，算法 v3 与画像、智能体 v3 落地，审计修复与重构落地，测试 235 项全绿 |
| 2.0.0-algo-v2 | 2026-09-24 | score-v2、impact-v2、sale-v2，图谱 v2，OCR v2，画像 v2，100 题库与 34 节点 74 边图包，Postgres 默认持久化，Avalonia 原生 UI，无微服务单体版 |
| 1.4.x 至 1.5.0 | 2026-09 | 智能体接真实学情，启动预热，上传分片，WebView 同构壳，商店签名 |

---

© 2026 知债：星穹学途四人团队
