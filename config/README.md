# Optional Sandboxie test boxes

The ordinary demo and test suite do not require Sandboxie. This configuration is only for the experimental boxed transport checks.

`sandboxie-boxes.proposed.ini` is a template. `scripts/provision-boxes.ps1` replaces `@PROJECT_ROOT@` with the current checkout's absolute path, then checks the resulting settings against its expected two-box configuration. Do not copy the literal template into your installed configuration.

Preview without changing Sandboxie:

```powershell
pwsh -NoProfile -File ./scripts/provision-boxes.ps1
```

After reviewing the preview, an elevated PowerShell can append the two sections:

```powershell
pwsh -NoProfile -File ./scripts/provision-boxes.ps1 -Apply
```

This modifies `C:\Program Files\Sandboxie-Plus\Sandboxie.ini` and asks the installed driver to reload configuration. It requires the default installation path and UTF-16LE configuration encoding, refuses existing `SandVnc_A` or `SandVnc_B` names, backs up the original under `runtime/`, and checks preservation of the existing sections. SandMan may add initialization defaults to the new sections. It does not restart a service or launch a client.

Both file roots point under the current checkout's `runtime/boxes/`. Automatic recovery and deletion are disabled. These minimal sections do not establish a host-file isolation policy. Review effective rules, inherited templates, IPC, and child-process behavior before any real-client work.

To reload and verify against the original backup:

```powershell
./scripts/provision-boxes.ps1 -Verify -Backup 'C:\path\to\SandVNC\runtime\Sandboxie.before-TIMESTAMP.ini'
```

Verification reloads configuration and compares effective driver settings; it is not a read-only driver operation. Keep that backup private. Do not commit the installed configuration or sandbox registry hives.

Before moving or deleting a checkout used for these boxes, stop its test processes and remove or update only its two test definitions through Sandboxie. Existing unrelated boxes and global settings must be preserved. Historical provisioning in the original development workspace does not provision a contributor's machine.
