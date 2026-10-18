# 前端构建合并：shared/*.js 生成区刷新 + 三镜像同步（方案 P2 阶段二 / W3）
# =====================================================================
# 取代「手工改三处镜像」的旧流程。单一事实源分层：
#   shared/core.js / shared/safe-dom.js / shared/store.js   ← 共享逻辑唯一源
#   deploy/monolith-web/index.html                         ← 页面基座（含 AP 生成区）
#   src/AstralPath.Monolith/Resources/index.html          ┐
#   src/AstralPath.Native/.../assets/www/index.html       ┘ 三镜像，由本脚本同步
#
# 用法：
#   scripts/build-frontend.ps1          # 刷新生成区 → 同步三镜像 → 报告 hash
#   scripts/build-frontend.ps1 -Check   # 只校验不写（verify_all / CI 用）
#
# 注意：本脚本不重建单文件产物。改了 index.html 之后如需产物，
#       按既定流程重打 scripts/build-single.py 的 EXPECTED_MD5 再重建
#       （md5 基线是有意的显式动作，防「悄悄换了基座」）。
# 退出码：0 = ALL GREEN；非 0 = 生成区漂移或镜像不一致
param(
    [switch]$Check
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 1) 生成区：shared/*.js → deploy/monolith-web/index.html
if ($Check) {
    python scripts/gen_frontend_regions.py --check
} else {
    python scripts/gen_frontend_regions.py
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "FRONTEND-REGION DRIFT · 生成区与 shared/ 不一致" -ForegroundColor Red
    exit 1
}

# 2) 三镜像同步 + md5 校验（含 C3 XSS 回归哨兵）
if ($Check) {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/sync-monolith-html.ps1 -Check
} else {
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/sync-monolith-html.ps1
}
if ($LASTEXITCODE -ne 0) { exit 1 }

if (-not $Check) {
    Write-Host "下一步（如需单文件产物）：重打 scripts/build-single.py 的 EXPECTED_MD5 后重建" -ForegroundColor DarkGray
}
Write-Host "FRONTEND BUILD ALL GREEN" -ForegroundColor Green
exit 0
