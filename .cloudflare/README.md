# Cloudflare 部署包 · 知债：星穹学途 v2.2.0

本目录是把**单体 Web 版**部署到 Cloudflare Pages 的产物配置。

## 目录结构

```text
.cloudflare/
├── wrangler.jsonc          # Pages 项目配置（项目名 astralpath）
└── pages/                  # ← 部署根目录（wrangler pages deploy pages）
    ├── index.html          # 单体版全功能页面（107 KB，8 大模块）
    ├── _headers            # 响应头：COOP/COEP + 缓存策略
    ├── _redirects          # SPA 回退（ocr-engine 直出，不回退）
    └── ocr-engine/         # pdf.js + Tesseract WASM（11 MB / 176 文件）
```

## 部署命令

```bash
# 方式一：Cloudflare Pages 直连GitHub（推荐，无需本地构建）
#   Dashboard → Workers & Pages → Create → Pages → Connect to Git
#   Build command 留空，Output directory 填 .cloudflare/pages

# 方式二：wrangler CLI 手动上传
npx wrangler login
npx wrangler pages deploy pages --project-name astralpath
```

## 关键配置说明

| 配置 | 原因 |
|---|---|
| `Cross-Origin-Opener-Policy: same-origin`<br/>`Cross-Origin-Embedder-Policy: require-corp` | Tesseract WASM 的 SIMD / 多线程路径依赖 `SharedArrayBuffer`，需跨源隔离 |
| `/ocr-engine/*` 一年强缓存 | 引擎文件不变，内容稳定 |
| `/index.html` 零缓存 | 保证内容更新即时生效 |
| `_redirects` 中 `ocr-engine` 单独直出 | 否则引擎请求会 fallback 到 index.html，导致 WASM 加载失败 |

## 已排除的文件

| 文件 | 原因 |
|---|---|
| `ocr-engine/dl-vendor.ps1` | Windows 供应商下载脚本，Web 端不需要 |
| `README.md` / `serve.ps1` / `启动-知债单体版.bat` | Windows 本地辅助脚本 |
| `selftest-ocr.html` | OCR 自测页，未被index.html 引用 |
| `graph_pack.json` / `question_bank.json` | 内容已内联为常量 `astralpath-monolith.json`，无fetch 请求 |

## 能力边界（务必知悉）

**可用的**（Pages 版与本地 Web 版一致）：

- 藏书阁 / 识网 / 知债 / 今日 / 智能体 / 画像 / 账户 八大模块全功能
- pdf.js 文本层直读（11/12 本教材，2–7 秒/本）
- Tesseract WASM OCR 降级路径（chi_sim + eng）
- 数据存浏览器 IndexedDB / localStorage，可导出 JSON
- 完全离线可用（部署后断网仍能跑）

**失去的**（仅 Windows / Android 壳具备）：

- 宿主 OCR 桥：RapidOCR v3 引擎 + `astralpath.ocr.progress` 流式页级进度
- 扫描书 OCR 质量与速度下降（降级到 Tesseract WASM，约 59 秒/本）
- `appassets.local` 虚拟域挂载、桌面 OCR 缓存清理

> Web 端 MLP 实测（2026-09-28）：12 本 PDF 全流程 12/12 通过，依赖的正是
> pdf.js 文本层 + Tesseract WASM 阶梯，因此 Pages 版功能完备。

## 限制余量（对照官方limits）

| 限制项 | 官方上限 | 本项目 | 余量 |
|---|---|---|---|
| 静态资产单文件 | 25 MiB | 3.76 MB（最大 WASM） | 充足 |
| Worker 内静态文件数 | 20,000（Free） | 178 | 充足 |
| Worker size | 64 MiB | 约 11 MB | 充足 |

## 数据与隐私

所有学习数据留在浏览器本地（IndexedDB / localStorage），**不上传服务器**。
清除浏览器数据会一并清空，可先在「账户 → 导出 JSON」备份。
