# 2026-09-25 全面验收 · 复现脚本

对应报告：`docs/验收报告-全面功能与稳定性-2026-09-25.md`

| 文件 | 用途 | 项数 |
|---|---|---|
| `deep_probe.py` | 后端深度探测：鉴权/越权、输入边界、错误处理、并发压测 | 32 |
| `walkthrough.js` | 无服务单体版前端端到端走查（八页切换、核心流程、边界、断网） | 30 |
| `focus.js` | 前端定点复测：答题全流程、alert 交互、opt-out 生效性、配额耗尽 | 10 |
| `extra.js` | 前端补测：答题/换题/Demo 推进/识网/上传/登录 | 13 |
| `smoke_all_endpoints.log` | 项目自带冒烟原始输出（93/93 通过） | 93 |
| `front_walk.json` / `front_focus.json` / `front_extra.json` | 前端三次走查的逐项原始结果 | — |

## 前置准备

```powershell
# 后端（监听 127.0.0.1:5190）
dotnet run --project src\AstralPath.Api -c Release --urls http://127.0.0.1:5190

# 前端：浏览器自动化无法访问 file:// 协议，需用 HTTP 托管
cd deploy\monolith-web
python -m http.server 8899 --bind 127.0.0.1

# 浏览器依赖
npm install -g @playwright/cli@latest
playwright-cli install-browser
```

> `*.js` 脚本内的 `playwright-core` 绝对路径需按本机 node 版本调整。

## 运行

```bash
python docs/acceptance-2026-09-25/deep_probe.py        # 后端 32 项
node   docs/acceptance-2026-09-25/walkthrough.js       # 前端 30 项
node   docs/acceptance-2026-09-25/focus.js             # 前端定点 10 项
node   docs/acceptance-2026-09-25/extra.js             # 前端补测 13 项
bash   scripts/smoke_all_endpoints.sh                  # 项目自带冒烟 93 项
```
