param([switch]$Apply, [switch]$Verify, [string]$Backup)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$configPath = 'C:\Program Files\Sandboxie-Plus\Sandboxie.ini'
$proposal = [IO.File]::ReadAllText((Join-Path $root 'config/sandboxie-boxes.proposed.ini')).Replace('@PROJECT_ROOT@', $root)
$expected = foreach ($pair in @(@('SandVnc_A', 'A'), @('SandVnc_B', 'B'))) {
    "[$($pair[0])]`nEnabled=y`nFileRootPath=$root\runtime\boxes\$($pair[1])`nAutoRecover=n`nAutoDelete=n"
}
if ($proposal.Replace("`r`n", "`n").Trim() -cne ($expected -join "`n`n")) { throw 'Proposal differs from the approved sections' }
if ($Apply -and $Verify) { throw 'Choose Apply or Verify' }
if (!$Apply -and !$Verify) { Write-Output $proposal; return }
$env:TEMP = Join-Path $root '.cache'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP,(Join-Path $root 'runtime') | Out-Null
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Text;
public static class SandboxProvisioning {
    [DllImport(@"C:\Program Files\Sandboxie-Plus\SbieDll.dll")]
    public static extern uint SbieApi_ReloadConf(uint session, uint flags);
    [DllImport(@"C:\Program Files\Sandboxie-Plus\SbieDll.dll", CharSet = CharSet.Unicode)]
    public static extern uint SbieApi_IsBoxEnabled(string box);
    [DllImport(@"C:\Program Files\Sandboxie-Plus\SbieDll.dll", CharSet = CharSet.Unicode)]
    public static extern uint SbieApi_QueryConf(string box, string setting, uint index, StringBuilder value, uint bytes);
}
'@
if ($Apply) {
$stream = [IO.File]::Open($configPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
try {
    $original = [byte[]]::new($stream.Length)
    $stream.ReadExactly($original)
    if ($original[0] -ne 255 -or $original[1] -ne 254) { throw 'Expected installed UTF-16LE configuration; refusing an encoding conversion' }
    $encoding = [Text.UnicodeEncoding]::new($false, $true, $true)
    $text = $encoding.GetString($original)
    foreach ($name in @('SandVnc_A', 'SandVnc_B')) {
        if ($text -match ('(?im)^\[' + [regex]::Escape($name) + '\]\s*$')) { throw "Section already exists: $name" }
        if ([SandboxProvisioning]::SbieApi_IsBoxEnabled($name) -ne 0xC0000034u) { throw "Driver does not confirm absent box: $name" }
    }
    $backup = Join-Path $root ('runtime/Sandboxie.before-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffff') + '.ini')
    [IO.File]::WriteAllBytes($backup, $original)
    $suffix = $encoding.GetBytes("`r`n`r`n" + $proposal.Replace("`r`n", "`n").Trim().Replace("`n", "`r`n") + "`r`n")
    try { $stream.Write($suffix); $stream.Flush($true) }
    catch { $stream.SetLength($original.Length); $stream.Flush($true); throw }
    $stream.Position = 0
    $written = [byte[]]::new($stream.Length)
    $stream.ReadExactly($written)
    if ([Convert]::ToHexString($written) -cne ([Convert]::ToHexString($original) + [Convert]::ToHexString($suffix))) {
        throw 'Unexpected configuration contents after append'
    }
} finally { $stream.Dispose() }
} else {
    if (!$Backup) { throw 'Verification requires the original backup path' }
    $backup = [IO.Path]::GetFullPath($Backup)
    if (!$backup.StartsWith(($root + '\runtime\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must be inside the project runtime directory' }
    $original = [IO.File]::ReadAllBytes($backup)
}
$reload = [SandboxProvisioning]::SbieApi_ReloadConf([uint32][Diagnostics.Process]::GetCurrentProcess().SessionId, 0)
$boxes = foreach ($pair in @(@('SandVnc_A', 'A'), @('SandVnc_B', 'B'))) {
    $values = @{}
    foreach ($setting in @('FileRootPath', 'AutoRecover', 'AutoDelete')) {
        $buffer = [Text.StringBuilder]::new(1024)
        $status = [SandboxProvisioning]::SbieApi_QueryConf($pair[0], $setting, 0x20000000, $buffer, 2048)
        if ($status -ne 0) { throw "Driver query failed for $($pair[0])/$setting`: $status" }
        $values[$setting] = $buffer.ToString()
    }
    $enabled = [SandboxProvisioning]::SbieApi_IsBoxEnabled($pair[0])
    if ($enabled -ne 0 -or $values.FileRootPath -ine "$root\runtime\boxes\$($pair[1])" -or
        $values.AutoRecover -ne 'n' -or $values.AutoDelete -ne 'n') { throw 'Effective driver configuration differs from the proposal' }
    [pscustomobject]@{ name = $pair[0]; enabledStatus = ('0x{0:X8}' -f $enabled); settings = $values }
}
$current = [IO.File]::ReadAllText($configPath)
$preserved = $current.StartsWith([Text.Encoding]::Unicode.GetString($original).TrimStart([char]0xFEFF).TrimEnd(), [StringComparison]::Ordinal)
if (!$preserved) { throw 'Existing configuration sections differ from the original backup' }
$receipt = [pscustomobject]@{ timestamp = [DateTime]::UtcNow.ToString('o'); configPath = $configPath; backup = $backup;
    originalBytes = $original.Length; existingSectionsPreserved = $preserved;
    managerInitialization = 'SandMan automatically added its ConfigLevel=10 defaults to both new sections';
    originalSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($original));
    installedSha256 = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash;
    reloadStatus = ('0x{0:X8}' -f $reload); boxes = @($boxes); processesLaunched = 0; serviceRestarted = $false }
$result = $receipt | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $root 'evidence/provisioning.json'), $result + "`n", [Text.UTF8Encoding]::new($false))
Write-Output $result
if ($reload -ne 0) { throw 'Configuration was appended but reload failed; see provisioning receipt' }
