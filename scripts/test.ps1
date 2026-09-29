param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$env:SANDVNC_ROOT = $root
$env:TEMP = Join-Path $root '.cache'
$env:TMP = $env:TEMP
if (Test-Path runtime/viewers.json) {
    $previous = Get-Content -Raw runtime/viewers.json | ConvertFrom-Json
    $running = Get-Process -Id $previous.brokerPid -ErrorAction SilentlyContinue
    if ($running -and $running.ProcessName -eq 'SandVnc') { throw 'Stop the existing demo with scripts/stop-demo.ps1 first' }
}
if (!$SkipBuild) { & ./scripts/build.ps1 *> evidence/build.log }
$broker = Start-Process -FilePath "$root\out\SandVnc.exe" -ArgumentList '--demo' -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput "$root\runtime-broker.log" -RedirectStandardError "$root\runtime-error.log" -PassThru
try {
    $ready = $false
    for ($i = 0; $i -lt 200; $i++) {
        if ($broker.HasExited) { throw (Get-Content -Raw runtime-error.log) }
        if (Test-Path runtime/viewers.json) {
            try {
                $runtime = Get-Content -Raw runtime/viewers.json | ConvertFrom-Json
                $ready = $runtime.brokerPid -eq $broker.Id
            } catch { $ready = $false }
        }
        if ($ready) { break }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready) { throw 'Demo startup timed out' }
    & ./scripts/measure.ps1 -Mode idle
    & ./scripts/measure.ps1 -Mode readers
    node tests/visual-timing.mjs
    if ($LASTEXITCODE) { throw 'Visual response test failed' }
    node tests/frame-order.mjs
    if ($LASTEXITCODE) { throw 'Capture ordering test failed' }
    node tests/concurrent.mjs
    if ($LASTEXITCODE) { throw 'Concurrent input tests failed' }
} finally {
    if ($ready) { & ./scripts/stop-demo.ps1 }
    elseif (!$broker.HasExited) { Stop-Process -Id $broker.Id }
}
