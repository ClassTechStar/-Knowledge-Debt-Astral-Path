@echo off
chcp 65001 >nul
REM ===================================================================
REM 知债：星穹学途 · Web 版启动器（单体版 2.2，绿色免安装）
REM -------------------------------------------------------------------
REM 直接双击 index.html（file:// 打开）时，浏览器沙箱禁止 Worker/fetch
REM 读取本地资源，内置 WASM OCR 引擎（ocr-engine\）无法加载，OCR 不可用。
REM 本启动器改用系统自带 PowerShell 起一个仅监听 127.0.0.1 的本地静态
REM 服务（serve.ps1，零依赖、无网络、无需 Python），再用默认浏览器打开
REM http://127.0.0.1:8799/index.html —— OCR 完整可用。
REM 服务空闲 30 分钟自动退出；如需直开文件可忽略本脚本（OCR 将降级为
REM 粘贴正文兜底）。
REM ===================================================================
start "" powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0serve.ps1"
REM serve.ps1 绑定端口后会自行打开浏览器；此处兜底再开一次（幂等）
timeout /t 2 /nobreak >nul
start "" "http://127.0.0.1:8799/index.html"
