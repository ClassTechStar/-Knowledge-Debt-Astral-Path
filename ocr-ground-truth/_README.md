# ⚠️ 这个目录不是 ground truth

`ocr-ground-truth/` 是 **umi 管线（ocr_pipeline_umi.extract_document, mode=mixed）自己的抽样输出**
（每书 18–20 页），最初作为"人工校对脚手架"导出。它不是独立真值，**不可**用作 OCR 质量评估的基准：

- 它来自被评测方之一的管线，天然偏向该管线的行为（含其 mixed 双重抽取缺陷）；
- 扫描书上的"真值"其实是引擎自己 OCR 的结果。

**真正的独立视觉真值在 `ocr-bench/gt-vision/`**（12 页 × 弱文本层书，逐页人工级转写，
配套 PNG 渲染图），CER 评估请用它。

回归评估入口：`python tools/ocr_regression.py`（详见 docs/ocr-max-vs-production-2026-09-27.md）。
