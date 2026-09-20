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
| Contracts | `src/AstralPath.Contracts` | DTO、错误码与统一响应封装（唯一契约出口） |
| Persistence | `src/AstralPath.Persistence` | 生产级持久化：PostgreSQL + pgvector（HNSW）+ 全文索引 + RRF 混合检索；可切换内存模式 |
| Services | `src/AstralPath.Services` | §19 扩展服务的**单一路由来源**与独立服务宿主模板（ServiceHost） |
| 独立服务 | `services/*`（10 个） | §19.11 拆分后的独立进程/容器，各自独立配置与数据库 |
| Infrastructure | `src/AstralPath.Infrastructure` | 演示内存仓库、双学生种子、consent 缓存、§44/§45/§46 模块仓库、材料与 OCR 流水线 |
| Api | `src/AstralPath.Api` | 统一契约 API + 演示 Web UI（`/`） |
| Shared | `src/AstralPath.Shared` | 双端共享 UI 层：ViewModels / Views（`.axaml`）/ 主题样式 / 数据源抽象 / 导航 / 转换器 |
| Desktop / Mobile | `src/AstralPath.Desktop` · `src/AstralPath.Mobile` | Avalonia 11 双端客户端（桌面三栏 + 移动底部 Tab），见「客户端（Avalonia 双端 UI）」 |
| Tests | `tests/*` | 金样例 + 召回 + 端到端 + Headless UI |

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

切换到真实 PostgreSQL（需 Docker）：

```bash
# 启动 PostgreSQL + pgvector（pgvector 0.8.6，支持 HNSW）
docker run -d --name astralpath-pg -e POSTGRES_PASSWORD=astralpath -e POSTGRES_DB=astralpath \
  -p 55432:5432 pgvector/pgvector:pg16

# 以 postgres 模式运行（默认仍是 memory，用于本地调试）
export Persistence__Mode=postgres
export Persistence__ConnectionString="Host=127.0.0.1;Port=55432;Database=astralpath;Username=postgres;Password=astralpath"
dotnet run --project src/AstralPath.Api -c Release
```

独立服务（§19.11）示例：

```bash
export ASTRALPATH_GRAPH_PACK="<repo>\graph-packs\accounting-v1"
dotnet run --project services/concept-diffusion-svc -c Release --urls http://127.0.0.1:5191
# 自身路由 200；其他服务路由 404（路由已按服务隔离）
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://127.0.0.1:5191/v1/diffusion/simulate \
  -H "Content-Type: application/json" -d '{"studentId":"demo-student-a","intervention":{"K02":20}}'
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://127.0.0.1:5191/v1/cohorts/stats \
  -H "Content-Type: application/json" -d '{"k":5,"scores":[1,2,3,4,5,6]}'
```

自检脚本：

```bash
bash scripts/smoke_all_endpoints.sh              # 需先启动 API；覆盖 100+ 路由与前端页面
python scripts/naming_consistency.py --check     # 命名一致性（CI 门禁，残留须为 0）
```

## 客户端（Avalonia 双端 UI）

桌面端与移动端共用 `AstralPath.Shared` 的 ViewModel / View / 样式，只有外壳（Shell）不同。

```powershell
dotnet run --project src/AstralPath.Desktop -c Release          # 桌面三栏（1440×900）
dotnet run --project src/AstralPath.Desktop -c Release -- --demo # 控制台复算自检（成功退出码 0）
dotnet run --project src/AstralPath.Mobile  -c Release          # 手机形态预览（390×844 窗口）
```

### 页面清单（方案 §11–§17）

| 页面 | 桌面 | 移动 | 要点 |
|---|---|---|---|
| 今日 | ✅ | ✅ 启动页 | 35 分钟预算胶囊 + 任务卡（含「为什么做这个」）+ 逐日切换 |
| 图谱 | ✅ | ✅ 债边简图 | 自绘深色画布（`#0F0F1A`）、Top-N 诊断、节点选中 Story 卡 |
| 计划 | ✅ | — | 14 天甘特、每日预算校验、K1–K5 违规逐条列出 |
| 练习 | ✅ | ✅ | 单选作答 + **信心滑条 1–5 必填**（未填则提交禁用）+ 判题卡 |
| 债边 | ✅ | ✅ | 状态筛选、销账状态机校验、颜色 + 线型双编码 |
| 进度 | ✅ | ✅ | 掌握度分布、band 中文标签、明确「不对学生做分数排名」 |
| 教师视图 | ✅ | — | **fail-closed**：无授权时显示「已撤销授权：教师视图已更新为空。」 |
| What-if | ✅ | — | 4 参数实时推演，与真实 impact 并排对比（容差 1e-6） |
| 我的画像 | ✅ | ✅ | 雷达 / 时间线 / 标签云 / 热力图四件套；opt-out 时**全部隐藏** |
| 知识库 | ✅ | — | 文档列表、可见性徽章、检索、新建 |
| 设置 | ✅ | — | 色盲安全配色 / 高对比 / 减少动画 / 触达尺寸 |

### 交互与可访问性

- **响应式断点**：宽度 ≥1180px 显示左导航 + 内容 + 右信息栏；<900px 收起两侧只留内容与底部 Tab。
- **快捷键**（桌面）：`Ctrl+1` / `Ctrl+2` 切演示学生、`Ctrl+D` 图谱诊断、`Ctrl+T` 教师视图、`Ctrl+R` 撤销授权、`Ctrl+P` 计划、`Ctrl+K` 知识库。
- **键盘与读屏**：图谱画布可聚焦，方向键在节点间移动、`Home`/`End` 跳首尾；所有交互元素带 `AutomationProperties.Name`，状态条为 `Polite` LiveRegion。
- **触达尺寸**：桌面 ≥44px、移动 ≥48px（移动端由 `Mobile/App.axaml` 覆盖）。
- **不靠颜色单独表意**：债边状态用「颜色 + 线型」，掌握度用「颜色 + 中文 band 标签」，热力图/雷达图/时间线一律同时给出数值文本；提供色盲安全三色（`#1B9E77` / `#D95F02` / `#7570B3`）。
- **伦理硬约束**：不展示精确分数排名、不按分数给学生排序、无「处分/惩罚」措辞，每页带免责页脚。

### 数据源

视图模型只依赖 `IAppDataSource`，默认实现为 `OfflineDemoDataSource`（进程内复用 Core 纯函数与 `AstralPathStore`，与 API 同源，零外部依赖）。公式类计算直接调用 Core，因此「手算 = API = 端上显示」在离线模式下同样成立。切换为 `ApiAppDataSource` 即可走 HTTP 契约（该实现尚未编写，见「未完成范围」）。

### 移动端构建（诚实标注）

默认 `TargetFramework` 为 `net10.0`，以「手机形态」运行同一套 UI（全部移动端视图与响应式布局可用，可被 Headless 测试覆盖）。启用 Android 打包需先以管理员身份安装工作负载，再打开条件开关：

```powershell
dotnet workload install wasm-tools          # 需管理员提权
dotnet build src/AstralPath.Mobile -c Release -p:EnableAndroidHead=true
```

未安装该工作负载时 `Android/` 目录不参与编译，`net10.0` 构建不受影响。

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
- 持久化实现：`src/AstralPath.Persistence`（`Persistence:Mode = memory | postgres`；postgres 模式自动建 pgvector + HNSW + GIN 全文索引）
- Kubernetes 清单：`deploy/k8s/`（Namespace / ConfigMap / Secret 占位 / Postgres StatefulSet / 10 个服务 Deployment+Service / Ingress / HPA + PDB）
- 混沌演练：`deploy/chaos/`（`pod-kill.sh`、`db-outage.sh`，含预期结果与回滚）
- 可观测性：`deploy/observability/`（OTel Collector、Prometheus 告警规则）
- 运维手册：`deploy/RUNBOOK.md`
- 环境变量统一使用 `ASTRALPATH_` 前缀（如 `ASTRALPATH_GRAPH_PACK`、`ASTRALPATH_OCR_PYTHON`、`ASTRALPATH_TESSERACT`）。旧 `ZZ_*` 前缀已废弃，启动时若检测到会打印迁移告警。

## 测试

```text
Core.Tests      11  金样 score/impact/K/sale/narrative + 图包无环 + 召回 ≥0.80 + OCR/材料流水线
Eval.Tests       2  合成 30 学生画像 + 公式纯函数守护
Api.Tests       53  端到端：诊断/计划/今日/consent 撤销 purge/销账/what-if/材料与题库/账户
                       §44 智能体 · §45 知识库（含分片直传与片段）· §46 画像 · §19 扩展服务
Desktop.Tests   47  Headless UI（Avalonia.Headless.XUnit）：全页面导航与视图解析、宽/窄屏响应式、
                       触达尺寸、页脚与伦理措辞扫描、11 个页面 VM 的行为硬约束（信心必填、
                       fail-closed、opt-out 隐藏可视化、销账校验…）、图谱布局确定性与 400 节点 <50ms
Persistence.Tests  8  持久化：内存仓储、嵌入确定性、RRF 公式、切分、SchemaSql 断言
                           + 真实 PostgreSQL 集成（pgvector/HNSW/全文/RRF，需 ASTRALPATH_PG_CONN）
合计           121  全部通过（dotnet test AstralPath.slnx -c Release）
```

Postgres 集成用例在无数据库环境下会**显式跳过**（打印 SKIPPED），不会误判为失败。

另：`scripts/smoke_all_endpoints.sh` 覆盖 100+ 路由与前端页面的端到端自检（真实动态 ID 与真实凭据，不使用假数据）。

## 已完成（本轮交付）

- **独立微服务拆分**（§19.11）：十个扩展服务拆为独立进程/容器，各自独立配置与数据库；路由按服务隔离（实测自身 200、他服务 404），单体行为等价由 53 项端到端测试验证。
- **生产级持久化**：PostgreSQL + pgvector（HNSW）+ GIN 全文 + RRF 混合检索；`memory` / `postgres` 双模式可切换；真实容器集成测试通过。
- **K8s / 混沌 / 可观测性**（§23–§25）：部署清单、混沌演练脚本、OTel 与 Prometheus 告警规则已产出并通过语法校验。

## 未完成范围（诚实标注）

1. **`ApiAppDataSource`（客户端 HTTP 数据源）**（§11.2 / §16）：客户端已完整实现 UI 与数据源抽象，默认走进程内 `OfflineDemoDataSource`；连接真实 API 的 HTTP 实现尚未编写。
2. **Android 打包**：移动端 UI 与响应式布局已实现并通过 Headless 测试，但 `net10.0-android` 目标需要管理员安装 `wasm-tools` 工作负载后以 `-p:EnableAndroidHead=true` 构建。
3. **K8s 清单未经集群验证**：`deploy/k8s/` 已完成 YAML 语法校验（17/17），但本机无集群，未做服务端 schema 校验与实际部署；首次上集群前请执行 `kubectl apply --dry-run=server`。
4. **混沌与可观测性为"配置就绪"**：脚本与采集配置已产出并通过 `bash -n` 检查，但未在真实集群执行过演练。

以上均不影响当前演示链路的完整性与可验证性（构建 0 错误 0 警告 · **121 测试通过** · 端到端自检 93 项全绿 · 双端客户端可启动）。

## 页脚合规声明

本系统仅用于教学辅助与学习规划，不构成处分依据。演示数据均为合成数据。
