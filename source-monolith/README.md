# 知债：星穹学途 · 纯前端单体版（源目录）

本目录是纯前端交付形态的源目录。应用本体是一个自包含的 HTML 文件，OCR 引擎随文件内联，双击即可用，不需要后端服务、不需要安装、离线也能跑。

## 目录里有什么

| 路径 | 作用 |
|---|---|
| `index.html` | 应用源文件，业务逻辑全在这里 |
| `astralpath-monolith.html` | 自包含单文件产物（18,187,662 字节，OCR 全栈内联），构建产物 |
| `ocr-engine/` | 离线识别运行时（tesseract WASM 双核、pdf.js、chi_sim 与 eng 语言包，共 11 个文件） |
| `tools/build-single.py` | 单文件构建脚本，把 OCR 依赖内联进 HTML |
| `tools/check-js.js` | 前端脚本语法门禁，只解析不执行 |
| `shared/` | 前端共享代码的单一来源：`core.js`（归一化、容错解析、拓扑破环）、`safe-dom.js`（转义）、`store.js`（状态分片存储） |
| `scripts/gen_frontend_regions.py` | 把 `shared/` 里的共享函数刷进 `index.html` 的生成区，`--check` 只校验 |
| `scripts/store_runner.js` 与 `scripts/verify_store.py` | 状态分片存储的黄金用例（10 例）与接入校验 |
| `serve.ps1`、`启动-知债单体版.bat` | 本地启动脚本 |
| `astralpath-skill.md` | 论文权威源，同时是 DuMate 编排层的 Skill 说明 |
| `毕业论文-知债星穹学途（湛江科技学院格式）.docx / .pdf` | 毕业论文 |
| `项目说明.md`、`graph_pack.json`、`question_bank.json` | 项目说明与静态数据 |

## 怎么构建

```
python tools/build-single.py
```

脚本会先核对 `index.html` 的基线校验和（`tools/build-single.py` 里的 `EXPECTED_MD5`，必须是小写十六进制），不一致直接退出，防止有人在没同步基线的情况下改基座。构建产物是 `astralpath-monolith.html`，除 onnxruntime-web 的 CDN（带降级哨兵，缺失只损失 ONNX 推理）外没有任何外部资源引用。

改 `index.html` 后要重打基线，改完 `shared/` 里的共享函数后要跑一次 `python scripts/gen_frontend_regions.py` 刷新生成区。

## 门禁

```
python scripts/gen_frontend_regions.py --check   # 生成区与 shared/ 是否一致
python scripts/verify_store.py                   # 分片存储 10 例 + 页面是否真的接入
node tools/check-js.js                           # 前端脚本语法（只解析不执行）
```

## 当前状态

版本 2.6.0。`index.html` 为 599,264 字节，md5 `2a4fa2681dff926cf46b700ab0f54ff4`；`astralpath-monolith.html` 为 18,187,662 字节，md5 `d81fb3b681bd0e5b4e85405a70d5f917`。两者与主仓库 `deploy/monolith-web/` 下的同名文件逐字节一致。

主仓库另有 Windows 壳与 Android 壳两份镜像，由主仓库的 `scripts/sync-monolith-html.ps1` 负责同步并做 md5 门禁，镜像不是本目录的编辑对象。

参赛材料在 `C:\Users\18948\Desktop\Team Tech file\`，其中《应用方案（参赛提交版）.md》与《DuMate编排蓝图与提交证据清单.md》是赛事必需提交项。
