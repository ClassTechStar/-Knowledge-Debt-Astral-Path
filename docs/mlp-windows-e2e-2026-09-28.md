# Windows 便携版 exe MLP 级端到端测试报告（2026-09-28）

范围：`release/AstralPath-Portable-2.2.0/`（569MB 免安装绿色包，.NET 10 自包含 + WebView2 + 随包 tools/ocr-venv），
`C:\Users\18948\Downloads\PDF TEST` 全部 12 本 PDF，按 **OCR → 知识图谱 → 智能体** 在真实 WinForms+WebView2 窗口中实测。

---

## 一、结论

**12/12 本 PDF 在真窗口中全流程可用，达到 MLP 级。** 修复 §三 的 2 个阻断回归后：文本层书秒级解析（pdf.js 在虚拟域下正常工作），扫描/乱码书自动转宿主 OCR（python 管线随包），13 张真实内容图谱可切换浏览，知债诊断/What-if/计划生成可用，智能体意图路由正确（路由轨迹面板可见 decision 链）。普通用户路径：解压 → 双击 exe → 藏书阁多选上传 → 全自动。

| 阶段 | 结果 | 关键数据 |
|---|---|---|
| OCR/解析 | ✅ 12/12 | 文本层书 pdf.js 直读（秒级）；大模型（每页仅 7 可读字）自动判扫描版→宿主 OCR（~8.5 分钟/290 页）；NLP/图灵/入门等图文书宿主 OCR 定向回填；进度条 + "OCR 运行中已等待 N 秒"实时反馈 |
| 知识图谱 | ✅ 13 张 | 每书真实内容图谱（如 Go 书 60 节点/107 边：Go语言简介/并发/开发环境…；深度学习入门 60 节点/190 边：误差反向传播/MNIST…），章节侧栏带页码目录，节点检视带正文与先修链 |
| 知债/今日 | ✅ | What-if 滑杆实时计算（impact=0.101478≥0.08 命中演示）、14 天计划按真实图谱生成（D1·第1章·core·20min / D2·Go语言简介·challenge·15min） |
| 智能体 | ✅ | "帮我诊断知识债"→正确路由；"图谱里有哪些章节"→返回当前书真实图谱摘要；路由轨迹面板（graph.view·execute·R3:anchor:图谱;score=0.72）；危机词路径同 Web 版（同一 JS，前轮已验） |

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

- 上传方式：真实 Win32 文件对话框多选 12 个 PDF（用户协同操作），`standard` 模式一次性顺序解析
- 全程耗时约 55 分钟（其中宿主 OCR：大模型 ~8.5 分钟、NLP ~7 分钟、深度学习入门 ~8 分钟、Go/图灵定向回填数十秒；其余文本层书秒级）
- 解析完成后自动跳转识网，13 张图谱（12 书 + 示例）全部可切换浏览，节点/章节/正文均为真实教材内容
- 截图存证：乱码图谱（修复前，§三-1 证据）、密度闸触发、OCR 进度反馈、识网真实图谱（Go/深度学习入门）、知债 What-if、修复计划、智能体问答 + 路由轨迹

### 遗留（不阻断 MLP）

1. 宿主 OCR 无逐页进度（桥为一次性 postMessage；已用耗时计时器缓解，流式进度需桥协议扩展）
2. 图文书（Java/C#/Go）的"图片内代码"在 Web/壳路径不会像后端 full 模式那样全书 OCR——文本层合格即直读，图片内文字依赖后续练习数据补充；如需对齐后端覆盖率可让宿主桥透传 `--ocr full`
3. 便携包 569MB（.NET 自包含 + ocr-venv），如需瘦身可改 framework-dependent（-100MB）或裁剪 ocr-venv

## 五、复测入口

```powershell
powershell -File release\build-portable.ps1   # 一键重建便携包（发布+运行时+zip）
cd release\AstralPath-Portable-2.2.0; .\AstralPath.Monolith.exe
```
