# AstralPath Web launcher static server (zero-dependency, PowerShell built-in)
# =====================================================================
# Purpose: index.html opened via file:// cannot use Worker/fetch for local
# resources (browser sandbox), so the bundled WASM OCR engine (ocr-engine\)
# fails to load. This script starts a loopback-only static file server; the
# browser then opens http://127.0.0.1:<port>/index.html with full OCR.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File serve.ps1 [-Port 8799] [-NoOpen] [-IdleTimeoutMin 30]
param(
    [int]$Port = 8799,
    [switch]$NoOpen,
    [int]$IdleTimeoutMin = 30
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
if (-not $Root) { $Root = (Get-Location).Path }

$Mime = @{
    '.html' = 'text/html; charset=utf-8'
    '.htm'  = 'text/html; charset=utf-8'
    '.js'   = 'text/javascript; charset=utf-8'
    '.mjs'  = 'text/javascript; charset=utf-8'
    '.css'  = 'text/css; charset=utf-8'
    '.json' = 'application/json; charset=utf-8'
    '.png'  = 'image/png'
    '.jpg'  = 'image/jpeg'
    '.svg'  = 'image/svg+xml'
    '.ico'  = 'image/x-icon'
    '.pdf'  = 'application/pdf'
    '.txt'  = 'text/plain; charset=utf-8'
    '.md'   = 'text/plain; charset=utf-8'
    '.wasm' = 'application/wasm'
    '.traineddata' = 'application/octet-stream'
    '.map'  = 'application/json; charset=utf-8'
}
$DefaultMime = 'application/octet-stream'

function Get-MimeType([string]$ext) {
    if ($Mime.ContainsKey($ext)) { return $Mime[$ext] }
    return $DefaultMime
}

function Send-Response($stream, [int]$code, [string]$contentType, [byte[]]$body, [bool]$headOnly) {
    $reason = switch ($code) {
        200 { 'OK' } 204 { 'No Content' } 404 { 'Not Found' } 405 { 'Method Not Allowed' }
        413 { 'Payload Too Large' } 500 { 'Internal Server Error' } default { 'OK' }
    }
    $hdr = "HTTP/1.1 $code $reason`r`n" +
           "Content-Type: $contentType`r`n" +
           "Content-Length: $($body.Length)`r`n" +
           "Cache-Control: no-store`r`n" +
           "Access-Control-Allow-Origin: *`r`n" +
           "Connection: close`r`n`r`n"
    $hb = [Text.Encoding]::ASCII.GetBytes($hdr)
    $stream.Write($hb, 0, $hb.Length)
    if (-not $headOnly -and $body.Length -gt 0) {
        $stream.Write($body, 0, $body.Length)
    }
    $stream.Flush()
}

# Port probing (try 12 consecutive ports from $Port)
$listener = $null
$chosen = $Port
for ($try = 0; $try -lt 12; $try++) {
    $candidate = $Port + $try
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $candidate)
        $listener.Start(8)
        $chosen = $candidate
        break
    } catch {
        $listener = $null
        continue
    }
}
if (-not $listener) { Write-Error "ports $Port..$($Port+11) all in use"; exit 1 }

$reportPath = Join-Path $Root 'selftest-report.json'
$logPath = Join-Path $Root 'selftest-server.log'
"[{0}] serve.ps1 started root={1} url=http://127.0.0.1:{2}/" -f (Get-Date -Format o), $Root, $chosen | Set-Content -Path $logPath -Encoding UTF8

if (-not $NoOpen) {
    Start-Process "http://127.0.0.1:$chosen/index.html"
}

$utf8 = [Text.Encoding]::UTF8
$lastActive = [DateTime]::UtcNow
$maxBody = 8MB

try {
    while ($true) {
        if ((([DateTime]::UtcNow - $lastActive).TotalMinutes) -ge $IdleTimeoutMin) {
            "[{0}] idle timeout ({1} min), exit" -f (Get-Date -Format o), $IdleTimeoutMin | Add-Content -Path $logPath -Encoding UTF8
            break
        }
        if (-not $listener.Pending()) {
            Start-Sleep -Milliseconds 200
            continue
        }
        $client = $listener.AcceptTcpClient()
        $lastActive = [DateTime]::UtcNow
        $stream = $client.GetStream()
        $stream.ReadTimeout = 30000
        $stream.WriteTimeout = 120000
        try {
            # ---- read request head ----
            $headBytes = New-Object System.Collections.Generic.List[byte]
            $buf = New-Object byte[] 8192
            $headerEnd = -1
            while ($headerEnd -lt 0) {
                $n = $stream.Read($buf, 0, $buf.Length)
                if ($n -le 0) { break }
                for ($i = 0; $i -lt $n; $i++) { $headBytes.Add($buf[$i]) }
                $txtProbe = [Text.Encoding]::ASCII.GetString($headBytes.ToArray())
                $headerEnd = $txtProbe.IndexOf("`r`n`r`n")
                if ($headerEnd -ge 0) { break }
                if ($headBytes.Count -gt 1MB) { break }
            }
            if ($headerEnd -lt 0) { $client.Close(); continue }
            $headText = [Text.Encoding]::ASCII.GetString($headBytes.ToArray(), 0, $headerEnd)
            $extraBodyBytes = $headBytes.Count - ($headerEnd + 4)
            $lines = $headText -split "`r`n"
            $reqLine = $lines[0]
            $parts = $reqLine -split ' '
            if ($parts.Count -lt 2) { $client.Close(); continue }
            $method = $parts[0].ToUpperInvariant()
            $rawUrl = $parts[1]
            $contentLength = 0
            foreach ($ln in $lines) {
                if ($ln -match '^Content-Length:\s*(\d+)') { $contentLength = [int]$Matches[1] }
            }
            # ---- read remaining body ----
            if ($contentLength -lt 0 -or $contentLength -gt $maxBody) { $contentLength = 0 }
            $body = New-Object byte[] $contentLength
            $got = [Math]::Max(0, [Math]::Min($extraBodyBytes, $contentLength))
            if ($got -gt 0) {
                [Array]::Copy($headBytes.ToArray(), $headerEnd + 4, $body, 0, $got)
            }
            while ($got -lt $contentLength) {
                $n = $stream.Read($body, $got, $contentLength - $got)
                if ($n -le 0) { break }
                $got += $n
            }

            # ---- routing ----
            $pathAndQuery = $rawUrl
            $queryIdx = $pathAndQuery.IndexOf('?')
            if ($queryIdx -ge 0) { $pathAndQuery = $pathAndQuery.Substring(0, $queryIdx) }
            $path = [Uri]::UnescapeDataString($pathAndQuery)
            if ($path.EndsWith('/')) { $path = $path + 'index.html' }

            if ($method -eq 'POST' -and ($path -eq '/__selftest/report' -or $path -eq '/selftest-report')) {
                try { [IO.File]::WriteAllBytes($reportPath, $body) } catch {}
                Send-Response $stream 200 'application/json; charset=utf-8' ([Text.Encoding]::ASCII.GetBytes('{"ok":true}')) $false
                "[{0}] selftest report saved ({1} bytes)" -f (Get-Date -Format o), $body.Length | Add-Content -Path $logPath -Encoding UTF8
            }
            elseif ($method -ne 'GET' -and $method -ne 'HEAD') {
                Send-Response $stream 405 'text/plain' ([Text.Encoding]::ASCII.GetBytes('method not allowed')) $false
            }
            elseif ($path -eq '/__selftest/report' -or $path -eq '/selftest-report') {
                $b = if (Test-Path -LiteralPath $reportPath) { [IO.File]::ReadAllBytes($reportPath) } else { [Text.Encoding]::ASCII.GetBytes('{}') }
                Send-Response $stream 200 'application/json; charset=utf-8' $b $false
            }
            else {
                $rel = $path.TrimStart('/')
                if ($rel -match '\.\.') {
                    Send-Response $stream 404 'text/plain' ([Text.Encoding]::ASCII.GetBytes('forbidden')) $false
                } else {
                    $fsPath = Join-Path $Root ($rel -replace '/', '\')
                    $full = [IO.Path]::GetFullPath($fsPath)
                    $rootFull = [IO.Path]::GetFullPath($Root)
                    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $full -PathType Leaf)) {
                        Send-Response $stream 404 'text/plain' ([Text.Encoding]::ASCII.GetBytes('not found')) $false
                    } else {
                        $ext = [IO.Path]::GetExtension($full).ToLowerInvariant()
                        $mimeType = Get-MimeType $ext
                        $bytes = [IO.File]::ReadAllBytes($full)
                        Send-Response $stream 200 $mimeType $bytes ($method -eq 'HEAD')
                        if ($rel -match '^ocr-engine/' -or $rel -eq 'index.html' -or $rel -like 'selftest*') {
                            "[{0}] GET {1} -> 200 ({2} bytes)" -f (Get-Date -Format o), $path, $bytes.Length | Add-Content -Path $logPath -Encoding UTF8
                        }
                    }
                }
            }
        } catch {
            try { "[{0}] error: {1}" -f (Get-Date -Format o), $_.Exception.Message | Add-Content -Path $logPath -Encoding UTF8 } catch {}
        } finally {
            try { $stream.Close(); $client.Close() } catch {}
        }
    }
} finally {
    try { $listener.Stop() } catch {}
}
