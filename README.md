# 知债：星穹学途（Knowledge Debt: Astral Path）

跨课程**知识债**诊断与修复智能体：用确定性公式找出你「欠」了哪门课的先修债，用约束满足生成可完成的还债计划，用受约束智能体陪伴销账，并守住教育伦理底线。

> **2026 iCAN AI / DuMate 竞赛实现** · 版本 **1.3.0** · Windows 10/11

> **项目名称规范（全文唯一口径）**
>
> - 全称：**知债：星穹学途（Knowledge Debt: Astral Path）** — 文档标题、封面、申报书、答辩与对外介绍  
> - 中文简称：**知债：星穹学途** — 正文叙述、章节标题与图表标签  
> - 英文标识：**AstralPath** — 命名空间、解决方案名、包名、镜像名、数据库名与域名  

---

## 目录

1. [产品简介](#1-产品简介)  
2. [快速开始](#2-快速开始)  
3. [新手五分钟上手](#3-新手五分钟上手)  
4. [功能模块](#4-功能模块)  
5. [核心公式（BASELINE）](#5-核心公式baseline)  
6. [系统架构](#6-系统架构)  
7. [API 一览](#7-api-一览)  
8. [演示账号](#8-演示账号)  
9. [开发者与测试](#9-开发者与测试)  
10. [常见问题](#10-常见问题)  
11. [伦理与合规](#11-伦理与合规)  
12. [未完成范围](#12-未完成范围诚实标注)

---

## 1. 产品简介

多门课交叉学习时，卡住往往不是「这一章没听懂」，而是**前面某门课的先修概念没打好**——这就是「知识债」。

知债：星穹学途会：

| 模块 | 做什么 |
|------|--------|
| **藏书阁** | 教材 PDF → 全文抽取 / OCR → 完整目录与章节正文 |
| **识网** | 自动生成知识图谱（思维导图），侧栏可读各章正文并**按章自动出题** |
| **知债** | BASELINE 红边诊断 + 14 天还债计划 + What-if 模拟 |
| **今日** | 约 35 分钟教材真题与练习（可轮换题库） |
| **智能体** | 意图路由；危机转人工；敏感词脱敏 |
| **画像** | 能力雷达、掌握度、学习时间线、CSR 图索引 |

**桌面端与 Web 端功能、界面布局、交互与数据处理逻辑完全一致**（桌面使用 WebView2 内嵌同一套 Web 页面与本地 API）。

---

## 2. 快速开始

### 2.1 Windows 安装版（推荐）

1. 运行安装包：`deploy/win-install/dist/AstralPath-Setup-1.3.0-Desktop.exe`  
2. 按向导安装（默认 `C:\Program Files\AstralPath`）  
3. 可选安装 **Microsoft Edge WebView2 运行时**（Win10 必需，Win11 通常已自带）  
4. 启动桌面图标「知债：星穹学途」，状态栏显示 **「已就绪」** 即可使用  

也可使用 Web 演示台：安装后启动本地服务，浏览器打开 `http://127.0.0.1:5190/`（或安装包内「启动 Web 演示台」）。

### 2.2 源码运行（开发者）

```powershell
cd <仓库根目录>          # 例如 C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path
dotnet build AstralPath.slnx -c Release
dotnet test  AstralPath.slnx -c Release --no-build

# 启动 API + Web UI
dotnet run --project src/AstralPath.Api -c Release -- --urls http://127.0.0.1:5190
```

- 演示台：<http://127.0.0.1:5190/>  
- Swagger：<http://127.0.0.1:5190/swagger>  
- 离线演示页：`materials/demo-ui/index.html`（无服务时可预览界面）

> **注意**：不要在 `appsettings.json` 中写死 `"urls"`，否则会覆盖命令行 / 环境变量指定的端口，导致桌面壳「本地服务启动失败」。

### 2.3 自检脚本

```bash
bash scripts/smoke_all_endpoints.sh              # 需先启动 API；覆盖 100+ 路由
python scripts/naming_consistency.py --check     # 命名一致性（CI 门禁）
```

---

## 3. 新手五分钟上手

**步骤 1 · 启动**  
安装版点桌面图标；源码版见上文命令。状态「已就绪」后继续。

**步骤 2 · 藏书阁导入教材**  
点顶部 **「藏书阁」** → **「导入示例资料」**（约 12 本），或 **「上传并解析」** 选择自己的 PDF。  
状态出现绿色 `ready` 即解析完成（大书需数十秒）。

**步骤 3 · 识网看图谱与章节**  
点 **「识网」** → 切换教材卡片 → 画布为**完整目录**思维导图。  
右侧**章节侧栏**点击任一章：查看正文摘要，并完成 **本章自动出题**（含选项与答案）。

**步骤 4 · 知债看红边与计划**  
点 **「知债」**：课程包 BASELINE 红边 + 诊断叙事；教材识网红边；右侧 **What-if** 拖滑块；点 **「生成/刷新 14 天计划」**（任一天 ≤35 分钟）。

**步骤 5 · 今日做题**  
点 **「今日」** → **「加载教材真题」** → 答题并调信心条 → **「换一批题」** 轮换。

**步骤 6 · 问智能体**  
点 **「智能体」**，试试：

| 输入 | 系统行为 |
|------|----------|
| 帮我诊断知识债 | 诊断意图 + 还债建议 |
| 生成14天计划 | 生成修复计划 |
| 我这周学不完 | 压力支持话术 |
| 危机相关表述 | **自动转人工（crisis.handoff）** |

---

## 4. 功能模块

### 4.1 藏书阁（资料库）

| 能力 | 说明 |
|------|------|
| 上传 PDF | 支持多选；文本层优先**全书抽取** |
| 扫描版 OCR | 可选 Tesseract（`chi_sim+eng`）；有文本层则不依赖 OCR |
| 深度解析 | 全文字符 · 完整目录 · 章节正文 · **按章自动出题** |
| 示例教材 | Kotlin / Python / Java / Go / C# / 深度学习系列 / AI Agent / 传记等 |

状态：`parsing` → `ready` / `failed`。

### 4.2 识网（知识图谱）

| 能力 | 说明 |
|------|------|
| 思维导图 | 章 / 节 / 小节完整入图，DAG 无环 |
| 章节侧栏 | 点章看正文 + 自动出题 |
| 教材真题 | 每书约 12 题，支持轮换 |
| 导出 | Markdown 大纲 / RDF 三元组 |
| 布局 | 分层图谱 / 逻辑结构图；节点拖拽、画布缩放平移 |

### 4.3 知债诊断

| 能力 | 说明 |
|------|------|
| 红边列表 | BASELINE impact + 诊断叙事 |
| 教材债边 | 识网 preview-debts（如「神经网络→反向传播」） |
| 14 天计划 | K1–K5 约束检查；任一天 ≤35 分钟 |
| What-if | 实时模拟 score / freq / days → impact |
| 教师热点 | consent fail-closed，未授权不进热点 |

### 4.4 今日任务

教材真题优先；选项 + 信心滑条；支持换一批、重置题序、Demo +1 天。教练节奏：概念卡 → 桥接题 → 小测。

### 4.5 智能体（受约束）

意图路由 · 危机转人工 · 敏感词脱敏 · 按角色工具白名单 · 会话状态（响应 / 澄清 / 人工）。

### 4.6 学习画像

能力雷达（掌握度 / 正确率 / 练习量 / 还债进度 / 信心）· 掌握度表 · 时间线 · Opt-out · CSR 图索引。

---

## 5. 核心公式（BASELINE）

金样精度 **1e-6**，实现为确定性纯函数：

```text
score  = 100 × (0.6×recent_acc + 0.3×sev_norm + 0.1×(self_conf/5))
sev_norm = 1 − min(sev, 1)

impact = freq × (50 − score_c) × recency × edge.weight
recency = 1 / (1 + days_since_last_error / 7)
触发：score_p < 40 ∧ score_c < 50 ∧ freq > 0

销账：cleared ⟺ 连续 2 次 quiz acc≥0.7 ∧ conf≥3
仅 progress 路径可写 cleared
```

§19.1 传播模型（`ConceptDiffusion`）：α=0.55、D_max=6、N≤400，结果必带  
`truncationBound = ‖b⁰‖·α^(D+1)/(1−α)`。

---

## 6. 系统架构

| 层 | 项目 | 职责 |
|----|------|------|
| Core | `src/AstralPath.Core` | BASELINE 纯函数；扩展服务算法；受约束智能体路由 |
| Graph | `src/AstralPath.Graph` | 图包加载、无环校验、版本化 |
| Contracts | `src/AstralPath.Contracts` | DTO 与错误码（唯一契约出口） |
| Infrastructure | `src/AstralPath.Infrastructure` | 材料 OCR 流水线、题库、演示仓库、consent、模块仓库 |
| Api | `src/AstralPath.Api` | REST API + Web UI（`wwwroot/index.html`） |
| Desktop | `src/AstralPath.Desktop` | WebView2 桌面壳（与 Web **同构**） |
| Mobile | `src/AstralPath.Mobile` | 移动端项目骨架 |
| Shared | `src/AstralPath.Shared` | 双端共享演示元数据 |
| Tests | `tests/*` | 金样例 + 召回 + 端到端 |
| Tools | `tools/*.py` | OCR 全文、深度章节、图谱算法 |

数据与部署：

- PostgreSQL DDL：`deploy/sql/001_init.sql`  
- 运维手册：`deploy/RUNBOOK.md`  
- 环境变量统一前缀 `ASTRALPATH_*`（旧 `ZZ_*` 已废弃，启动时告警）  
- Windows 安装包：`deploy/win-install/dist/AstralPath-Setup-1.3.0-Desktop.exe`

---

## 7. API 一览

### 主链路

| 方法 | 路由 |
|------|------|
| POST | `/v1/students/{id}/ingest/scores` |
| POST·GET | `/v1/students/{id}/diagnose?graph_ver=` |
| GET | `/v1/students/{id}/graph-view` · `/mastery` |
| POST | `/v1/students/{id}/plans` · GET `/v1/plans/{id}` · POST `/v1/plans/{id}/rebalance` |
| POST | `/v1/students/{id}/today` · `/v1/attempts` · `/v1/debt-edges/sale-check` |
| GET | `/v1/teachers/{id}/hotspots` |
| POST·GET | `/v1/consents/{studentId}` |
| POST | `/v1/graphs/{packId}/validate` · `/v1/what-if` · `/v1/demo/advance` |
| GET | `/v1/question-banks` · `/v1/questions/{id}` |
| GET·POST | `/v1/materials`（upload / parse / chapters / textbook-questions / seed-samples / parse-all / today-from-books 等） |
| GET | `/v1/knowledge-graphs` · `/{graphId}` · `/{id}/csr` |
| POST·GET | `/api/v1/auth/*`（register / sessions / me / profile） |

### 受约束智能体（§44）

`POST /v1/agent/turns` · `GET|DELETE /v1/agent/sessions/{id}` · `GET /v1/agent/tools?role=` · `GET /v1/agent/goldens/{id}`

### 知识库（§45）

`/v1/kb/documents`（CRUD / tags / versions / publish / rollback / archive）· `/v1/kb/search` · `/v1/kb/uploads` · `/v1/kb/chunks/{id}` · `/internal/v1/kb/import`

### 用户画像（§46）

`/v1/profile/{id}` · `/features` · `/tags` · `/radar` · `/timeline` · `POST .../tags` · `POST .../opt-out`

### 扩展服务（§19）

concept-diffusion · exam-impact · peer-cohort · study-group · micro-lesson · learning-velocity · spaced-review · prerequisite-simulator · knowledge-forecast · lab-bench（路由见实现）

### 模块自检

`GET /health/ready` · `/api/meta` · `/api/demo/students` · `/v1/modules/status`

---

## 8. 演示账号

| 账号 / ID | 说明 |
|-----------|------|
| `demo@astralpath.local` / `demo123456` | 学生登录 |
| `teacher@astralpath.local` / `teacher123` | 教师登录 |
| `demo-student-a` | 王小明 · 有债预埋（≥3 红边），推荐演示 |
| `demo-student-b` | 李华 · 无债对照 |
| `demo-teacher` | 教师端 · 默认仅可见已授权学生 |

---

## 9. 开发者与测试

```text
Core.Tests   11  金样 score/impact/K/sale/narrative + 图包无环 + 召回 ≥0.80 + OCR 流水线
Eval.Tests    2  合成学生画像 + 公式纯函数守护
Api.Tests    53  端到端：诊断/计划/今日/consent/销账/what-if/材料与题库/账户
               §44 智能体 · §45 知识库 · §46 画像 · §19 扩展服务
合计         66  全部通过（dotnet test AstralPath.slnx -c Release）
```

架构铁律：

- **C1** score/impact/K/销账 = 确定性代码，金样 CI 阻断  
- **C2** LLM 不改数字（模板叙事 + 禁词门禁）  
- **C3** 图包 publish 前无环校验，边必须有 source  
- **C4** 计划 `constraints_checked=true` 才返回 201  
- **C6/C9** 教师端 fail-closed + 禁词扫描  
- **C8** cleared 仅 progress 路径写入  
- **§19.3/§19.4/§19.9** 强伦理：k-匿名、不排名、不用明文分数、外推 >30 天抑制；越权一律 404  

---

## 10. 常见问题

**Q：桌面窗口空白或提示「本地服务启动失败」？**  
A：请使用端口修复后的安装包（`appsettings.json` 不得写死 `urls`）。日志：`%LOCALAPPDATA%\AstralPath\api-stdout.log`。

**Q：扫描版 PDF 解析内容很少？**  
A：安装 [Tesseract](https://github.com/UB-Mannheim/tesseract) 与 `chi_sim+eng` 语言包，`traineddata` 放到 `api/tools/tessdata/`。带文本层的 PDF 无需 OCR 即可完整解析。

**Q：教师看不到学生？**  
A：故意设计：consent fail-closed，需学生授权后教师才可见。

**Q：销账（cleared）条件？**  
A：连续 2 次小测 正确率 ≥0.7 且信心 ≥3；仅 progress 路径可写入。

**Q：桌面与 Web 是否一致？**  
A：一致。桌面为 WebView2 内嵌同一 `wwwroot/index.html` 与同一本地 API。

---

## 11. 伦理与合规

本系统**仅用于教学辅助与学习规划，不构成处分依据**。

- 教师可见性 **consent fail-closed**  
- 敏感词脱敏；危机内容自动转人工  
- 同伴 / 预测类能力不排名、不用明文分数、k-匿名  
- 演示数据均为合成数据  

---

## 12. 未完成范围（诚实标注）

以下为方案中的长期项，非本次交付覆盖范围：

1. **Avalonia 双端完整 UI**（§11–§17）：当前桌面端为 **WebView2 与 Web 同构壳**；Avalonia 原生 `.axaml` 界面仍为骨架。  
2. **独立微服务部署**（§19.11）：十个扩展服务为单进程纯函数实现，契约一致，尚未拆分为独立容器与库。  
3. **生产级持久化**：已提供 DDL，默认仍为内存演示仓库。  
4. **K8s / 混沌 / 可观测性**：已有 Runbook 与部分清单，未在真实集群完成演练。

以上不影响当前演示链路完整性（构建通过 · 测试全绿 · 端到端自检可跑）。

---

**知债：星穹学途四人团队** · © 2026 · [仓库](https://github.com/ClassTechStar/-Knowledge-Debt-Astral-Path)
