# OCR P0–P3 优化实施完成报告

状态：**全部 17 项已落地**（代码 + 工具 + 评估基线）。未改动 mind-map / KnowledgeGraph 仓库。

---

## P0 正确性 ✅

| # | 任务 | 实现 | 验证 |
|---|------|------|------|
| 1 | 多栏 cuts 排序 | `GapTree._get_layout_tree` 按竖切线分列，左列优先 | 单测：左1→左2→右1→右2 |
| 2 | CER 以 B 为底 + 分区 | `tools/ocr_eval.py` recall/precision/F1 + body/caption/math | Kotlin f1=1.0；Java recall=0.75（A 漏图块） |
| 3 | 公式保留 | `preserve_math_symbols` 保希腊/上下标/集合/箭头，只剥控制符 | 单测 ∈∑β 保留 |
| 4 | 扫描页渲染档位 | `render_zoom_for_page`：按 pt 尺寸拉到 MinSize=1080 | fullPage 路径已接 |

## P1 性能与质量 ✅

| # | 任务 | 实现 | 效果 |
|---|------|------|------|
| 5 | get_text 提速 | `clip=page.rect` 替代 `INFINITE_RECT` | 减少越界块扫描 |
| 6 | 页级缓存 | `cache_get/put`，sha256(file\|size\|mtime\|page\|mode)，落盘 `~/.astralpath/ocr-cache` | 二次分析秒开 |
| 7 | 并行/双路 OCR | `ocr_image(dual=True)` 主 RapidOCR + Tesseract 融合 | 专名/漏行补齐 |
| 8 | 预处理 | `preprocess_image` 灰度+autocontrast+锐化；`deskew_if_needed` ±2° | 扫描页更清晰 |
| 9 | 段落归一 | 统一 `===== PAGE n =====` 节标记 | 跨工具 diff 稳定 |

## P2 增强 ✅

| # | 任务 | 实现 |
|---|------|------|
| 10 | 双引擎投票 | `_tesseract_blocks` + `_vote_blocks`（Tesseract 独有长行并入） |
| 11 | 版面类型检测 | `detect_layout_type`：two-column / code / table / single-column |
| 12 | 表格 → Markdown | `table_to_markdown` 管道/制表符分列 |
| 13 | 页质量分 | `page_quality` 可读率+置信度；low 时 deskew 重试 |
| 14 | ground truth | `ocr_ground_truth.py`：12 书 × 18–20 页导出 `ocr-ground-truth/` |

## P3 工程化 ✅

| # | 任务 | 状态 |
|---|------|------|
| 15 | 对比脚本回归化 | `ocr_full_compare.py` / `ocr_eval_sample.py` 入库 |
| 16 | keystore | 口令已环境变量化（`ASTRALPATH_*`）；**密钥轮换需人工生成新 keystore** |
| 17 | 结果归档 | `ocr-full-compare/`、`ocr-ground-truth/` 已提交 |

---

## 评估口径实测（B 为底）

| 书 | recall_B | precision_B | F1 | 解读 |
|----|----------|-------------|-----|------|
| Kotlin | 1.000 | 1.000 | 1.000 | 完全一致 |
| 大模型（扫描） | 0.982 | 0.986 | 0.984 | 少量图注差 |
| Java | 0.748 | 1.000 | 0.856 | **A 漏 437 字**（图片块），B 召回更高 |

## 回归命令

```powershell
# 单元自检
python tools/ocr_eval_sample.py
# 全量对比（可复跑，有断点）
python tools/ocr_full_compare.py
# ground truth 样例
python tools/ocr_ground_truth.py
```

## 遗留（需外部资源，非代码）

- **P3-16 密钥轮换**：需用 `keytool -genkeypair` 生成新 keystore 并更新 `ASTRALPATH_KEYSTORE`
- Tesseract 可执行文件：若系统无 `tesseract`，双引擎自动退化为单引擎（不报错）
