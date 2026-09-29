$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\') + '\'
$runtimePath = Join-Path $root 'runtime/viewers.json'
if (!(Test-Path $runtimePath)) { return }
$runtime = Get-Content -Raw $runtimePath | ConvertFrom-Json
foreach ($processId in @($runtime.brokerPid) + @($runtime.viewers.pid)) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (!$process) { continue }
    $resolved = [IO.Path]::GetFullPath($process.Path)
    if (!$resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to stop an outside process: $processId" }
    if ($process.ProcessName -notin @('SandVnc', 'input-probe', 'pxgme32')) { throw 'Unexpected process name' }
    if ($process.ProcessName -eq 'pxgme32' -and $resolved -ine (Join-Path $root 'runtime/client/win32/pxgme32.exe')) { throw 'Unexpected client path' }
    Stop-Process -Id $process.Id
}
