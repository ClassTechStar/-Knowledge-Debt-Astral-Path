---
name: knowledge-debt-repair
description: >-
  跨课程「知识债（先修缺口）」诊断与修复智能体技能。当学习者出现「某门课学不懂、总是卡住、
  前面基础不牢」等场景，或需要为多门交叉课程生成可完成的补课计划、每日自适应练习、
  先修债销账判定与班级热点简报时使用。核心能力：确定性公式检测先修债边（detect-prereq-debt）、
  按 K1–K5 约束生成 14 天修复计划（build-repair-plan）、按今日作答与信心调度明日练习
  （adapt-drill）、在 consent 过滤器之后聚合班级热点给教师（counselor-brief）。
  本技能来自 2026 iCAN AI / DuMate 竞赛作品「知债：星穹学途（AstralPath）」v2.2.0，
  已取得 235+ 项全量回归全绿与 12 本教材 MLP 抽样验收。适用：教育科技产品、学习管理系统、
  自适应学习平台中「先修依赖 / 债务诊断 / 补课规划 / 学情预警」类业务。
  # 触发词：知识债、先修债、学不懂、卡住了、补课计划、14天计划、销账、债边、先修缺口、知识缺口、班级热点、学习预警
---

# 跨课程知识债诊断与修复（Knowledge Debt: Repair）

## 1. 定位与业务痛点

多门课交叉学习时，卡住往往不是「这一章没听懂」，而是前面某门课的先修概念没打好——这就是「知识债」。
本技能把 AstralPath 验证有效的「诊断 → 计划 → 练习 → 销账」闭环沉淀为可复用工作流：

| 阶段 | 做什么 | 类比 |
|---|---|---|
| **诊断** | 找出「会计等式 → 借贷记账法」这类红边（先修债） | 体检找病灶 |
| **计划** | 14 天补课表（每日 ≤40 分钟，0/2/6 间隔，K1–K5 约束检查） | 开药方 |
| **练习** | 真题 + 信心校准 + 自适应选题 | 吃药复查 |
| **销账** | 连续 2 次达标且累计 ≥3 次 → 已还清（状态机唯一入口） | 痊愈出院 |

**确定性边界（红线，不可违反）**：

- 数字只能算出来：`score` / `impact` / 销账判定一律由公式与状态机计算，**禁止 LLM 改数字**
- **不发明先修边**：债边必须来自图包 `kp_edges`，禁止 AI 臆造知识依赖
- `cleared` 只能由销账状态机（SaleStateMachine）写入
- 违反 K1–K5 的计划不得展示（宁可返回 `DEGRADED_MANUAL_LIST` 手动清单）
- 无 consent 的学生不得进入任何教师侧聚合

## 2. 输入与输出

### 输入

| 字段 | 说明 | 来源 |
|---|---|---|
| `mastery[]` | 每知识点掌握度记录（成功/失败次数、信心 1–5、最近作答时间） | 学习系统作答流水 |
| `kp_edges[]` | 知识图谱先修边（from_kp → to_kp，含权重与类型：先修 50 / 迁移缺口 24） | 图包 `graph_pack.json` |
| `errors[]` | 错误频次（可按知识点聚合） | 练习判分结果 |
| `kp_meta` | 知识点名、预估时长 `est_min`、难度 | 题库 / 图包 meta |
| `minutes` | 每日时间预算（默认 35，上限 40） | 用户设置 |
| `consent` | 学生授权开关（布尔，默认 false） | 账户模块 |

### 输出

| 阶段 | 输出 | 校验要求 |
|---|---|---|
| 诊断 | `debt_edges[] + impact` | impact 必须公式，禁 LLM |
| 计划 | `Plan (constraints_checked=true)` | 违反 K1–K5 不得展示 |
| 练习 | 明日调整建议（题目、信心校准） | 无 attempt 不得调高 mastery |
| 简报 | `hotspots + 三句话教师口径` | 仅 consent 通过后的聚合，k-匿名（<3 抑制） |

## 3. 四阶段工作流

> 任何阶段都先过本节「0. 前置」，再按需进入 1–4；阶段间有依赖，禁止跳步或并行写同一状态。

### 0. 前置：数据与授权

1. 校验输入 schema（`mastery`、`kp_edges`、`errors` 字段齐全，`consent` 为布尔）。
2. 校验图包：无环（GRAPH_WOULD_CYCLE 拒绝）、边必须有 `weight/source`。
3. 未授权路径：学生侧数据仅本人可见；教师侧聚合必须经 consent 过滤，样本 <3 抑制输出。
4. 伦理门禁：任何叙事/文案先过禁词表与危机词表（见 §6）。

### 1. detect-prereq-debt（债边诊断）

输入 `mastery[] + kp_edges[] + errors[]`，逐边计算：

```text
score = min( 0.50·know + 0.30·retention + 0.20·prereqSupport , 0.55+0.45·min(prereq) )
know   = 0.85·Beta(成功,失败) + 0.15·(conf/5)
retention = exp(-age/S);  S = 2.5·(1+0.45·streak)·(1+0.25·ln(1+reps))

impact = 0.55 · σ((sf-0.55)·8) · σ((0.50-st)·8) · w · cross · (1+0.12·ln(1+下游)) · (1+0.08·ln(1+freq))
cross  = 1.0 | 1.15 (transfer_gap)；命中条件：freq>0 ∧ impact≥0.08
```

- `sf` = 先修知识点分（source），`st` = 目标知识点分（target）
- 输出 `debt_edges[]`，按 impact 降序；保留 `weight/source` 追踪
- **禁 LLM 改分**：本阶段不产生任何由模型自由生成的数字

### 2. build-repair-plan（14 天修复计划）

输入 TopN `debt_edge[] + kp_meta + minutes`：

1. 按 `impact / est_min` 贪心优先（高杠杆先还）；
2. 逐日填充，每日 ≤ `minutes`（默认 35，上限 40），间隔采用 0/2/6 天复习节奏；
3. 必须通过 **K1–K5 约束检查器**：

| 规则 | 断言 | 失败码 |
|---|---|---|
| K1 | `sum(est_min) ≤ day_budget` | K1 |
| K2 | 无连续 3 天对同一 kp 且 difficulty≥4 | K2 |
| K3 | 每项 `why` 非空且包含 `from_kp` 名 | K3 |
| K4 | 同 kp 内 concept 先于 drill/quiz | K4 |
| K5 | 覆盖 TopN 债边，或未覆盖边给显式 `dropped` 原因 | K5 |

4. 约束失败 → 重排 ≤2 次 → 仍失败返回 `DEGRADED_MANUAL_LIST`；
5. 用户改期后必须重跑约束，禁止超时堆叠。

### 3. adapt-drill（自适应练习与信心校准）

输入 `today_attempts + confidence(1–5)`：

- 判分后更新 `know` 的 Beta 参数与 `retention`；
- 明日按缺口与间隔调度题目（题库按知识点与难度分层）；
- **无 attempt 不得调高 mastery**；信心校准用 Brier（ECE 校准曲线）度量；
- 练习文案受教练自适应规则约束，禁止羞辱类措辞。

### 4. counselor-brief（教师班级简报 · 可选）

输入 `class_aggregate(consent 过滤后)`：

- 输出班级热点（hotspots：高频债边、瓶颈知识点）+ 三句话教师口径；
- k-匿名：任一样本 <3 抑制；危机/敏感域条目禁入；
- 无 consent 学生不得出现在任何聚合结果中。

## 4. 销账判定（SaleStateMachine · 唯一入口）

```text
cleared ⟺ 近期连续 2 次达标 ∧ 累计 ≥3 次
weighted = 0.7·acc + 0.3·(conf/5) ≥ 0.65
```

- `cleared` 状态只由销账状态机写入，模型与前端均无权直接修改；
- 销账检查为内联反馈，不占用故事生成。

## 5. 工具调用与编排（DuMate 能力映射）

| 业务能力 | 本技能中的编排方式 |
|---|---|
| 知识图谱构建 | 多维表 kp_nodes/kp_edges（图包）；无环校验用确定性规则节点 |
| 公式计算 | 规则/计算节点（score/impact/K1–K5/销账），同构实现：`src/AstralPath.Core`、`deploy/monolith-web/index.html`、`tools/*.py` 三方对拍（tol 1e-9） |
| 叙事与意图路由 | LLM 节点 + Schema：意图表 `tools/agent-intents.json`（22 意图三方对拍）；危机转人工；敏感词脱敏 |
| 计划约束 | 规则检查节点 + 人工确认（K1–K5 全部机器可判定） |
| 教练对话 | 对话练习 + 写回表（今日作答 → 明日调整）；话题切换门槛 TauExec，省略指代可衔接上文 |
| consent | 开关变量 + 教师简报分支（默认不可见是特性不是 bug） |
| 教材上架 | OCR 管线：文本层（pdf.js）→ WASM/宿主 OCR 阶梯 → 章节 → 关键词 → 图谱（页密度闸/乱码闸） |

## 6. 教育伦理护栏（强制）

- **禁词表**：不适合 / 太笨 / 比别人差 / 没救 / 别学了 / 处分 / 废物 / 无可救药 / 智商（含英文等价物）
- **危机词表**：不想活 / 自杀 / 轻生 / 结束生命 / 自残 / 伤害自己 / 跳楼 / 上吊 / suicide / kill myself / 活不下去 → **只安抚 + 转人工，不评价学生**
- **敏感域**：身份证 / 银行卡密码 / 详细住址 → 不入画像，教师侧禁入
- 本系统仅用于教学辅助与学习规划，不构成处分依据；演示数据均为合成数据
- opt-out 等价性：关闭即生效（练习改顺序、隐藏推断风格轴），不是只翻转布尔

## 7. 验证证据（截至 2026-09-28，v2.2.0）

- 全量回归 **ALL GREEN**：`scripts/verify_all.ps1`（README 记录 235+ 项，含 Core 28 / Persistence 8 / Desktop 47 / API 66 / Eval 2 / MobileCore 84）
- **12 本教材 MLP 抽样**：OCR 12/12 · 知识图谱 12/12 · 智能体 22 意图 + 危机词通过
  （`docs/mlp-acceptance-2026-09-28.md`、`docs/mlp-windows-acceptance-2026-09-28.md`）
- OCR 引擎线：PaddleOCR vs RapidOCR 平行基准（12 本 PDF 多进程）；`tools/ocr_regression.py` 12/12 PASS
- 公式三方行为对拍（Python / C# / JS，tol 1e-9）、agent-intents 三方对拍、金样 1e-6

## 8. 参考实现与文件索引

见配套文件 `REFERENCES.md`（逐文件路径、职责与类型签名）。

## 9. 故障降级预案

| 故障 | 现象 | 预案 | 口播一句 |
|---|---|---|---|
| LLM 超时/503 | 故事空或慢 | 模板叙事已预置 | 「叙事可降级，数字不变」 |
| 网络断开 | today 拉取失败 | 本地缓存当日任务 | 「今日任务已缓存在本机」 |
| 计划约束失败 | 422 | DEGRADED_MANUAL_LIST | 「宁可给手动清单，不给违规计划」 |
| consent 未开 | 教师页空 | 空态文案已准备 | 「默认不可见是特性不是 bug」 |
| 图节点过多 | 卡顿 | 闭包 + 虚拟化 | 「演示图谱走债边闭包」 |

---

© 2026 知债：星穹学途四人团队 · 技能来源：2026 iCAN AI / DuMate 竞赛作品 AstralPath v2.2.0