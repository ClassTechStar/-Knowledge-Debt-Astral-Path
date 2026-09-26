# 下载/复制 OCR WASM 引擎离线资源到 deploy\monolith-web\ocr-engine\（后台运行）
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$dir = 'C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\deploy\monolith-web\ocr-engine'
New-Item -ItemType Directory -Force $dir | Out-Null
$log = Join-Path $dir 'download.log'
"start $(Get-Date -Format o)" | Set-Content -Path $log -Encoding UTF8
$files = @(
    @('tesseract.min.js', 'https://cdn.jsdelivr.net/npm/tesseract.js@5.1.1/dist/tesseract.min.js'),
    @('worker.min.js', 'https://cdn.jsdelivr.net/npm/tesseract.js@5.1.1/dist/worker.min.js'),
    @('tesseract-core-simd-lstm.wasm.js', 'https://cdn.jsdelivr.net/npm/tesseract.js-core@5.1.1/tesseract-core-simd-lstm.wasm.js'),
    @('tesseract-core-lstm.wasm.js', 'https://cdn.jsdelivr.net/npm/tesseract.js-core@5.1.1/tesseract-core-lstm.wasm.js'),
    @('pdf.min.js', 'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/legacy/build/pdf.min.js'),
    @('pdf.worker.min.js', 'https://cdn.jsdelivr.net/npm/pdfjs-dist@3.11.174/legacy/build/pdf.worker.min.js')
)
foreach ($f in $files) {
    $out = Join-Path $dir $f[0]
    Invoke-WebRequest -Uri $f[1] -OutFile $out -UseBasicParsing
    "OK $($f[0]) $((New-Object IO.FileInfo($out)).Length) bytes" | Add-Content -Path $log -Encoding UTF8
}
Copy-Item 'C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\tools\tessdata\chi_sim.traineddata' (Join-Path $dir 'chi_sim.traineddata') -Force
Copy-Item 'C:\Users\18948\XiaomiMiMoProjects\Knowledge Debt Astral Path\tools\tessdata\eng.traineddata' (Join-Path $dir 'eng.traineddata') -Force
"traineddata: chi_sim $((New-Object IO.FileInfo((Join-Path $dir 'chi_sim.traineddata'))).Length) / eng $((New-Object IO.FileInfo((Join-Path $dir 'eng.traineddata'))).Length) bytes" | Add-Content -Path $log -Encoding UTF8
"done $(Get-Date -Format o)" | Add-Content -Path $log -Encoding UTF8
