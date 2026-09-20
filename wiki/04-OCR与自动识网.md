# 知债：星穹学途 · OCR 与自动识网

## 新增能力

1. **资料 OCR / 文本解析**
   - 优先抽取 PDF 文本层（pypdf / pypdfium2）
   - 扫描版自动启用 **RapidOCR**（`tools/ocr-venv`）
   - 模式：`quick` / `standard` / `none`

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
