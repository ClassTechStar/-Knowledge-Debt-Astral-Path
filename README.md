# 知债：星穹学途（Knowledge Debt: Astral Path）

跨课程知识债诊断与修复智能体 —— 2026 iCAN AI / DuMate 竞赛实现。

## 项目位置

`C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path`

## 架构

| 层 | 项目 | 职责 |
|---|---|---|
| Core | `src/AstralPath.Core` | BASELINE 纯函数：score / impact / K1–K5 / 销账 / 禁词 / 教练 |
| Graph | `src/AstralPath.Graph` | 图包加载、无环校验、版本化 |
| Contracts | `src/AstralPath.Contracts` | DTO 与错误码 |
| Infrastructure | `src/AstralPath.Infrastructure` | 演示内存仓库、双学生种子、consent 缓存 |
| Api | `src/AstralPath.Api` | 统一契约 API + 演示 Web UI |
| Shared | `src/AstralPath.Shared` | 双端共享演示元数据 |
| Tests | `tests/*` | 金样例 + 召回 + 端到端 |

## BASELINE 公式（金样 1e-6）

```text
score  = 100 × (0.6×recent_acc + 0.3×sev_norm + 0.1×(self_conf/5))
sev_norm = 1 − min(sev, 1)

impact = freq × (50 − score_c) × recency × edge.weight
recency = 1 / (1 + days_since_last_error / 7)
触发：score_p < 40 ∧ score_c < 50 ∧ freq > 0

销账：cleared ⟺ 连续 2 次 quiz acc≥0.7 ∧ conf≥3
仅 progress-svc 可写 cleared
```

## 快速开始

```powershell
$env:DOTNET_ROOT = "C:\Program Files\dotnet"
$env:NUGET_PACKAGES = "C:\Temp\ngp"
Set-Location "C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path"

dotnet build AstralPath.slnx -c Release
dotnet test  AstralPath.slnx -c Release --no-build
dotnet run --project src/AstralPath.Api -c Release --urls http://127.0.0.1:5190
```

打开 `http://127.0.0.1:5190/` 使用演示台（图谱 / 诊断 / 计划 / 今日任务 / What-if / 教师端）。

Swagger：`http://127.0.0.1:5190/swagger`

## 演示账号

| ID | 说明 |
|---|---|
| `demo-student-a` | 王小明 · 有债预埋（≥3 红边） |
| `demo-student-b` | 李华 · 无债对照 |
| `demo-teacher` | 教师端 · 默认仅可见已授权学生 |

## 主 API

| 方法 | 路由 |
|---|---|
| POST | `/v1/students/{id}/ingest/scores` |
| POST | `/v1/students/{id}/diagnose?graph_ver=1` |
| GET | `/v1/students/{id}/graph-view?graph_ver=1` |
| POST | `/v1/students/{id}/plans` |
| POST | `/v1/students/{id}/today` |
| POST | `/v1/attempts` |
| POST | `/v1/debt-edges/sale-check` |
| GET | `/v1/teachers/{id}/hotspots` |
| POST | `/v1/consents/{studentId}/grant\|revoke` |
| POST | `/v1/graphs/accounting-v1/validate` |
| POST | `/v1/what-if` |
| POST | `/v1/demo/advance` |

## 架构铁律落地

- C1：score/impact/K/销账 = 确定性代码，金样 CI 阻断
- C2：LLM 不改数字（演示叙事走模板 + 禁词门禁）
- C3：图包 publish 前无环校验，边必须有 source
- C4：计划 `constraints_checked=true` 才返回 201
- C8：cleared 仅 progress 路径写入
- C6/C9：教师端 fail-closed + 禁词扫描

## 测试

```text
Core.Tests   11  金样 score/impact/K/sale/narrative + 图包无环 + 召回 ≥0.80 + OCR/材料流水线
Eval.Tests    2  合成 30 学生画像 + 公式纯函数守护
Api.Tests    20  端到端：诊断/计划/今日/consent 撤销 purge/销账/what-if/材料与题库/账户
合计          33  全部通过（dotnet test AstralPath.slnx -c Release）
```
