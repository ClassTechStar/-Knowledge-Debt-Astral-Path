# 单一事实源同步：deploy/monolith-web/index.html → Windows 壳 / Android 壳
# =====================================================================
# 用法：
#   scripts/sync-monolith-html.ps1                    # 复制到两端镜像并校验 md5
#   scripts/sync-monolith-html.ps1 -Check             # 只校验不复制（verify_all / CI 用）
#   scripts/sync-monolith-html.ps1 -IncludeOcrEngine  # 连 ocr-engine/ 运行时一起同步
# 退出码：0 = ALL GREEN；非 0 = 有镜像漂移或命中 C3 回归哨兵。
param(
    [switch]$Check,
    [switch]$IncludeOcrEngine
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "deploy\monolith-web\index.html"
$targets = @(
    "src\AstralPath.Monolith\Resources\index.html",
    "src\AstralPath.Native\app\src\main\assets\www\index.html"
)

function Get-Md5($p) { (Get-FileHash -Algorithm MD5 -LiteralPath $p).Hash }

# C3 回归哨兵（2026-09-26 修复）：图谱/章节「先修链」渲染必须经过 esc()，
# 禁止回退到裸 .map(title) 直接拼 innerHTML（节点标题来自用户上传的教材，可注入脚本）。
$sentinel = '\.map\(title\)'
foreach ($p in (@($src) + $targets | ForEach-Object { Join-Path $root $_ })) {
    if ((Test-Path $p -PathType Leaf) -and (Select-String -LiteralPath $p -Pattern $sentinel -Quiet)) {
        Write-Host "XSS-REGRESSION · $p 出现未转义的 .map(title)（C3 回归，innerHTML 注入）" -ForegroundColor Red
        exit 1
    }
}

if (-not $Check) {
    foreach ($t in $targets) {
        Copy-Item $src (Join-Path $root $t) -Force
        Write-Host "synced -> $t"
    }
    if ($IncludeOcrEngine) {
        $srcDir = Join-Path $root "deploy\monolith-web\ocr-engine"
        $ocrTargets = @(
            "src\AstralPath.Monolith\Resources\ocr-engine",
            "src\AstralPath.Native\app\src\main\assets\www\ocr-engine"
        )
        foreach ($t in $ocrTargets) {
            Copy-Item (Join-Path $srcDir "*") (Join-Path $root $t) -Recurse -Force
            Write-Host "synced ocr-engine -> $t"
        }
    }
}

$srcHash = Get-Md5 $src
$fail = $false
foreach ($t in $targets) {
    $p = Join-Path $root $t
    if (-not (Test-Path $p)) { Write-Host "MISSING $t" -ForegroundColor Red; $fail = $true; continue }
    if ((Get-Md5 $p) -ne $srcHash) { Write-Host "MISMATCH $t" -ForegroundColor Red; $fail = $true }
    else { Write-Host "OK $t" }
}
if ($fail) { exit 1 }
Write-Host "HTML SYNC ALL GREEN · md5=$srcHash" -ForegroundColor Green
exit 0
