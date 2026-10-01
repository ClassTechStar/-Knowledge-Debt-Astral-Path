# 参考实现与文件索引（textbook-to-graph）

> 本文件是 `SKILL.md` 的配套索引，列出技能各阶段对应的真实实现、函数签名与验证数据，
> 便于复刻与审计。项目根：`-Knowledge-Debt-Astral-Path`（AstralPath v2.2.0，
> 2026 iCAN AI / DuMate 竞赛作品「知债：星穹学途」）。

## 1. 通用约定

- **命名**：全称「知债：星穹学途（Knowledge Debt: Astral Path）」；代码标识 **AstralPath**；
  禁用旧名「知债图 / ZhiZhaiTu」。
- **确定性优先**：本技能的章节/术语/边全部算法可复算；LLM 仅出现在出题措辞环节，
  且受 `source_page` 强制约束。
- **上下游**：产物 `graph.json` → 同生态 `skills/knowledge-debt-repair`（kp_nodes/kp_edges 输入），
  组成「教材 → 图谱 → 债诊断 → 修复计划」全链路。

## 2. 阶段 1 · extract-text（抽取）

| 实现 | 路径 | 要点 |
|---|---|---|
| 生产管线 | `tools/ocr_pipeline_umi.py` | `extract_document` / `extract_page` / `detect_needs_ocr`；mixed 模式（文本层直读 + OCR 补坏页）；页级并行；PROGRESS 流式进度 |
| 生成管线（C#） | `tools/ocr_pipeline.py` + `src/AstralPath.Core/Ocr/` | API `/v1/materials/upload` 后端管线；文本清洗、部首修复、乱码闸 |
| 表格提取 | `tools/table_extract.py` | 管道表 / 空白对齐 / TSV 行聚类 → Markdown（P1-3） |
| 全书模式 | `tools/ocr_full_extract.py` | full 全书透传（P0）+ 多配置投票 |
| 引擎横评 | `tools/bench_ocr_engines.py`、`tools/ocr_engine_eval.py` | PaddleOCR vs RapidOCR 平行基准 |
| OCR 回归 | `tools/ocr_regression.py` | 12 本 12/12 PASS |
| Web 端同构 | `deploy/monolith-web/index.html` | pdf.js 文本层 + WASM OCR 阶梯（浏览器内，无服务器） |

**验收口径**：抽样 24 页/本，可读字符 ≥40 通过。12 本实测 6.8k–21.2k 字符/本，
文本书 OCR 页 0–5（文本层直读），扫描书 24/24 全 OCR。

## 3. 阶段 2–3 · structure-doc 与 build-kg（结构化与构图）

| 实现 | 路径 | 签名 / 要点 |
|---|---|---|
| 构图主入口 | `tools/kg_builder.py` | `build_graph(text, book_name, max_terms=24) -> dict`；`to_mindmap_tree(graph)`、`to_markdown(tree)`、`graph_stats(g)` |
| 算法核心 | `tools/kg_algorithm.py` | AC 自动机实体抽取 → 并查集/有界 Levenshtein 融合 → TextRank + PMI → DAG 收尾 |
| 批量驱动 | `tools/kg_deep_test_13.py`、`tools/deep_test_13pdfs.py` | 环境变量 `ASTRALPATH_PDF_DIR`（输入）、输出 `kg-deep-test/*.mindmap.{json,md}` |
| 算法说明 | `docs/算法v3-图谱与OCR优化说明.md` | G1 TextRank 稠密矩阵 / G2 术语→章节锚定 / G3 依赖句式正则 / G4 PMI 低频偏置 / G6 DAG 收尾 / G7 分层布局 / G8 学习路径排序；O1 混淆修复标识符保护 / O2 页眉页脚 / O5 TSV 行聚类 / O6 多配置投票 / O7 阅读顺序 |

**图结构**（`graph.json`）：

```json
{
  "book": "C#从入门到精通",
  "nodes": [
    { "id": "C01", "kind": "chapter", "title": "第1章 ...", "page": 3 },
    { "id": "T12", "kind": "term",    "title": "委托与事件", "score": 0.83 }
  ],
  "edges": [
    { "from": "C01", "to": "T12", "type": "belongs_to" },
    { "from": "T07", "to": "T12", "type": "prerequisite", "evidence": "需要先掌握事件" }
  ],
  "overflow": []
}
```

**验收门槛**：节点 ≥10 且边 ≥5；DAG 无环。12 本实测 30–51 节点 / 47–133 边，<0.1s/本。

## 4. 阶段 4 · draft-quiz（出题素材）

| 实现 | 路径 | 要点 |
|---|---|---|
| 题库存储 | `eval/question_bank.json`、`eval/question_bank.db`（SQLite 去重，P2-6） | 每题带 `why`；难度分布 1:21 / 2:51 / 3:28 |
| 教材真题 | `eval/textbook-questions.json` | 教材配套题（TextbookQuestionBank） |
| 章节出题 | `src/AstralPath.Infrastructure/MaterialTaskGenerator.cs`、`MaterialPipeline.ParseChapterBundle` | 章侧栏 + 按章出题（C# 侧） |
| 意图词表 | `tools/agent-intents.json` | 智能体侧「按章出题」等 22 意图（三方对拍唯一事实源） |

**红线**：每题必须带 `source_page` / `source_quote`；判分确定性匹配；无页码即丢弃。

## 5. 数据资产与验证证据

| 资产 | 路径 | 规模 |
|---|---|---|
| 13 本教材图谱 | `kg-deep-test/*.mindmap.{json,md}` | 6,720 页 / 2,519 节点 / 2,662 边 |
| 12 本 MLP 报告 | `docs/mlp-acceptance-2026-09-28.md`（Android）、`docs/mlp-windows-acceptance-2026-09-28.md`（Windows） | OCR 12/12 · 图谱 12/12 |
| 原始数据 | `docs/mlp-acceptance-2026-09-28.json`、`docs/materials-13pdf-results.tsv` | 逐书逐项指标 |
| OCR 基准报告 | `docs/ocr-engine-bench-report.md`、`docs/ocr-full-compare-and-optimization.md` | Paddle vs Rapid、full 模式优化 |
| 深度优化清单 | `docs/深度优化任务清单-2026-09-28.md` | P0–P3：full 透传 / 流式进度 / 表格 / SQLite 去重 / 质量闸 / 缓存 |
| 全量回归 | `scripts/verify_all.ps1` | 235+ 项 ALL GREEN（含图谱+OCR 84 项） |

## 6. 复刻最小路径（三步）

```powershell
# 1) 抽取（文本书秒级；扫描书需先安装 PaddleOCR 或 RapidOCR）
python tools/deep_test_13pdfs.py          # 或自写脚本调用 ocr_pipeline_umi.extract_document

# 2) 构图（章节 + 术语 + DAG + 思维导图，<0.1s/本）
python tools/kg_deep_test_13.py           # 产物：kg-deep-test/*.mindmap.{json,md}

# 3) 质量闸门与验收
python tools/ocr_regression.py           # OCR 回归 12/12
powershell -File scripts/verify_all.ps1  # 全量门禁 ALL GREEN
```

## 7. 已知边界（诚实标注）

- **中文优化**：章节模式（「第 N 章」）、依赖句式、混淆修复均按中文教材设计；
  英文/小语种书籍退化为目录骨架图。
- **扫描书依赖宿主 OCR**：便携包已裁 cv2（392MB 瘦身），扫描页 OCR 需宿主引擎桥或完整环境。
- **出题环节含 LLM**：仅措辞层；事实内容（题干素材、答案、页码）由规则从原文提取。
- **加密 PDF**：需先解除加密；批量任务中单独报错不中断。

---

© 2026 知债：星穹学途四人团队 · 技能来源：2026 iCAN AI / DuMate 竞赛作品 AstralPath v2.2.0
