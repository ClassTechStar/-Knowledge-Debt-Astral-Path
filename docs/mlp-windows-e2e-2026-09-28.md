# Windows 便携版 exe MLP 级端到端测试报告（2026-09-28）

范围：`release/AstralPath-Portable-2.2.0/`（569MB 免安装绿色包，.NET 10 自包含 + WebView2 + 随包 tools/ocr-venv），
`C:\Users\18948\Downloads\PDF TEST` 全部 12 本 PDF，按 **OCR → 知识图谱 → 智能体** 在真实 WinForms+WebView2 窗口中实测。

---

## 一、结论

（测试进行中，完成后填写）

## 二、便携包构成与自包含验证

| 组件 | 说明 |
|---|---|
| `AstralPath.Monolith.exe` 等 | .NET 10 自包含发布（目标机免装 .NET） |
| `Resources/index.html` | 与 Web 版逐字节同源（md5 门禁） |
| `Resources/ocr-engine/` | Tesseract WASM + pdf.js（虚拟域下作为后备引擎） |
| `tools/ocr_pipeline.py` + 四件套 | 宿主桥 OCR 管线（RapidOCR v3，页级并行） |
| `tools/ocr-venv/` | 项目专用 Python 3.12 环境（rapidocr/onnxruntime/jieba） |
| `tools/tessdata/` | chi_sim+eng traineddata |

自包含验证：真窗口上传扫描书时，宿主桥实际执行
`release\AstralPath-Portable-2.2.0\tools\ocr-venv\Scripts\python.exe ...\tools\ocr_pipeline.py <临时PDF> --ocr standard`
——exe 旁的 tools 与 venv 被正确发现（OcrHost WalkUp 解析），无需任何安装或环境变量。

## 三、本轮发现并修复的问题（修复后复测）

| # | 严重度 | 问题 | 修复 |
|---|---|---|---|
| 1 | **阻断** | 壳内页面经 `file://` 加载 → Chromium 禁 Worker → pdf.js 文本层失效 → 裸解析器产出乱码被当成功接受（图谱节点全是乱码，实测截图存证） | `Program.cs` 改用 WebView2 `SetVirtualHostNameToFolderMapping` 挂 `https://appassets.local` 虚拟域——worker/WASM 全解锁，宿主桥不受影响 |
| 2 | **阻断** | OcrHost 看门狗硬编码 10 分钟，扫描/图文书 standard 全书 OCR（预算 900s）必超时 | 对齐 MaterialPipeline：按模式参数化（full 40min / standard 20min / 其他 3min）+ `ASTRALPATH_OCR_TIMEOUT_SECONDS` 覆盖 |
| 3 | 高 | 裸解析分支缺"可读率"闸（Latin-1 乱码不在乱码区间） | `index.html` 恢复 `ratio<0.70 → needOcr` 判定 |
| 4 | 中 | 宿主 OCR 静默执行数分钟，用户误判卡死而重复点击（实测同一本书 4 个管线进程并存） | OCR 等待期显示已等待秒数的滚动状态 + 解析期间禁用上传按钮（防重复提交） |
| 5 | 低 | 密度闸（上一轮 Web 修复）在 exe 中同样生效 | 「大模型」被正确判为"每页仅 7 可读字符（共 290 页），疑似扫描版"自动转 OCR（实测截图存证） |

## 四、实测记录

（完成后填写：逐书结果、识网/知债/智能体验证、截图索引）

## 五、复测入口

```powershell
powershell -File release\build-portable.ps1   # 一键重建便携包（发布+运行时+zip）
cd release\AstralPath-Portable-2.2.0; .\AstralPath.Monolith.exe
```
