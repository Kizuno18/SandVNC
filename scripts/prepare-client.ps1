param([Parameter(Mandatory)][string]$Source)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ((Test-Path (Join-Path $root 'evidence/client-security-block.json')) -or
    (Test-Path (Join-Path $root 'evidence/reference/client-security-block.json'))) {
    throw 'A Defender intervention was recorded for the workspace copy. Review that event before preparing another copy.'
}
$source = (Resolve-Path -LiteralPath $Source).Path
$destination = Join-Path $root 'runtime/client'
New-Item -ItemType Directory -Force $destination | Out-Null
foreach ($folder in @('assets', 'win32')) {
    robocopy (Join-Path $source $folder) (Join-Path $destination $folder) /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) { throw "Client copy failed: $folder" }
}
$original = (Get-FileHash -LiteralPath (Join-Path $source 'win32/pxgme32.exe')).Hash
$copied = (Get-FileHash -LiteralPath (Join-Path $destination 'win32/pxgme32.exe')).Hash
if ($original -ne $copied) { throw 'Client executable hash mismatch' }
[pscustomobject]@{ destination = $destination; executableSha256 = $copied; personalDataCopied = $false } | ConvertTo-Json
