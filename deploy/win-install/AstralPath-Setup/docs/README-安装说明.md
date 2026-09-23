# 知债：星穹学途 · Windows 安装说明

版本 1.3.0 · Windows 10/11 x64

## 与 Web 端关系

桌面应用使用 WebView2 内嵌 **同一份** Web 界面与同一本地 API，功能、布局、交互、数据逻辑完全一致。

## 安装

1. 双击 `AstralPath-Setup-1.3.0-Desktop.exe`
2. 按向导选择目录（默认 `C:\Program Files\AstralPath`）
3. 可选安装 WebView2 运行时（Win10 必需）
4. 创建桌面 / 开始菜单快捷方式
5. 完成后可立即启动

## 卸载

开始菜单 →「卸载 知债：星穹学途」

## 组件

- `bin\AstralPath.Desktop.exe` — 桌面壳（Web 同构）
- `api\AstralPath.Api.exe` — 本地服务
- `启动-Web演示台.bat` — 浏览器打开 http://127.0.0.1:5190/

演示账户：`demo@astralpath.local` / `demo123456`

教学辅助，不构成处分依据。
