# 知债：星穹学途（Knowledge Debt: Astral Path）· 安装说明

版本 1.3.0 · Windows 10（1809+）/ Windows 11 x64

## 桌面端与 Web 端关系

**桌面应用 = 与 Web 端完全一致的界面与逻辑**  
实现方式：WinForms + Microsoft Edge WebView2 内嵌本机 Web 演示 UI  
（`api\wwwroot\index.html` 与浏览器访问 `http://127.0.0.1:PORT/` 为同一文件、同一 API）。

| 维度 | 是否一致 |
|------|----------|
| 功能 | 一致（藏书阁/识网/知债/今日/智能体/画像/账户） |
| 界面布局 | 一致（同一 HTML/CSS） |
| 交互 | 一致（同一 JS） |
| 数据处理 | 一致（同一本地 AstralPath.Api） |

## 安装步骤

1. 双击 `AstralPath-Setup-1.3.0-Desktop.exe`
2. 同意许可 → 选择目录（默认 `C:\Program Files\AstralPath`）
3. 勾选「安装 WebView2 运行时」（Win10 必需；Win11 通常已自带）
4. 可选创建桌面快捷方式
5. 安装完成后可立即启动

## 卸载

开始菜单 →「卸载 知债：星穹学途」，或 系统设置 → 应用。

## 版本信息

- 产品名：知债：星穹学途（Knowledge Debt: Astral Path）
- 版本：1.3.0
- 发布方：知债：星穹学途四人团队
- 仓库：https://github.com/ClassTechStar/-Knowledge-Debt-Astral-Path

## 合规

教学辅助系统，不构成处分依据。consent fail-closed；敏感词脱敏；危机内容转人工。
