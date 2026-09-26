# 知债：星穹学途 · 一键验收（ALL GREEN / FAILED）
# 用法：powershell -File scripts\verify_all.ps1
# 退出码：0 = ALL GREEN；非 0 = FAILED

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$failed = @()

# 统一输出编码，并把 dotnet CLI 切英文，避免控制台中文乱码
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$env:DOTNET_NOLOGO = "1"

function Step($name, $cmd) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    & $cmd
    if ($LASTEXITCODE -ne 0) { $script:failed += $name }
}

# 1) 常量对齐：JS 的 K 对象 ↔ C# 的 FormulaConstants.cs
Step "constants-K-vs-FormulaConstants" { python scripts/verify_constants.py }

# 1b) 智能体意图登记表三方对拍：tools/agent-intents.json ↔ index.html ↔ AgentRouter.cs
if (Test-Path scripts/verify_agent_intents.py) {
    Step "agent-intents-three-way" { python scripts/verify_agent_intents.py }
} else {
    Write-Host "[SKIP] scripts/verify_agent_intents.py 尚未生成" -ForegroundColor Yellow
}

# 2) 知识图包：无环 / 无悬空边 / 无重复边 / why 非空
if (Test-Path scripts/verify_graph.py) {
    Step "graph-pack" { python scripts/verify_graph.py graph-packs/astralpath-v2/graph_pack.json }
} else {
    Write-Host "[SKIP] scripts/verify_graph.py 尚未生成（P3 交付物）" -ForegroundColor Yellow
}

# 3) 题库：≥100 题 / correct_index 合法 / 可溯源
if (Test-Path scripts/verify_bank.py) {
    Step "question-bank" { python scripts/verify_bank.py eval/question_bank.json --min 100 }
} else {
    Write-Host "[SKIP] scripts/verify_bank.py 尚未生成（P3 交付物）" -ForegroundColor Yellow
}

# 4) .NET 测试（逐个项目，dotnet test 一次只能接一个）
Step "test-Core"        { dotnet test tests/AstralPath.Core.Tests -c Release }
Step "test-Persistence" { dotnet test tests/AstralPath.Persistence.Tests -c Release }
Step "test-Desktop"     { dotnet test tests/AstralPath.Desktop.Tests -c Release }
Step "test-Api"         { dotnet test tests/AstralPath.Api.Tests -c Release }
Step "test-Eval"        { dotnet test tests/AstralPath.Eval.Tests -c Release }
# 图谱 / OCR 算法回归（离线 Core 已改为链接主 Core 源码，漂移不可能再发生）
Step "test-MobileCore"  { dotnet test src/AstralPath.Mobile.Offline/AstralPath.Mobile.Tests -c Release }

# 5) 三端 HTML 同源 + C3 XSS 回归哨兵（统一由同步脚本提供）
Step "monolith-html-sync" { powershell -NoProfile -ExecutionPolicy Bypass -File scripts/sync-monolith-html.ps1 -Check }

# 6) 命名规范扫描（含旧名残留；脚本内置「政策声明语境」判定，
#    "禁止再使用旧名…"这类声明句不会误报 —— 勿再用朴素 Select-String 重复实现）
if (Test-Path scripts/naming_consistency.py) {
    Step "naming" { python scripts/naming_consistency.py --check }
}

# 结论
Write-Host "`n" ("=" * 60)
if ($failed.Count -eq 0) {
    Write-Host "ALL GREEN" -ForegroundColor Green
    exit 0
} else {
    Write-Host ("FAILED: " + ($failed -join ", ")) -ForegroundColor Red
    exit 1
}
