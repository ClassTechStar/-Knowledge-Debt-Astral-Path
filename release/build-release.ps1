# 知债：星穹学途（AstralPath）双端安装包一键构建脚本
# 用法：powershell -ExecutionPolicy Bypass -File release\build-release.ps1
# 依赖：.NET SDK 10 · Inno Setup 7（C:\Program Files\Inno Setup 7）· Gradle 9.7.1（C:\Gradle）· JDK 17+ · Android SDK（%LOCALAPPDATA%\Android\Sdk）
# 说明：本脚本不改动任何源码，只刷新三端同源的单体 HTML 副本并产出安装包。
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot


# 0-0. 发布签名环境自动加载（P3-16：口令不入库，本机 ~/.android/astralpath-signing/signing.env）
# 说明：signing.env 为 $env:NAME=value 文本；用正则解析注入进程环境变量
#（dot-source 在部分受限环境不可靠，2026-10-17 T17 修复）
$signEnv = Join-Path $env:USERPROFILE '.android\astralpath-signing\signing.env'
if ((Test-Path $signEnv) -and -not $env:ASTRALPATH_KEYSTORE) {
    $signRaw = Get-Content $signEnv -Raw
    foreach ($m in [regex]::Matches($signRaw, '(?m)^\s*\$env:(\w+)\s*=\s*(.+)$')) {
        [Environment]::SetEnvironmentVariable($m.Groups[1].Value, $m.Groups[2].Value.Trim().Trim('"'), 'Process')
    }
    Write-Host 'signing env loaded from signing.env' -ForegroundColor Cyan
}

# 某些受限 shell/自动化环境缺失 ProgramFiles 变量，会导致 NuGet 静态初始化崩溃
# （Value cannot be null. Parameter 'path1'），此处显式补齐。
if (-not $env:ProgramFiles) { $env:ProgramFiles = 'C:\Program Files' }
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = 'C:\Program Files (x86)' }
if (-not $env:ANDROID_HOME) { $env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk" }

# 0-1. 快速契约门禁（2.4-L4：常量/意图表/公式跨端对拍不过不打包）
foreach ($gate in @("verify_constants.py", "verify_agent_intents.py", "verify_formulas.py")) {
    $g = Join-Path $repo "scripts\$gate"
    if (Test-Path $g) {
        Write-Host ">>> gate: $gate" -ForegroundColor Cyan
        python $g
        if ($LASTEXITCODE -ne 0) { throw "$gate 未通过，终止打包" }
    }
}

# 0. 三端同源：单体 HTML 分发到 Windows 壳与 Android assets
Copy-Item "$repo\deploy\monolith-web\index.html" "$repo\src\AstralPath.Monolith\Resources\index.html" -Force
Copy-Item "$repo\deploy\monolith-web\index.html" "$repo\src\AstralPath.Native\app\src\main\assets\www\index.html" -Force

# 0b. WASM OCR engine assets -> Android www (browser/Android share; Windows shell uses host bridge)
# Exclude maintenance files (download.log / dl-vendor.ps1 / relaytest.txt)
$engSrc = "$repo\deploy\monolith-web\ocr-engine"
$engDst = "$repo\src\AstralPath.Native\app\src\main\assets\www\ocr-engine"
New-Item -ItemType Directory -Force $engDst | Out-Null
# 递归同步（含 cmaps\ 等子目录；Fix3 的 cMapUrl 依赖 ocr-engine\cmaps 随包分发）
Get-ChildItem $engSrc -Recurse -File | Where-Object { $_.Name -notin @("download.log","dl-vendor.ps1","relaytest.txt") } | ForEach-Object {
    $rel = $_.FullName.Substring($engSrc.Length).TrimStart('\')
    $dst = Join-Path $engDst $rel
    New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
    Copy-Item $_.FullName $dst -Force
}
if (-not (Test-Path "$engDst\cmaps")) { Write-Host "WARN: ocr-engine/cmaps 未同步，pdf.js cMapUrl 将 404（非致命）" }
Copy-Item "$repo\deploy\monolith-web\serve.ps1" "$repo\src\AstralPath.Native\app\src\main\assets\www\" -Force
# 0-2. tessdata 保障（iss 打包 tools\tessdata；*.traineddata 不入库，缺失时从 ocr-engine 复制）
#（2026-10-17 T17 修复：此前缺失会导致 ISCC 编译直接失败）
$tessDir = "$repo\tools\tessdata"
foreach ($tf in @('chi_sim.traineddata', 'eng.traineddata')) {
    $dst = Join-Path $tessDir $tf
    if (-not (Test-Path $dst)) {
        $src = "$repo\deploy\monolith-web\ocr-engine\$tf"
        if (Test-Path $src) { New-Item -ItemType Directory -Force $tessDir | Out-Null; Copy-Item $src $dst -Force; Write-Host "tessdata 补齐: $tf" }
        else { Write-Host "WARN: 缺 $tf 且无源可复制（ISCC 可能失败）" -ForegroundColor Yellow }
    }
}

# 0c. 新增（2026-09-26）：三端同源守卫 —— 防止源文件被回退后再打包，静默把补丁覆盖回旧版。
#     1) 三份 index.html 的 SHA256 必须一致；2) 源文件必须带 pdf.js 文本层补丁，否则直接失败。
$htmlTargets = @(
    "$repo\deploy\monolith-web\index.html",
    "$repo\src\AstralPath.Monolith\Resources\index.html",
    "$repo\src\AstralPath.Native\app\src\main\assets\www\index.html"
)
$htmlHashes = @($htmlTargets | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash })
if (($htmlHashes | Sort-Object -Unique).Count -ne 1) {
    throw ("monolith-html-sync FAILED: " + ($htmlHashes -join ' != '))
}
$htmlSrc = Get-Content "$repo\deploy\monolith-web\index.html" -Raw
if ($htmlSrc -notmatch 'pdfJsTextLayer' -or $htmlSrc -notmatch 'ocr-engine/pdf\.min\.js') {
    throw "monolith HTML patch missing (pdfJsTextLayer / ocr-engine/pdf.min.js); refuse to build"
}
Write-Host ("monolith-html-sync OK sha256=" + $htmlHashes[0])


# 1. Windows：.NET 10 自包含发布 + Inno Setup 7 编译
dotnet restore "$repo\src\AstralPath.Monolith\AstralPath.Monolith.csproj" -r win-x64
 dotnet publish "$repo\src\AstralPath.Monolith\AstralPath.Monolith.csproj" -c Release -r win-x64 --self-contained true --no-restore -o "$repo\src\AstralPath.Monolith\publish"
& 'C:\Program Files\Inno Setup 7\ISCC.exe' /O"$repo\deploy\win-install\dist" "$repo\src\AstralPath.Monolith\setup-monolith.iss"

# 2. Android：gradle assembleRelease（工程自带 release 签名配置）+ 归档到 dist
Push-Location "$repo\src\AstralPath.Native"
try { & C:\Gradle\bin\gradle.bat assembleRelease --console=plain } finally { Pop-Location }
Copy-Item "$repo\src\AstralPath.Native\app\build\outputs\apk\release\app-release.apk" "$repo\src\AstralPath.Native\dist\AstralPath-Android-2.6.0-Store.apk" -Force

# 3. 汇总到 release/
New-Item -ItemType Directory -Force "$repo\release" | Out-Null
Copy-Item "$repo\deploy\win-install\dist\AstralPath-Monolith-Setup-2.6.0.exe" "$repo\release\" -Force
Copy-Item "$repo\src\AstralPath.Native\dist\AstralPath-Android-2.6.0-Store.apk" "$repo\release\" -Force
Write-Host '双端安装包构建完成，见 release\ 目录'
