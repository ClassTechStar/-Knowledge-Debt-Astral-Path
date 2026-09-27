# 知债：星穹学途 · OCR 与自动识网

## 新增能力

1. **资料 OCR / 文本解析**
   - 优先抽取 PDF 文本层（pypdf / pypdfium2）
   - 扫描版自动启用 **RapidOCR v3**（本地 RapidOCR 仓库 `python/` 包，PP-OCRv6 检测/识别模型）
   - 模式：`quick` / `standard` / `none`
   - **页级多线程并行**：渲染串行（pdfium 非线程安全），OCR 按 `ASTRALPATH_OCR_WORKERS`（默认 4）线程并行，每线程独立引擎实例，单实例 ORT 线程由 `ASTRALPATH_OCR_INTRA`（默认 2）限死防超订阅
   - 引擎切换：`ASTRALPATH_OCR_ENGINE=auto(默认→rapid) | tesseract`；tesseract 三 PSM 投票路径保留为回退
   - 引擎基准（120 抽样页，2026-09-27）：rapid3 1.45 页/s ≈ 3.5× 1.4.4（0.41）≈ 13.9× PaddleOCR（0.105），质量持平（CER vs 文本层 0.36–0.37）
   - **全量双轮评测（2026-09-27，12 本 5,982 页）**：独立最大化抽取 337.9 万词字符 vs 生产管线 245.4 万（72.6%）。6 本文本层书 ≈99% 完好；C#/Go/Java 仅 51–54%（代码在截图里，无乱码不触发 OCR）；强化学习 15%（pypdf 欠抽取无兜底）；扫描大模型 6.6%（只 OCR 28 页）；黄仁勋 9.2% 字符被 pypdf 康熙部首污染（⻩/⼭/⾹，乱码判定豁免区）。RapidOCR 正文 CER 0.3–5.4%（12 页独立视觉真值）。深度优化任务清单见 `docs/ocr-max-vs-production-2026-09-27.md`
   - **P0–P3 第二轮优化已全部落地（2026-09-28）**：多抽取器交叉验证（pymupdf/pypdf/pdfium）+ 部首伪字还原 + 逐页 1.25 择优合并 + 有图书全书 OCR + `--ocr full` 模式 + C# 超时参数化（3/20/40min）+ 图示页降噪 + 夹层复核 + KG 章节形态扩展（阶段/步骤/Part…）+ 页级缓存 + 回归门 `tools/ocr_regression.py`。验收：12 本字符量 99.3–106%、页覆盖 98.8%、部首伪字 0、回归 PASS。详见 `docs/ocr-p0-p3-round2-completion.md`

2. **按文件自动生成知识图谱**
   - 章节标题抽取 → 顺序先修边
   - 关键词抽取 → 锚定前序章节
   - 无环校验 + 节点/边导出
   - 可对自动图谱做债边预览

3. **UI 对齐 GalReview**
   - 参考 `GalReview/frontend/src/styles/global.css` 与 AppShell/识网页
   - 画布 `#f3f4f5`、大圆角面板、顶栏 rail、藏书阁 / 识网 / 知债 / 今日
   - DAG 点阵画布 + 节点检视器（graph-board / graph-inspector）

## 关键文件

| 路径 | 说明 |
|---|---|
| `tools/ocr_pipeline.py` | OCR/解析/建图管线 |
| `tools/ocr-venv/` | 项目专用 OCR Python 环境 |
| `src/AstralPath.Infrastructure/MaterialPipeline.cs` | C# 调用管线 + 图谱组装 |
| `src/AstralPath.Api/Controllers/MaterialsController.cs` | 资料/识网 API |
| `src/AstralPath.Api/wwwroot/index.html` | GalReview 风格演示台 |

## API

```text
GET  /v1/materials
POST /v1/materials/upload?ocr=standard   (multipart file)
POST /v1/materials/{id}/parse?ocr=standard
GET  /v1/materials/{id}
POST /v1/materials/{id}/generate-graph
GET  /v1/knowledge-graphs
GET  /v1/knowledge-graphs/{graphId}
POST /v1/knowledge-graphs/{graphId}/preview-debts?studentId=demo-student-a
POST /v1/materials/seed-samples
```

## 使用

```powershell
$env:ASTRALPATH_OCR_PYTHON = "C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\tools\ocr-venv\Scripts\python.exe"
Set-Location "C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path"
dotnet run --project src/AstralPath.Api -c Release --urls http://127.0.0.1:5190
```

打开 `http://127.0.0.1:5190/`：

1. **藏书阁** → 一键导入示例 PDF / 上传自己的资料  
2. 点击 **解析**（扫描版选 standard）  
3. **识网** → 查看自动知识图谱 DAG，点「预览债边」  
4. **知债** → 继续会计课程包诊断与计划  

## 样本资料（已接入）

- Kotlin编程实践
- Python编程从入门到实践（第3版）
- 深度学习 Deep Learning（花书）
- 深度学习进阶：自然语言处理
- C#从入门到精通（第7版）
- Java从入门到精通（第6版）
- Go语言从入门到精通
- 深度学习入门系列（斋藤康毅）
- 大模型应用开发：动手做 AI Agent

## 测试

```text
Core.Tests   11  含 OCR 管线 + 自动图谱无环
Eval.Tests    2
Api.Tests    15  含 materials/knowledge-graphs 接口
合计         28  全部通过
```
