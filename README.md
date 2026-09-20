# 知债：星穹学途（Knowledge Debt: Astral Path）

《知债：星穹学途》（Knowledge Debt: Astral Path）是一个跨课程知识债诊断与修复智能体——用确定性公式找出你“欠”了哪门课的先修债，用约束满足生成可完成的还债计划，用受约束智能体陪你一步步销账，同时守住教育伦理底线。

2026 iCAN AI / DuMate 竞赛实现。

> **项目名称规范（全文唯一口径）**
> - 全称：**知债：星穹学途（Knowledge Debt: Astral Path）** —— 用于文档标题、封面、申报书、答辩材料、对外介绍。
> - 中文简称：**知债：星穹学途** —— 用于正文叙述、章节标题与图表标签。
> - 英文标识：**AstralPath** —— 用于代码命名空间、解决方案名、包名、镜像名、数据库名与域名（如 `AstralPath.slnx` / `AstralPath.Core` / `api.astralpath.local`）。



## 架构

| 层 | 项目 | 职责 |
|---|---|---|
| Core | `src/AstralPath.Core` | BASELINE 纯函数：score / impact / K1–K5 / 销账 / 禁词 / 教练；§19 扩展服务算法；§44 受约束智能体路由 |
| Graph | `src/AstralPath.Graph` | 图包加载、无环校验、版本化 |
| Contracts | `src/AstralPath.Contracts` | DTO 与错误码（唯一契约出口） |
| Infrastructure | `src/AstralPath.Infrastructure` | 演示内存仓库、双学生种子、consent 缓存、§44/§45/§46 模块仓库、材料与 OCR 流水线 |
| Api | `src/AstralPath.Api` | 统一契约 API + 演示 Web UI（`/`） |
| Shared | `src/AstralPath.Shared` | 双端共享演示元数据 |
| Desktop / Mobile | `src/AstralPath.Desktop` · `src/AstralPath.Mobile` | 跨端客户端**项目骨架**（桌面/移动形态已立项，UI 层待实现，见“未完成范围”） |
| Tests | `tests/*` | 金样例 + 召回 + 端到端 |

## BASELINE 公式（金样 1e-6）

```text
score  = 100 × (0.6×recent_acc + 0.3×sev_norm + 0.1×(self_conf/5))
sev_norm = 1 − min(sev, 1)

impact = freq × (50 − score_c) × recency × edge.weight
recency = 1 / (1 + days_since_last_error / 7)
触发：score_p < 40 ∧ score_c < 50 ∧ freq > 0

销账：cleared ⟺ 连续 2 次 quiz acc≥0.7 ∧ conf≥3
仅 progress 路径可写 cleared
```

§19.1 传播模型（另见 `ConceptDiffusion`）：α=0.55、D_max=6、N≤400，结果必带 `truncationBound = ‖b⁰‖·α^(D+1)/(1−α)`。

## 快速开始

```powershell
$env:DOTNET_ROOT = "C:\Program Files\dotnet"
$env:NUGET_PACKAGES = "C:\Temp\ngp"
Set-Location "C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path"

dotnet build AstralPath.slnx -c Release
dotnet test  AstralPath.slnx -c Release --no-build
dotnet run --project src/AstralPath.Api -c Release --urls http://127.0.0.1:5190
```

打开 <http://127.0.0.1:5190/> 使用演示台（起点 / 藏书阁 / 识网 / 知债 / 今日 / What-if / 教师端 / 账户）。
Swagger：<http://127.0.0.1:5190/swagger>

自检脚本：

```bash
bash scripts/smoke_all_endpoints.sh              # 需先启动 API；覆盖 100+ 路由与前端页面
python scripts/naming_consistency.py --check     # 命名一致性（CI 门禁，残留须为 0）
```

## 演示账号

| ID | 说明 |
|---|---|
| `demo-student-a` | 王小明 · 有债预埋（≥3 红边） |
| `demo-student-b` | 李华 · 无债对照 |
| `demo-teacher` | 教师端 · 默认仅可见已授权学生 |

账户登录：`demo@astralpath.local` / `demo123456`；教师：`teacher@astralpath.local` / `teacher123`。

## API

### 主链路

| 方法 | 路由 |
|---|---|
| POST | `/v1/students/{id}/ingest/scores` |
| POST·GET | `/v1/students/{id}/diagnose?graph_ver=` |
| GET | `/v1/students/{id}/graph-view?graph_ver=` |
| GET | `/v1/students/{id}/mastery` |
| POST | `/v1/students/{id}/plans` |
| GET | `/v1/plans/{id}` · POST `/v1/plans/{id}/rebalance` |
| POST | `/v1/students/{id}/today` |
| POST | `/v1/attempts` |
| POST | `/v1/debt-edges/sale-check` |
| GET | `/v1/teachers/{id}/hotspots` |
| POST | `/v1/consents/{studentId}/grant \| revoke` · GET `/v1/consents/{studentId}` |
| POST | `/v1/graphs/{packId}/validate` |
| POST | `/v1/what-if` |
| POST | `/v1/demo/advance` |
| GET | `/v1/question-banks` · GET `/v1/questions/{id}` |
| GET·POST | `/v1/materials`（含 upload / upload-batch / parse / generate-graph / tasks / textbook-questions / seed-samples / parse-all / today-from-books） |
| GET | `/v1/knowledge-graphs` · `/v1/knowledge-graphs/{graphId}` · `/v1/knowledge-graphs/{id}/csr` |
| POST | `/api/v1/auth/register \| sessions \| logout \| material-today` · GET·PUT `/api/v1/auth/me \| profile` |

### §44 受约束智能体

| 方法 | 路由 |
|---|---|
| POST | `/v1/agent/turns` |
| GET·DELETE | `/v1/agent/sessions/{sessionId}` |
| GET | `/v1/agent/tools?role=` |
| GET | `/v1/agent/goldens/{goldenId}` |

### §45 知识库

| 方法 | 路由 |
|---|---|
| POST | `/v1/kb/documents` |
| GET·PATCH | `/v1/kb/documents/{id}` |
| PUT | `/v1/kb/documents/{id}/tags` |
| POST·GET | `/v1/kb/documents/{id}/versions` |
| POST | `/v1/kb/documents/{id}/publish \| rollback \| archive` |
| GET | `/v1/kb/tags` |
| POST | `/v1/kb/search` |
| POST | `/v1/kb/uploads` · POST `/v1/kb/uploads/{uploadId}/commit` |
| GET | `/v1/kb/chunks/{chunkId}?docId=` |
| POST | `/internal/v1/kb/import` |

### §46 用户画像

| 方法 | 路由 |
|---|---|
| GET | `/v1/profile/{studentId}` · `/features` · `/tags` · `/radar` · `/timeline` |
| POST | `/v1/profile/{studentId}/tags` · `/v1/profile/{studentId}/opt-out` |

### §19 扩展服务

| 服务 | 路由 |
|---|---|
| concept-diffusion（§19.1） | POST `/v1/diffusion/simulate` |
| exam-impact（§19.2） | POST `/v1/exams/{examId}/impact` · `/v1/exams/{examId}/preexam-plan` |
| peer-cohort（§19.3，**强伦理**） | POST `/v1/cohorts/stats` |
| study-group（§19.4，**强伦理**） | POST `/v1/study-groups/match` |
| micro-lesson（§19.5） | POST `/v1/micro-lessons/draft` · `/assemble` · GET `/v1/micro-lessons/{id}` |
| learning-velocity（§19.6） | POST `/v1/velocity/fit` |
| spaced-review（§19.7） | POST `/v1/spaced-review/schedule` |
| prerequisite-simulator（§19.8） | POST `/v1/prereq-simulator/simulate` |
| knowledge-forecast（§19.9，**强伦理**） | POST `/v1/forecast/student` |
| lab-bench（§19.10） | POST `/internal/v1/lab/experiments` · `/{id}/run` · GET `/{id}/report` · POST `/internal/v1/lab/formula-versions` · `/{v}/promote` |

### 模块自检

| 方法 | 路由 |
|---|---|
| GET | `/v1/modules/status` |
| GET | `/health/ready` · `/api/meta` · `/api/demo/students` |

## 架构铁律落地

- C1：score/impact/K/销账 = 确定性代码，金样 CI 阻断
- C2：LLM 不改数字（演示叙事走模板 + 禁词门禁；微课装配拦截结论性数字，放行排期时长）
- C3：图包 publish 前无环校验，边必须有 source
- C4：计划 `constraints_checked=true` 才返回 201
- C6/C9：教师端 fail-closed + 禁词扫描
- C8：cleared 仅 progress 路径写入
- §19.3/§19.4/§19.9 强伦理：k-匿名、不排名、不用明文分数、外推 >30 天即抑制；越权一律 404（不泄漏资源是否存在）

## 数据与部署

- PostgreSQL 初始 DDL：`deploy/sql/001_init.sql`（图包 / 掌握度 / 债边 / 计划 / 尝试 / consent / 知识库 / 画像 / 扩展服务 / 审计）
- 运维手册：`deploy/RUNBOOK.md`
- 环境变量统一使用 `ASTRALPATH_` 前缀（如 `ASTRALPATH_GRAPH_PACK`、`ASTRALPATH_OCR_PYTHON`、`ASTRALPATH_TESSERACT`）。旧 `ZZ_*` 前缀已废弃，启动时若检测到会打印迁移告警。

## 测试

```text
Core.Tests   11  金样 score/impact/K/sale/narrative + 图包无环 + 召回 ≥0.80 + OCR/材料流水线
Eval.Tests    2  合成 30 学生画像 + 公式纯函数守护
Api.Tests    53  端到端：诊断/计划/今日/consent 撤销 purge/销账/what-if/材料与题库/账户
                       §44 智能体 · §45 知识库（含分片直传与片段）· §46 画像 · §19 扩展服务
合计         66  全部通过（dotnet test AstralPath.slnx -c Release）
```

另：`scripts/smoke_all_endpoints.sh` 覆盖 100+ 路由与前端页面的端到端自检（真实动态 ID 与真实凭据，不使用假数据）。

## 未完成范围（诚实标注）

方案描述的以下部分尚未实现，属**数月级**工作量，非单次交付可覆盖：

1. **Avalonia 双端客户端 UI**（§11–§17）：`AstralPath.Desktop` / `AstralPath.Mobile` 目前仅为项目骨架（控制台入口），尚未引入 Avalonia 与 `.axaml` 界面。
2. **独立微服务拆分**（§19.11）：十个扩展服务当前以**单进程内的纯函数服务**实现，契约与拥有边界与独立部署形态一致，但未拆分为独立进程/容器与独立数据库。
3. **生产级持久化**：DDL 已提供，默认仍为内存演示仓库，未接入真实 PostgreSQL / HNSW 向量索引与 RRF 混合检索。
4. **K8s / 混沌 / 可观测性落地**（§23–§25）：仅有 Runbook，未产出部署清单与演练脚本。

以上均不影响当前演示链路的完整性与可验证性（构建 0 错误 0 警告 · 66 测试通过 · 端到端自检全绿）。

## 页脚合规声明

本系统仅用于教学辅助与学习规划，不构成处分依据。演示数据均为合成数据。
