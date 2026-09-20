# OCR 对齐 tesseract（官方仓库）

## 依据

- 本地仓库：`C:\Users\18948\Documents\GitHub\tesseract`
- 官方 CLI（README）：

```text
tesseract imagename outputbase [-l lang] [--oem ocrenginemode] [--psm pagesegmode] [configfiles...]
```

## 本项目接入

| 项 | 值 |
|---|---|
| 引擎 | Tesseract 5.4.0（winget: UB-Mannheim.TesseractOCR） |
| 路径 | `C:\Program Files\Tesseract-OCR\tesseract.exe` |
| tessdata | `tools/tessdata`（chi_sim + eng + osd） |
| 语言 | `-l chi_sim+eng` |
| OEM | `--oem 1`（LSTM，与 Tesseract 4/5 默认一致） |
| PSM | `--psm 3`（全自动分页） |

## 调用链

1. PDF 页 → `pypdfium2` 渲染 PNG（约 200 DPI）
2. `tesseract page.png outbase -l chi_sim+eng --oem 1 --psm 3 --tessdata-dir <dir>`
3. 读取 `outbase.txt`（另有 `.tsv`/`.hocr` 可选）
4. 合并章节/关键词 → 知识图谱 + 题目

## 环境变量

```text
ASTRALPATH_TESSERACT   = C:\Program Files\Tesseract-OCR\tesseract.exe
TESSDATA_PREFIX = ...\astralpath\tools\tessdata
ASTRALPATH_OCR_PYTHON  = ...\tools\ocr-venv\Scripts\python.exe
ASTRALPATH_TESS_LANG   = chi_sim+eng
ASTRALPATH_TESS_OEM    = 1
ASTRALPATH_TESS_PSM    = 3
```

## 验证

对扫描版《C#从入门到精通》正文页 CLI 识别结果示例：

> 类是一种数据结构，它可以封装数据成员、函数成员和其他的类。类是创建对象的模板。

管线输出：`ocrEngine=tesseract`，`langs=chi_sim,eng,osd`。

## 命令行自检

```powershell
$env:TESSDATA_PREFIX = "...\tools\tessdata"
python tools/ocr_pipeline.py --info
python tools/ocr_pipeline.py "<pdf>" --ocr quick --out out.json
# 或直接 tesseract：
tesseract page.png out -l chi_sim+eng --oem 1 --psm 3 --tessdata-dir <tessdata>
```
