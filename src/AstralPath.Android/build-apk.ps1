# =============================================================================
#  知债：星穹学途（Knowledge Debt: Astral Path）· Android APK 构建脚本
#
#  用途：无需 Gradle，直接用 Android SDK 命令行工具产出**已签名的发布版 APK**。
#
#  流程（8 步）：
#    1) aapt2 compile  资源 → .flat
#    2) aapt2 link     生成基础 APK（注入 minSdk/targetSdk/versionCode/versionName）
#    3) 拷贝 assets/www（与 Web 端 wwwroot 同一份 index.html）
#    4) javac          编译 Java 源码（--release 17）
#    5) d8             class → classes.dex（--release --min-api）
#    6) 打包           classes.dex + assets 写入 APK（zip）
#    7) zipalign       4 字节对齐（**必须在签名前**）
#    8) apksigner      以**发布密钥**签名（启用 v1/v2/v3），随后自动校验
#
#  密钥管理（最佳实践）：
#    · 密钥库与口令存于 keystore.properties（已加入 .gitignore，不进版本库）
#    · 首次运行自动生成 2048 位 RSA 发布密钥；请连同口令一起备份
#    · 字段说明见 keystore.properties.example
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File build-apk.ps1
# =============================================================================
$ErrorActionPreference = "Continue"   # 原生工具会写 stderr 提示，故不用 Stop，统一改用 $LASTEXITCODE 判定
$ProgressPreference = "SilentlyContinue"

$proj = $PSScriptRoot
$sdk  = if ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { Join-Path $env:LOCALAPPDATA "Android\Sdk" }
$bt         = Join-Path $sdk "build-tools\36.0.0"
$aapt2      = Join-Path $bt "aapt2.exe"
$zipalign   = Join-Path $bt "zipalign.exe"
$apksigner  = Join-Path $bt "apksigner.bat"
$d8         = Join-Path $bt "d8.bat"
$androidJar = Join-Path $sdk "platforms\android-34\android.jar"

$javaHome = $env:JAVA_HOME
if (-not $javaHome) {
    foreach ($cand in @("$env:ProgramFiles\Java\jdk-26.0.2.1", "$env:ProgramFiles\Android\Android Studio\jbr")) {
        if (Test-Path (Join-Path $cand "bin\javac.exe")) { $javaHome = $cand; break }
    }
}
if (-not $javaHome) { throw "未找到 JDK：请设置 JAVA_HOME 或安装 JDK 17+" }
$javac   = Join-Path $javaHome "bin\javac.exe"
$keytool = Join-Path $javaHome "bin\keytool.exe"

$minSdk    = 26                       # Android 8.0+
$targetSdk = 34
$verCode   = 13
$verName   = "1.3.0"
$apkName   = "AstralPath-$verName-release.apk"

foreach ($t in @($aapt2, $zipalign, $apksigner, $d8, $androidJar, $javac, $keytool)) {
    if (-not (Test-Path $t)) { throw "构建工具缺失：$t" }
}
Write-Host "[env] JDK = $javaHome"
Write-Host "[env] SDK = $sdk"

$work = Join-Path $proj "build-manual"
$dist = Join-Path $proj "dist"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$work\gen", "$work\obj", "$work\classes", "$work\apk", "$dist" | Out-Null

# ── 1) 资源编译 ──────────────────────────────────────────────────────────────
Write-Host "[1/8] aapt2 compile 资源"
Get-ChildItem "$proj\app\src\main\res" -Recurse -File | ForEach-Object {
    & $aapt2 compile $_.FullName -o $work | Out-Null
}
$flat = @(Get-ChildItem $work -Filter "*.flat" | ForEach-Object { $_.FullName })
if ($flat.Count -eq 0) { throw "aapt2 compile 未产出任何 .flat" }
Write-Host "      .flat 数量 = $($flat.Count)"

# ── 2) 资源链接（注入 SDK 版本与版本号）──────────────────────────────────────
Write-Host "[2/8] aapt2 link"
& $aapt2 link -o "$work\apk\app.unsigned.apk" `
    -I $androidJar `
    --manifest "$proj\app\src\main\AndroidManifest.xml" `
    --java "$work\gen" `
    --min-sdk-version $minSdk `
    --target-sdk-version $targetSdk `
    --version-code $verCode `
    --version-name $verName `
    --auto-add-overlay `
    @flat
if ($LASTEXITCODE -ne 0) { throw "aapt2 link 失败（exit=$LASTEXITCODE）" }

# ── 3) assets ────────────────────────────────────────────────────────────────
Write-Host "[3/8] 拷贝 assets/www"
$assetsDir = "$work\apk\assets"
New-Item -ItemType Directory -Force -Path "$assetsDir\www" | Out-Null
Copy-Item "$proj\app\src\main\assets\www\*" "$assetsDir\www\" -Recurse -Force

# ── 4) javac ─────────────────────────────────────────────────────────────────
Write-Host "[4/8] javac"
$sources  = @(Get-ChildItem "$proj\app\src\main\java" -Recurse -Filter *.java | ForEach-Object { $_.FullName })
$sources += @(Get-ChildItem "$work\gen" -Recurse -Filter *.java | ForEach-Object { $_.FullName })
$argFile  = "$work\javac.args"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("--release"); $lines.Add("17")
$lines.Add("-encoding"); $lines.Add("UTF-8")
$lines.Add("-Xlint:-deprecation")   # 壳内 onActivityResult 属有意保留的兼容用法
$lines.Add("-classpath"); $lines.Add($androidJar)
$lines.Add("-d"); $lines.Add("$work\classes")
foreach ($s in $sources) { $lines.Add($s) }
[System.IO.File]::WriteAllLines($argFile, $lines, (New-Object System.Text.UTF8Encoding $false))
$javacOut = & $javac "@$argFile" 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { Write-Host $javacOut; throw "javac 失败（exit=$LASTEXITCODE）" }

# ── 5) d8 ────────────────────────────────────────────────────────────────────
Write-Host "[5/8] d8"
$classFiles = @(Get-ChildItem "$work\classes" -Recurse -Filter *.class | ForEach-Object { $_.FullName })
$d8file = "$work\d8.args"
$d8lines = New-Object System.Collections.Generic.List[string]
$d8lines.Add("--release"); $d8lines.Add("--lib"); $d8lines.Add($androidJar)
$d8lines.Add("--min-api"); $d8lines.Add("$minSdk")
$d8lines.Add("--output"); $d8lines.Add("$work\apk")
foreach ($s in $classFiles) { $d8lines.Add($s) }
[System.IO.File]::WriteAllLines($d8file, $d8lines, (New-Object System.Text.UTF8Encoding $false))
$d8Out = & $d8 "@$d8file" 2>&1 | Out-String
if (-not (Test-Path "$work\apk\classes.dex")) { Write-Host $d8Out; throw "d8 未产出 classes.dex" }

# ── 6) 打包 dex + assets ─────────────────────────────────────────────────────
Write-Host "[6/8] 打包 classes.dex 与 assets"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$outApk = "$work\app.withpayload.apk"
Copy-Item "$work\apk\app.unsigned.apk" $outApk -Force
$zip = [System.IO.Compression.ZipFile]::Open($outApk, [System.IO.Compression.ZipArchiveMode]::Update)
$dex = $zip.CreateEntry("classes.dex", [System.IO.Compression.CompressionLevel]::Optimal)
$s = [System.IO.File]::OpenRead("$work\apk\classes.dex"); $d = $dex.Open(); $s.CopyTo($d); $d.Dispose(); $s.Dispose()
$assetCount = 0
Get-ChildItem "$work\apk\assets" -Recurse -File | ForEach-Object {
    $rel = ("assets/" + $_.FullName.Substring("$work\apk\assets".Length).TrimStart('\', '/')).Replace('\', '/')
    $e = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
    $fs = [System.IO.File]::OpenRead($_.FullName); $ds = $e.Open(); $fs.CopyTo($ds); $ds.Dispose(); $fs.Dispose()
    $assetCount++
}
$zip.Dispose()
Write-Host "      assets 文件数 = $assetCount"

# ── 7) zipalign（必须在签名之前）─────────────────────────────────────────────
Write-Host "[7/8] zipalign -f 4"
& $zipalign -f 4 $outApk "$work\app.aligned.apk"
if ($LASTEXITCODE -ne 0) { throw "zipalign 失败（exit=$LASTEXITCODE）" }

# ── 8) 发布签名 ──────────────────────────────────────────────────────────────
Write-Host "[8/8] 以发布密钥签名"
$propsPath = Join-Path $proj "keystore.properties"
if (-not (Test-Path $propsPath)) {
    Write-Host "      未找到 keystore.properties，生成发布密钥与凭据文件"
    $ksDir = Join-Path $proj "keystore"
    New-Item -ItemType Directory -Force -Path $ksDir | Out-Null
    $ksPath = Join-Path $ksDir "astralpath-release.jks"
    $chars = ((48..57) + (65..90) + (97..122)) | ForEach-Object { [char]$_ }
    $storePass = -join ($chars | Get-Random -Count 24)
    # PKCS12 密钥库不支持与存储口令不同的密钥口令（keytool 会忽略 -keypass 并告警），
    # 故密钥口令与存储口令保持一致；如需独立口令请改用 -storetype JKS。
    $keyPass = $storePass
    & $keytool -genkeypair -v -keystore $ksPath -alias astralpath `
        -keyalg RSA -keysize 2048 -validity 10950 `
        -storepass $storePass -keypass $keyPass `
        -dname "CN=Knowledge Debt: Astral Path, OU=AstralPath, O=AstralPath Team, L=Guangzhou, ST=Guangdong, C=CN"
    if ($LASTEXITCODE -ne 0) { throw "keytool 生成密钥失败" }
    @"
# 发布签名凭据（**请勿提交到版本库**；本文件已在 .gitignore 中）
# 请连同 keystore 文件一起备份：丢失后将无法对同一应用发布更新。
storeFile=keystore/astralpath-release.jks
storePassword=$storePass
keyAlias=astralpath
keyPassword=$keyPass
"@ | Set-Content -Path $propsPath -Encoding UTF8
    Write-Host "      已生成密钥库：$ksPath"
    Write-Host "      已生成凭据文件：$propsPath（请备份）"
}

$cfg = @{}
Get-Content $propsPath | Where-Object { $_ -match "^\s*[^#].*=" } | ForEach-Object {
    $kv = $_.Split('=', 2); $cfg[$kv[0].Trim()] = $kv[1].Trim()
}
$ksFile = Join-Path $proj $cfg['storeFile']
if (-not (Test-Path $ksFile)) { throw "密钥库不存在：$ksFile" }
# 密钥口令缺省＝存储口令（PKCS12 语义；JKS 可在 keystore.properties 显式指定 keyPassword）
$keyPassEff = if ($cfg['keyPassword']) { $cfg['keyPassword'] } else { $cfg['storePassword'] }

$outPath = Join-Path $dist $apkName
& $apksigner sign `
    --ks $ksFile `
    --ks-key-alias $($cfg['keyAlias']) `
    --ks-pass "pass:$($cfg['storePassword'])" `
    --key-pass "pass:$keyPassEff" `
    --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true `
    --out $outPath "$work\app.aligned.apk"
if ($LASTEXITCODE -ne 0) { throw "apksigner 签名失败（exit=$LASTEXITCODE）" }

# ── 校验 ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== 签名校验（apksigner verify）==="
& $apksigner verify --print-certs --verbose $outPath
if ($LASTEXITCODE -ne 0) { throw "APK 签名校验未通过" }

Write-Host ""
Write-Host "=== APK 元信息（aapt2 dump badging）==="
& $aapt2 dump badging $outPath | Select-Object -First 14

$size = (Get-Item $outPath).Length
Write-Host ""
Write-Host "APK    => $outPath"
Write-Host "大小   => $([math]::Round($size / 1KB, 1)) KB"
Write-Host "SHA256 => $((Get-FileHash $outPath -Algorithm SHA256).Hash)"
