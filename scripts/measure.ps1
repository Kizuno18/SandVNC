param([int]$Seconds = 5, [ValidateSet('idle', 'readers')][string]$Mode = 'idle')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$runtime = Get-Content -Raw runtime/viewers.json | ConvertFrom-Json
$processIds = @($runtime.brokerPid) + @($runtime.viewers.pid)
if ($Mode -eq 'readers') {
    $node = (Get-Command node).Source
    $reader = Start-Process -FilePath $node -ArgumentList @('tests/frame-readers.mjs', ($Seconds + 3)) -WorkingDirectory $root -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 1
}
function Snapshot {
    @($processIds | ForEach-Object {
        $process = Get-Process -Id $_
        [pscustomobject]@{ pid = $process.Id; name = $process.ProcessName; cpuSeconds = $process.CPU; workingSetBytes = $process.WorkingSet64; privateBytes = $process.PrivateMemorySize64 }
    })
}
$before = Snapshot
$timer = [Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds $Seconds
$elapsed = $timer.Elapsed.TotalSeconds
$after = Snapshot
$samples = for ($i = 0; $i -lt $before.Count; $i++) {
    [pscustomobject]@{ pid = $after[$i].pid; name = $after[$i].name; oneCoreCpuPercent = 100 * ($after[$i].cpuSeconds - $before[$i].cpuSeconds) / $elapsed;
        workingSetBytes = $after[$i].workingSetBytes; privateBytes = $after[$i].privateBytes }
}
$gpu = $null
$gpuErrors = @()
try {
    $gpu = (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction Stop).CounterSamples |
        Where-Object { $instanceName = $_.InstanceName; @($processIds | Where-Object { $instanceName -match "pid_$($_)_" }).Count -gt 0 } |
        Select-Object InstanceName,CookedValue
} catch { $gpuErrors += $_.Exception.Message }
if (!$gpu) {
    try {
        $gpu = Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction Stop |
            Where-Object { $instanceName = $_.Name; @($processIds | Where-Object { $instanceName -match "pid_$($_)_" }).Count -gt 0 } |
            Select-Object Name,UtilizationPercentage
    } catch { $gpuErrors += $_.Exception.Message }
}
$metrics = @($runtime.viewers | ForEach-Object {
    Invoke-RestMethod ($runtime.url + '/status') -Headers @{ Authorization = 'Bearer ' + $_.token }
})
[pscustomobject]@{ timestamp = [DateTime]::UtcNow.ToString('o'); mode = "Synthetic $Mode; no browser; not PXG acceptance";
    elapsedSeconds = $elapsed; processes = $samples; gpu = $gpu; gpuErrors = $gpuErrors; broker = $metrics;
    clientBaseline = $null; browserCpu = $null; inputToPhotonMs = $null } |
    ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 "evidence/resources-$Mode.json"
if ($Mode -eq 'readers') { $reader.WaitForExit(); if ($reader.ExitCode) { throw 'Frame readers failed' } }
