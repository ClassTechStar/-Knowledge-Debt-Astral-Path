# 知债：星穹学途 · Windows 便携版（免安装）一键构建
# =====================================================
# 用法：powershell -ExecutionPolicy Bypass -File release\build-portable.ps1
# 产物：release\AstralPath-Portable-<版本>\（解压即用文件夹）+ 同名 .zip
# 依赖：.NET 10 SDK（发布用 .NET 自包含，目标机免装 .NET；WebView2 用系统自带）
# 说明：不改动源码；发布后随包 tools\（管线 + tessdata + ocr-venv 瘦身）与 Resources\ocr-engine\。
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$version = "2.2.0"
$out = Join-Path $repo "release\AstralPath-Portable-$version"

Write-Host ">>> [1/4] dotnet publish (self-contained win-x64)" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$repo\src\AstralPath.Monolith" -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host ">>> [2/4] WASM OCR engine -> Resources\ocr-engine" -ForegroundColor Cyan
Copy-Item "$repo\deploy\monolith-web\ocr-engine" "$out\Resources\ocr-engine" -Recurse -Force

Write-Host ">>> [3/4] tools runtime (pipeline + tessdata + slim ocr-venv)" -ForegroundColor Cyan
New-Item -ItemType Directory -Force (Join-Path $out "tools") | Out-Null
foreach ($f in @("ocr_pipeline.py", "deep_chapters.py", "kg_algorithm.py", "kg_builder.py", "agent-intents.json", "table_extract.py")) {
    Copy-Item "$repo\tools\$f" "$out\tools\" -Force
}
Copy-Item "$repo\tools\tessdata" "$out\tools\tessdata" -Recurse -Force
robocopy "$repo\tools\ocr-venv" "$out\tools\ocr-venv" /E /R:1 /W:1 /NFL /NDL /NP /XD __pycache__ tests test | Out-Null
if ($LASTEXITCODE -ge 8) { throw "ocr-venv copy failed" }

# P1-6 portable slim: pipeline needs PIL/pypdf/pypdfium2/pymupdf/numpy/onnx/rapidocr/jieba
# Drop OpenCV (cv2 ~112MB) / shapely / pip / unused locale packs
$venvPkgs = Join-Path $out "tools\ocr-venv\Lib\site-packages"
$junkNames = @(
  "cv2", "shapely", "shapely.libs", "pip", "setuptools", "wheel",
  "google", "pkg_resources"
)
foreach ($j in $junkNames) {
    $p = Join-Path $venvPkgs $j
    if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
}
Get-ChildItem $venvPkgs -Directory | Where-Object {
    $_.Name -match '^(cv2|opencv|shapely|pip|setuptools|wheel|google)[.-]'
} | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

foreach ($loc in @("cs","de","es","fr","it","ja","ko","pl","pt-BR","ru","tr")) {
    $d = Join-Path $out $loc
    if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ">>> [4/4] zip" -ForegroundColor Cyan
$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip -Force

$size = [math]::Round((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host ("DONE portable ready: {0} ({1}MB) + {2}.zip" -f $out, $size, (Split-Path $out -Leaf)) -ForegroundColor Green
Write-Host "Start: double-click AstralPath.Monolith.exe; host bridge finds tools\ocr_pipeline.py" -ForegroundColor Green
