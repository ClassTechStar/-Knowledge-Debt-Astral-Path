# 知债：星穹学途（Knowledge Debt: Astral Path）

跨课程**知识债**诊断与修复智能体：用确定性公式找出你「欠」了哪门课的先修债，用约束满足生成可完成的还债计划，用受约束智能体陪伴销账，并守住教育伦理底线。

> **2026 iCAN AI / DuMate 竞赛实现** · 版本 **2.0.0-algo-v2** · Windows 10/11 · Android 8.0+
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
| **Web 单体版** | `deploy/monolith-web/index.html` | 全功能、无服务器、浏览器直接打开 |
| **Windows 单体安装包** | `deploy/win-install/dist/AstralPath-Monolith-Setup-2.0.0.exe` | WebView2 壳 + 同一 HTML；与旧版并存 |
| **Windows 桌面安装包** | `deploy/win-install/dist/AstralPath-Setup-1.5.0-Desktop.exe` | 全功能桌面端（可选本地 API） |
| **Android APK** | `src/AstralPath.Native/dist/AstralPath-WebUI-1.5.0.apk` | WebView 同构 + 离线核心 |
| **100 题库** | `eval/question_bank.db` / `question_bank.json` | 会计 34 / Python 33 / DL 33 |
| **34 点图包** | `graph-packs/astralpath-v2/graph_pack.json` | 34 节点 / 74 边，无环，三门课 |
| **项目方案（可复刻）** | `docs/03-知债星穹学途-合并版(TDS+最终版技术方案).md` | 含 §2.0 算法 v2 + 零基础导读 |
| **四人分工** | `docs/知债星穹学途-四人团队分工方案.md` | 含算法 v2 工作包 |
| **Vibe 提示词** | `docs/vibe-prompts/` | P1–P4 可直接粘贴给 AI 开发 |

---

## 3. 快速开始

### 3.1 无微服务单体版（推荐 · 零依赖）

```powershell
# Web：双击打开
deploy\monolith-web\index.html

# Windows：运行安装包后桌面图标启动
deploy\win-install\dist\AstralPath-Monolith-Setup-2.0.0.exe
```

功能：起点/藏书阁/识网/知债/今日/智能体/画像/账户 全部在本机计算，数据存 localStorage，可导出 JSON。

### 3.2 全功能桌面版（可选本地 API）

```powershell
deploy\win-install\dist\AstralPath-Setup-1.5.0-Desktop.exe
```

### 3.3 Android

安装 `src/AstralPath.Native/dist/AstralPath-WebUI-1.5.0.apk`（商店签名）。无电脑、无 adb 亦可离线使用核心功能。

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
AstralPath.Monolith/      无微服务 Windows 壳（Setup-2.0.0）
AstralPath.Mobile.Offline/Avalonia 11 + SQLite 离线单体
AstralPath.Persistence/  Postgres（默认）+ memory 回退
tests/                    Core 11 · Persistence 8 · Desktop 47 · API 53 · Eval 2
```

**持久化**：默认 **PostgreSQL + pgvector**（`Persistence:Mode=postgres`）；连接串可用 `Persistence__ConnectionString` 覆盖；连不上且 `AllowMemoryFallback=true` 时降级 memory。

---

## 7. 测试与金样

| 套件 | 通过 | 说明 |
|------|------|------|
| Core.Tests | **11/11** | score/impact/sale/计划/图/走读金样（1e-6） |
| Persistence.Tests | **8/8** | Postgres/InMemory 双模式 |
| Desktop.Tests | **47/47** | Avalonia 原生 UI 绑定与导航 |
| Api.Tests | **53/53** | 上传/解析/诊断/计划/agent |
| Eval.Tests | **2/2** | 合成数据回归 |
| 单体 HTML 自测 | **41 项** | 结构/算法/页面流转 |

```powershell
dotnet test tests/AstralPath.Core.Tests tests/AstralPath.Persistence.Tests `
  tests/AstralPath.Desktop.Tests tests/AstralPath.Api.Tests tests/AstralPath.Eval.Tests -c Release
```

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

零基础队友可直接把提示词粘贴给 Cursor / Claude Code：

| 角色 | 文件 | 负责 |
|------|------|------|
| P1 | `docs/vibe-prompts/提示词-P1-产品与教育伦理.md` | 文案、禁词、隐私、演示脚本 |
| P2 | `docs/vibe-prompts/提示词-P2-算法与后端.md` | 公式、Core、金样 |
| P3 | `docs/vibe-prompts/提示词-P3-数据与评测.md` | 图包、题库、验收脚本 |
| P4 | `docs/vibe-prompts/提示词-P4-客户端与体验.md` | UI、壳、安装包 |

---

## 10. 演示账号与伦理

- 账号：`demo@astralpath.local` / `demo123456`（或 `demo-student-a`）
- **红线**：数字只能算出来；AI 不许改分；不发明先修边；`cleared` 只由状态机写
- **隐私**：教师侧 k-匿名（&lt;3 抑制）；危机/敏感域禁入画像；可随时 opt-out
- **危机词**：只安抚 + 转人工，不评价学生

---

## 11. 变更日志

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
