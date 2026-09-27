# OCR P0–P3 第二轮优化实施完成报告

日期：2026-09-28 · 依据：`docs/ocr-max-vs-production-2026-09-27.md` 任务清单（17 项）· **全部落地并回归验证 PASS**

---

## 一、验收结果（12 本 5,982 页，基线=独立最大化抽取 337.9 万词字符）

| 指标 | 优化前 | 优化后 | 验收线 |
|---|---|---|---|
| 总字符量（vs 基线） | 72.6% | **≈100%（各书 99.3–106%）** | — |
| 页级探针覆盖 | 75.0% | **98.8%** | ≥90% |
| C#/Go/Java 字符量 | 51–54% | **99.3 / 99.7 / 100.2%** | ≥90% |
| 强化学习（pypdf 欠抽取） | 24.7k 词字符、KG 1 章 | **163.7k、KG 10 章** | ≥150k / ≥8 章 |
| 大模型（纯扫描） | 18.1k（6.6%） | **290.1k（106%）** | ≥90% |
| 康熙部首伪字（黄仁勋/C#/Java） | 9.21% / 5.05% / 0.11% | **0 / 0 / 0** | =0 |
| GT 页被生产覆盖 | 5/12 | **11/12**（缺页=整页计算图，图示页） | — |
| KG 章节恢复 | 自制框架 ch=1 | **ch=60**；强化学习 ch=1→10 | — |

回归命令：`python tools/ocr_regression.py`（12/12 PASS ✅，含单元自检）。
单书耗时（standard，页缓存冷启）：文本书 1–5 分钟；C# 868 页 654s、Java 775 页 911s（预算 900s 内）；缓存热启秒级。

## 二、实施清单（17/17）

### P0 正确性
| # | 任务 | 实现位置 |
|---|---|---|
| 1 | 多抽取器交叉验证（pymupdf/pypdf/pdfium，污染排除+择优） | `ocr_pipeline._select_extractor` |
| 2 | 康熙部首→CJK 映射（NFKC 240 条 + 实证表 26 条） | `fix_radical_chars` |
| 3 | 乱码判定纳入 U+2E80–U+2FDF 部首区 | `GARBLE_RANGES` |
| 4 | 弱文本层/图片内容书全书逐页 OCR 补齐 | `extract_full_text` 目标选择 |
| 5 | `--ocr full` 全书模式；C# 超时按模式参数化（3/20/40 分钟，env 可覆盖） | `ocr_pipeline.main`、`MaterialPipeline.ResolveTimeoutMinutes` |
| 6 | 取消 wide-garble 整本替换，一律逐页回填 | `extract_full_text`（旧分支删除） |

### P1 质量
| # | 任务 | 实现位置 |
|---|---|---|
| 7 | 逐页 text×OCR 1.25 规则择优合并 | `merge_page_texts` |
| 8 | 图示页检测（图片占比×OCR 行长）+ 节点标签噪声过滤 | `is_figure_page` + merge figure 分支 |
| 9 | rapid 路径预处理（灰度+autocontrast，env 可关）+ 自适应渲染倍率（MinSize=1080，封顶 4x） | `preprocess_pil_for_ocr`、`_render_scale_for` |
| 10 | `fix_ocr_text` 标点/×→* 归一化改为 aggressive 可选（默认保真） | `fix_ocr_text(aggressive=)` |
| 11 | 出版方 OCR 夹层复核（抽样 6 页 text vs OCR 相似度中位数 <0.90 → 标记） | `overlay_suspect_check` |

### P2 知识图谱
| # | 任务 | 实现位置 |
|---|---|---|
| 12 | 章节形态扩展：篇/部分/阶段/讲/步骤N/Part/Unit/Lesson/Stage + 编行标题并行 + deep_chapters 同步 | `kg_algorithm.CHAPTER_FIND_RE`、`deep_chapters.CHAPTER_RES` |
| 13 | 术语按章分布统计 + 多章 related 边 + 同段共现加权 | `extract_terms_weighted`、`cooccurrence_edges` |
| 14 | KG 输入质量闸（sparse/underextract/overlay_suspect/truncated → graphStats.inputQuality=low） | `ocr_pipeline.process_file` |

### P3 工程化
| # | 任务 | 实现位置 |
|---|---|---|
| 15 | 死代码清理（build_nodes_edges 空循环、terms no-op）；硬编码路径清（XiaomiMiMoProjects 移除，改脚本/工作目录相对） | `ocr_pipeline.py`、`MaterialPipeline.cs` |
| 16 | 页级 OCR 缓存接入生产 rapid 路径（key=文件+页+倍率+引擎+预处理标志；`~/.astralpath/ocr-cache/prod`） | `_ocr_cache_get/put` |
| 17 | 回归固化：`tools/ocr_regression.py`（自检+复跑+判定）；`ocr-ground-truth/_README.md` 澄清非真值；真值= `ocr-bench/gt-vision/` | 本目录 |

## 三、实施中发现并修复的连带缺陷

1. **pdfium 线程安全**：自适应倍率计算在工作线程内开文档 → 并发 "Data format error"。已在 `_PDFIUM_RENDER_LOCK` 内串行。
2. **pypdfium2 `PdfImage` 边界 API**：是 `get_bounds()` 而非 `get_pos()`；且异常静默 `continue` 导致图片占比恒 0（半图页全部漏判）。
3. **大写 `.PDF` 扩展**：pypdf 直接打不开（0 页）——多抽取器池天然兜底。
4. **OCR 总预算**：原 per-chunk 预算会乘以 chunk 数放大；改为整批 deadline + `ocr_budget_truncated` note + 页缓存续跑。
5. **生产环境补齐**：`tools/ocr-venv` 安装 pymupdf 1.28.2（三路抽取器池生效的前提）。

## 四、行为变化说明（运维须知）

- standard 模式对"有图片内容的书"现在做**全书逐页 OCR**（`full_page_ocr:reason=image_content`）：单书冷启 1–16 分钟（C# 868 页 654s / Java 911s），C# 端 standard 超时已放宽到 20 分钟；二次解析走页缓存秒级。
- 预算/上限可调：`ASTRALPATH_OCR_BUDGET_S`（默认 900）、`ASTRALPATH_OCR_MAX_PAGES`（默认 1500）、`ASTRALPATH_OCR_TIMEOUT_SECONDS`（C# 侧整体覆盖）。
- OCR 文本默认**不再改写标点**（全角保留，`×` 不再变 `*`）；如需旧行为设 `ASTRALPATH_OCR_AGGRESSIVE_NORMALIZE=1`。
- 图内节点标签噪声（思维导图/计算图页）不再混入正文；payload `detail.figurePages` 列出图示页。
- 扫描书 standard 即全书 OCR（大模型 290 页 342s）；`--ocr full` 用于超长书/无上限场景（40 分钟超时）。

## 五、改动文件

| 文件 | 变更 |
|---|---|
| `tools/ocr_pipeline.py` | P0-1/2/3/4/5/6、P1-7/8/9/10/11、P3-15/16 |
| `tools/kg_algorithm.py` | P2-12/13 |
| `tools/deep_chapters.py` | P2-12 |
| `src/AstralPath.Infrastructure/MaterialPipeline.cs` | P0-5 超时、P3-15 路径 |
| `src/AstralPath.Api.Core/Controllers/MaterialsController.cs` | full 模式白名单 |
| `tools/ocr_regression.py`（新增） | P3-17 回归门 |
| `ocr-ground-truth/_README.md`（新增） | P3-17 澄清 |
| `tools/max_extract_merge.py` / `tools/max_vs_project_compare.py` | 评测工具（上轮） |

数据留档：`ocr-bench/project_v1_before/`（优化前 payload）、`ocr-bench/project/`（优化后）、`ocr-bench/max_vs_project.json`（对比明细）。
