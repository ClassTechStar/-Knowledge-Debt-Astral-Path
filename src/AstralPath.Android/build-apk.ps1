# 手工打包 AstralPath Android APK（无需 Gradle）
# 依赖：Android SDK build-tools + platform android.jar + JDK
$ErrorActionPreference = "Continue"
$ProgressPreference = "SilentlyContinue"
$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = "C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path\src\AstralPath.Android" }
$proj = $PSScriptRoot
$sdk = "C:\Users\18948\AppData\Local\Android\Sdk"
$bt = "$sdk\build-tools\36.0.0"
$androidJar = "$sdk\platforms\android-34\android.jar"
$javaHome = "C:\Program Files\Java\jdk-26.0.2.1"
$javac = "$javaHome\bin\javac.exe"
$keytool = "$javaHome\bin\keytool.exe"
$zipalign = "$bt\zipalign.exe"
$apksigner = "$bt\apksigner.bat"
$aapt2 = "$bt\aapt2.exe"
$d8 = "$bt\d8.bat"

$work = Join-Path $proj "build-manual"
$dist = Join-Path $proj "dist"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$work\gen","$work\obj","$work\classes","$work\apk","$dist" | Out-Null

Write-Host "[1/8] aapt2 compile resources"
$resOut = "$work\res.zip"
Get-ChildItem "$proj\app\src\main\res" -Recurse -Include *.xml | ForEach-Object {
  & $aapt2 compile $_.FullName -o $work | Out-Null
}
# aapt2 compile outputs flat files in $work; link them
$flat = Get-ChildItem $work -Filter "*.flat" | ForEach-Object { $_.FullName }

Write-Host "[2/8] aapt2 link"
& $aapt2 link -o "$work\apk\app.unsigned.apk" `
  -I $androidJar `
  --manifest "$proj\app\src\main\AndroidManifest.xml" `
  --java "$work\gen" `
  --auto-add-overlay `
  @($flat) 2>&1 | Out-String | Write-Host

Write-Host "[3/8] copy assets"
$assetsDir = "$work\apk\assets"
New-Item -ItemType Directory -Force -Path "$assetsDir\www" | Out-Null
Copy-Item "$proj\app\src\main\assets\www\*" "$assetsDir\www\" -Recurse -Force

Write-Host "[4/8] javac"
$sources = @(Get-ChildItem "$proj\app\src\main\java" -Recurse -Filter *.java | ForEach-Object { $_.FullName })
$sources += @(Get-ChildItem "$work\gen" -Recurse -Filter *.java | ForEach-Object { $_.FullName })
$argFile = "$work\javac.args"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("--release"); $lines.Add("17")
$lines.Add("-encoding"); $lines.Add("UTF-8")
$lines.Add("-classpath"); $lines.Add($androidJar)
$lines.Add("-d"); $lines.Add("$work\classes")
foreach ($s in $sources) { $lines.Add($s) }
[System.IO.File]::WriteAllLines($argFile, $lines, (New-Object System.Text.UTF8Encoding $false))
$oldEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $javac "@$argFile" 2>&1 | Out-String | Write-Host
$ErrorActionPreference = $oldEap

Write-Host "[5/8] d8 dex"
$classFiles = @(Get-ChildItem "$work\classes" -Recurse -Filter *.class | ForEach-Object { $_.FullName })
$d8file = "$work\d8.args"
$d8lines = New-Object System.Collections.Generic.List[string]
$d8lines.Add("--release"); $d8lines.Add("--lib"); $d8lines.Add($androidJar)
$d8lines.Add("--output"); $d8lines.Add("$work\apk")
foreach ($s in $classFiles) { $d8lines.Add($s) }
[System.IO.File]::WriteAllLines($d8file, $d8lines, (New-Object System.Text.UTF8Encoding $false))
$oldEap2 = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $d8 "@$d8file" 2>&1 | Out-String | Write-Host
$ErrorActionPreference = $oldEap2

Write-Host "[6/8] add assets + dex into apk"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$srcApk = "$work\apk\app.unsigned.apk"
$outApk = "$work\app.withpayload.apk"
Copy-Item $srcApk $outApk -Force
$zip = [System.IO.Compression.ZipFile]::Open($outApk, [System.IO.Compression.ZipArchiveMode]::Update)
if (Test-Path "$work\apk\classes.dex") {
  $e = $zip.CreateEntry("classes.dex")
  $s = [System.IO.File]::OpenRead("$work\apk\classes.dex"); $d = $e.Open(); $s.CopyTo($d); $d.Dispose(); $s.Dispose()
}
Get-ChildItem "$work\apk\assets" -Recurse -File | ForEach-Object {
  $rel = ("assets/" + $_.FullName.Substring("$work\apk\assets".Length).TrimStart('\','/')).Replace('\','/')
  $e = $zip.CreateEntry($rel)
  $s = [System.IO.File]::OpenRead($_.FullName); $d = $e.Open(); $s.CopyTo($d); $d.Dispose(); $s.Dispose()
  "  packed $rel"
}
$zip.Dispose()

Write-Host "[7/8] zipalign"
& $zipalign -f 4 $outApk "$work\app.aligned.apk"

Write-Host "[8/8] sign"
$ks = "$work\debug.keystore"
if (-not (Test-Path $ks)) {
  $ktOut = & $keytool -genkeypair -keystore $ks -storepass android -keypass android `
    -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 `
    -dname "CN=AstralPath Debug, OU=Dev, O=知债, L=City, S=ST, C=CN" 2>&1
  $ktOut | Out-String | Write-Host
}
if (-not (Test-Path $ks)) { throw "keystore not created" }
$oldEap3 = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $apksigner sign --ks $ks --ks-pass pass:android --key-pass pass:android `
  --out "$dist\AstralPath-1.3.0.apk" "$work\app.aligned.apk" 2>&1 | Out-String | Write-Host
$ErrorActionPreference = $oldEap3

Write-Host "APK => $dist\AstralPath-1.3.0.apk"
Get-Item "$dist\AstralPath-1.3.0.apk" | Select-Object FullName, Length
