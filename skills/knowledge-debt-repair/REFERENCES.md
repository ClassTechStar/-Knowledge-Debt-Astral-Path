# 参考实现与文件索引（knowledge-debt-repair）

> 本文件是 `SKILL.md` 的配套索引，列出技能各能力对应的真实实现与验证证据，便于复刻与审计。
> 项目根：`-Knowledge-Debt-Astral-Path`（AstralPath v2.2.0，2026 iCAN AI / DuMate 竞赛作品「知债：星穹学途」）。

## 1. 通用约定

- **命名**：全称「知债：星穹学途（Knowledge Debt: Astral Path）」；代码标识 **AstralPath**；禁用旧名「知债图 / ZhiZhaiTu」。
- **金样精度**：公式金样 1e-6；跨语言对拍（Python / C# / JS）tol 1e-9。
- **单一事实源**：意图词表 `tools/agent-intents.json`；常量表 `src/AstralPath.Core/Formula/FormulaConstants.cs`；
  社交文案与禁词登记 `src/AstralPath.Core/Formula/FormulaWeights.cs` 的 bannedEthics —— 改动任一处必须同步其余各方并跑 `scripts/verify_agent_intents.py`。

## 2. 核心实现（C# / .NET 10）

| 能力 | 路径 | 职责 |
|---|---|---|
| score-v2 掌握度 | `src/AstralPath.Core/Formula/` | `MasteryCalculator`（know/retention/prereqSupport 合成） |
| impact-v2 债边 | `src/AstralPath.Core/Algorithms/DebtScanner.cs` | 红边检测：impact 公式 + 命中条件，禁 LLM 改分 |
| 销账状态机 | `src/AstralPath.Core/Algorithms/SaleStateMachine.cs` | `cleared` 唯一写入入口 |
| K1–K5 约束 | `src/AstralPath.Core/Planner/PlannerConstraintChecker.cs` | 计划约束检查器（违反即拒绝展示） |
| 14 天计划 | `src/AstralPath.Core/Algorithms/PlannerBuilder.cs` | impact/est_min 贪心 + 0/2/6 间隔 |
| 自适应选题 | `src/AstralPath.Core/Algorithms/QuestionScheduler.cs` | 按缺口与间隔调度题目 |
| 知识图谱 | `src/AstralPath.Core/Graph/` | DAG 无环校验、瓶颈/关键路径、布局 |
| OCR 清洗 | `src/AstralPath.Core/Ocr/` | 文本清洗、部首修复、乱码闸 |
| 画像 v3 | `src/AstralPath.Core/Profiling/UserProfiler.cs` | Brier 校准、风格五轴、真 k-匿名（<3 抑制） |
| 意图路由 v3 | `src/AstralPath.Core/Agent/AgentRouter.cs` | 22 意图运行表（DefaultAgentIntents.Table）、话题切换、省略指代 |
| 叙事门禁 | `src/AstralPath.Core/Narrative/NarrativeGuard.cs` | 禁词替换、危机词转人工、槽位校验 |

## 3. 服务端与客户端

| 目标 | 路径 |
|---|---|
| API（Web/Windows 共用） | `src/AstralPath.Api`、`src/AstralPath.Api.Core`（统一错误形状 `{data,error,traceId}`） |
| Web 单体（无服务器） | `deploy/monolith-web/index.html`（同构业务语义 + pdf.js 文本层 + WASM OCR 阶梯 + 41 项自测） |
| Windows 单体/便携 | `src/AstralPath.Monolith`、`release/AstralPath-Monolith-Setup-2.2.0.exe`；便携版由 `release/build-portable.ps1` 构建（README 声明交付 `release/AstralPath-Portable-2.2.0/`+zip，zip 本体未随仓库存，需本机构建产出） |
| Android | `src/AstralPath.Native`、`release/AstralPath-Android-2.2.0-Store.apk` |
| Avalonia 离线（MobileCore） | `src/AstralPath.Mobile.Offline`（图谱/OCR 84 项回归） |
| 持久化 | `src/AstralPath.Persistence`（PostgreSQL + pgvector 默认，memory 回退） |

## 4. 数据资产

| 资产 | 路径 | 规模 |
|---|---|---|
| 34 点图包 | `graph-packs/astralpath-v2/graph_pack.json` | 34 节点 / 74 边（先修 50 + 迁移缺口 24），无环，三门课 |
| 会计图包 | `graph-packs/accounting-v1/` | 36 KP / 50 边 |
| 100 题库 | `eval/question_bank.json`（同步 `deploy/monolith-web/question_bank.json`） | 34 知识点，难度 1:21 / 2:51 / 3:28，每题带 `why` |
| 教材真题 | `eval/textbook-questions.json` | 教材配套题 |
| 13 本教材图谱 | `kg-deep-test/*.mindmap.{json,md}` | 6,720 页 / 2,519 节点 / 2,662 边 |

## 5. 工具链（Python，tools/ 与 scripts/）

| 脚本 | 职责 |
|---|---|
| `tools/ocr_pipeline.py` | OCR 生产管线：页级并行 + 三抽取器交叉验证 + full 全书模式 + PROGRESS 流式进度 |
| `tools/table_extract.py` | 表格提取：管道/空白对齐/TSV → Markdown（P1-3） |
| `tools/kg_builder.py`、`tools/kg_algorithm.py` | 章节 → 关键词 → DAG 构图（TextRank/PMI/依赖句式） |
| `tools/deep_chapters.py`、`tools/deep_test_13pdfs.py` | 13 本教材全量章节图谱 |
| `tools/bench_ocr_engines.py`、`tools/ocr_engine_eval.py` | PaddleOCR vs RapidOCR 平行基准 |
| `tools/ocr_regression.py` | OCR 回归 12/12 PASS |
| `tools/agent-intents.json` | 意图/危机词/禁词/负向词登记表（唯一事实源） |
| `scripts/verify_formulas.py` | 公式三方对拍 |
| `scripts/verify_agent_intents.py` | 意图词表对拍 |
| `scripts/verify_constants.py` | 常量一致性 |
| `scripts/verify_all.ps1` | 一键全量回归 ALL GREEN（235+ 项） |

## 6. 文档与验证证据

| 文档 | 内容 |
|---|---|
| `README.md` | 产品简介、交付物、核心公式（v2）、架构、变更日志 |
| `docs/03-知债星穹学途-合并版(TDS+最终版技术方案).md` | 契约 + 算法 + 架构 + §8.6 Skill 四件套 + §29–§33 工程现状与复刻篇 |
| `docs/算法v3-图谱与OCR优化说明.md` | 图谱 v3 / OCR v3 细节 |
| `docs/算法v3-画像与智能体优化说明.md` | 画像 v3 / 智能体 v3 细节（Brier、意图门槛 A38/A39） |
| `docs/验收报告-全面功能与稳定性-2026-09-25.md` | 全面验收：自动化测试 222 → 235 项全绿、P0/P1–P3 修复记录 |
| `docs/mlp-acceptance-2026-09-28.md`、`docs/mlp-windows-acceptance-2026-09-28.md` | 12 本 PDF MLP 抽样：OCR 12/12 · 图谱 12/12 · 智能体 22 意图+危机词通过 |
| `docs/深度优化任务清单-2026-09-28.md` | P0–P3 深度优化清单与实施回写 |
| `wiki/00-目录与口径.md` | 术语与口径、禁词表 v1（P1+P2 双签） |
| `wiki/03-公式手算.md` | score/impact 手算金样 |
| `wiki/08-图谱对齐KG与mindmap.md` | 三元组/思维导图对齐口径 |
| `docs/vibe-prompts/提示词-P1..P4-*.md` | 四人 Vibe Coding 复刻提示词 v3（P1 产品伦理 / P2 算法后端 / P3 数据评测 / P4 客户端体验） |

## 7. 演示与验收

- 演示账号：`demo@astralpath.local` / `demo123456`（或 `demo-student-a`）
- 一键回归：`powershell -File scripts\verify_all.ps1`
- 发布管线：契约门禁（公式对拍 / 意图对拍 / 三端 md5 + XSS 哨兵）通过后才允许发版

## 8. 已知边界与缺口（复刻时注意）

- 便携包已裁 cv2/shapely/pip/多语言包，体积 392.5MB（约 -33%）；裁减版依赖宿主 OCR 桥。
- 真机（实体设备）验证未做（P1-7）；评测集 gt-vision 在，未扩 12×20（P2-5 部分）。
- 文档数字源（P3-2）仍手工维护。