# 知债：星穹学途 · Windows 便携版（免安装）一键构建
# =====================================================
# 用法：powershell -ExecutionPolicy Bypass -File release\build-portable.ps1
# 产物：release\AstralPath-Portable-<版本>\（解压即用文件夹）+ 同名 .zip
# 依赖：.NET 10 SDK（发布用 .NET 自包含，目标机免装 .NET；WebView2 用系统自带）
# 说明：不改动源码；发布后随包 tools\（管线四件套 + tessdata + ocr-venv）
#       与 Resources\ocr-engine\（WASM 后备引擎）。
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$version = "2.2.0"
$out = Join-Path $repo "release\AstralPath-Portable-$version"

Write-Host ">>> [1/4] dotnet publish（self-contained win-x64）" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$repo\src\AstralPath.Monolith" -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host ">>> [2/4] WASM OCR 引擎 → Resources\ocr-engine（浏览器式后备；宿主桥为主）" -ForegroundColor Cyan
Copy-Item "$repo\deploy\monolith-web\ocr-engine" "$out\Resources\ocr-engine" -Recurse -Force

Write-Host ">>> [3/4] tools\ 运行时（管线 + tessdata + ocr-venv）" -ForegroundColor Cyan
New-Item -ItemType Directory -Force (Join-Path $out "tools") | Out-Null
foreach ($f in @("ocr_pipeline.py", "deep_chapters.py", "kg_algorithm.py", "kg_builder.py", "agent-intents.json")) {
    Copy-Item "$repo\tools\$f" "$out\tools\" -Force
}
Copy-Item "$repo\tools\tessdata" "$out\tools\tessdata" -Recurse -Force
robocopy "$repo\tools\ocr-venv" "$out\tools\ocr-venv" /E /R:1 /W:1 /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "ocr-venv copy failed" }

Write-Host ">>> [4/4] 打包 zip" -ForegroundColor Cyan
$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip -Force

$size = [math]::Round((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host ("DONE 便携版就绪：{0}（{1}MB）+ {2}.zip" -f $out, $size, (Split-Path $out -Leaf)) -ForegroundColor Green
Write-Host "启动：双击 AstralPath.Monolith.exe；宿主桥自动发现 tools\ocr_pipeline.py 与 tools\ocr-venv。" -ForegroundColor Green
