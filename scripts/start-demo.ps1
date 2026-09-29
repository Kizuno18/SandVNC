param([int]$Port = 8765)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$env:SANDVNC_ROOT = $root
$env:TEMP = Join-Path $root '.cache'
$env:TMP = $env:TEMP
& (Join-Path $root 'out/SandVnc.exe') --demo --port $Port
if ($LASTEXITCODE) { throw "Demo exited: $LASTEXITCODE" }
