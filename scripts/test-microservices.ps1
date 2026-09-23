$ErrorActionPreference = "Continue"
$root = "C:\Users\18948\Documents\GitHub\-Knowledge-Debt-Astral-Path"
$report = Join-Path $root "deploy\微服务实机测试报告.md"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$svcs = @(
  @{n="concept-diffusion-svc";p=8081;u="/v1/diffusion/simulate";b='{"sourceKp":"K01","alpha":0.55,"maxDepth":6}'},
  @{n="exam-impact-svc";p=8082;u="/v1/exams/e1/impact";b='{"studentId":"demo-student-a","daysToExam":7}'},
  @{n="peer-cohort-svc";p=8083;u="/v1/cohorts/stats";b='{"courseCode":"ACCOUNTING","kpIds":["K01"]}'},
  @{n="study-group-svc";p=8084;u="/v1/study-groups/match";b='{"members":["demo-student-a","demo-student-b"],"size":2}'},
  @{n="micro-lesson-svc";p=8085;u="/v1/micro-lessons/draft";b='{"kpId":"K01","minutes":5}'},
  @{n="learning-velocity-svc";p=8086;u="/v1/velocity/fit";b='{"studentId":"demo-student-a","series":[0.4,0.6,0.7]}'},
  @{n="spaced-review-svc";p=8087;u="/v1/spaced-review/schedule";b='{"kpIds":["K01"],"days":14}'},
  @{n="prerequisite-simulator-svc";p=8088;u="/v1/prereq-simulator/simulate";b='{"sourceKp":"K01","boostKp":"K02"}'},
  @{n="knowledge-forecast-svc";p=8089;u="/v1/forecast/student";b='{"studentId":"demo-student-a","horizonDays":30}'},
  @{n="lab-bench-svc";p=8090;u="/internal/v1/lab/experiments";b='{"title":"score-w","owner":"demo"}'}
)
$lines = @()
$lines += "# 微服务实机测试报告"
$lines += ""
$lines += "产品：知债：星穹学途（Knowledge Debt: Astral Path）  "
$lines += "范围：十个扩展服务独立进程  "
$lines += "日期：" + (Get-Date -Format "yyyy-MM-dd HH:mm")
$lines += ""
$lines += "测试项：1 启动 2 核心功能 3 边界/异常 4 响应时间(ms)"
$lines += ""
$lines += "| 服务 | 端口 | 启动 | 健康 | 核心 | 空body | 隔离 | 健康ms | 核心ms | 状态 |"
$lines += "|---|---|---|---|---|---|---|---|---|---|"

foreach ($s in $svcs) {
  $start="FAIL"; $health="-"; $core="-"; $empty="-"; $iso="-"; $mh=0; $mc=0; $issues=@()
  $dll = Join-Path $root ("services\" + $s.n + "\bin\Release\net10.0\" + $s.n + ".dll")
  $work = Split-Path $dll
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $dotnet
  $psi.ArgumentList.Add($dll)
  $psi.WorkingDirectory = $work
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:" + $s.p
  $proc = [System.Diagnostics.Process]::Start($psi)
  if ($proc) { $start="OK" }
  Start-Sleep -Seconds 3

  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $h = Invoke-RestMethod ("http://127.0.0.1:" + $s.p + "/health/ready") -TimeoutSec 6
    $sw.Stop(); $mh = [int]$sw.ElapsedMilliseconds
    $health = "OK"
  } catch {
    $sw.Stop(); $mh = [int]$sw.ElapsedMilliseconds
    $health = "FAIL"; $issues += "health fail"
  }

  $sw2 = [Diagnostics.Stopwatch]::StartNew()
  try {
    $r = Invoke-WebRequest ("http://127.0.0.1:" + $s.p + $s.u) -Method POST -ContentType "application/json" -Body $s.b -UseBasicParsing -TimeoutSec 10
    $sw2.Stop(); $mc = [int]$sw2.ElapsedMilliseconds
    $core = "OK" + [string]$r.StatusCode
    if ($mc -gt 3000) { $issues += "core slow " + $mc }
  } catch {
    $sw2.Stop(); $mc = [int]$sw2.ElapsedMilliseconds
    $sc = "NA"
    try { $sc = [string]$_.Exception.Response.StatusCode.value__ } catch {}
    $core = "FAIL" + $sc
    $issues += "core fail " + $sc
  }

  try {
    $null = Invoke-WebRequest ("http://127.0.0.1:" + $s.p + $s.u) -Method POST -ContentType "application/json" -Body "{}" -UseBasicParsing -TimeoutSec 6
    $empty = "200"
  } catch {
    $sc = "NA"
    try { $sc = [string]$_.Exception.Response.StatusCode.value__ } catch {}
    if ($sc -like "4*") { $empty = $sc } else { $empty = $sc; $issues += "empty body " + $sc }
  }

  try {
    $null = Invoke-WebRequest ("http://127.0.0.1:" + $s.p + "/v1/velocity/fit") -Method POST -ContentType "application/json" -Body "{}" -UseBasicParsing -TimeoutSec 6
    $iso = "LEAK"
    $issues += "route leak"
  } catch {
    $sc = "NA"
    try { $sc = [string]$_.Exception.Response.StatusCode.value__ } catch {}
    if ($sc -eq "404") { $iso = "404" } else { $iso = $sc; $issues += "isolate " + $sc }
  }

  try { Stop-Process -Id $proc.Id -Force } catch {}
  $st = "可用"
  if ($issues.Count -gt 0) { $st = "部分" }
  if ($health -eq "FAIL") { $st = "不可用" }
  $lines += ("| " + $s.n + " | " + $s.p + " | " + $start + " | " + $health + " | " + $core + " | " + $empty + " | " + $iso + " | " + $mh + " | " + $mc + " | " + $st + " |")
  if ($issues.Count -gt 0) {
    $lines += ("问题：" + ($issues -join "；"))
  }
}

$lines += ""
$lines += "结论：见上表状态列；FAIL 项需修复。"
Set-Content -Path $report -Value ($lines -join "`n") -Encoding UTF8
Get-Content $report