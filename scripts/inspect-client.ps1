param([Parameter(Mandatory)][string]$Client, [Parameter(Mandatory)][string]$Dumpbin)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$client = (Resolve-Path -LiteralPath $Client).Path
$dumpbin = (Get-Command $Dumpbin -CommandType Application -ErrorAction Stop).Source
if (!(Test-Path $client)) { throw "Client missing: $client" }
$imports = & $dumpbin /imports $client
if ($LASTEXITCODE) { throw 'Import inspection failed' }
$imports | Select-String 'GetAsyncKeyState|GetKeyState|GetKeyboardState|GetCursorPos|SetCursorPos|RawInput|GetMessageW|PeekMessageW|DispatchMessageW|DirectInput|d3d|opengl' |
    ForEach-Object { $_.Line.Trim() } | Set-Content -Encoding utf8 evidence/client-input-imports.txt
& ./out/SandVnc.exe --inspect | Set-Content -Encoding utf8 evidence/client-inventory.json
if ($LASTEXITCODE) { throw 'Window inventory failed' }
Get-FileHash -Algorithm SHA256 -LiteralPath $client | Select-Object Path,Hash |
    ConvertTo-Json | Set-Content -Encoding utf8 evidence/client-hash.json
