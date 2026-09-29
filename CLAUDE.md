# Project commands

- Build: `./scripts/build.ps1`
- Run diagnostic viewers: `./scripts/start-demo.ps1`
- Stop owned demo processes: `./scripts/stop-demo.ps1`
- Native integration tests and diagnostic measurements: `./scripts/test.ps1`
- Read-only real-client inventory: `./scripts/inspect-client.ps1 -Client <exe> -Dumpbin <dumpbin.exe>`
- Diff/whitespace check: `git diff --check`

Write only inside this repository unless the current user explicitly authorizes an outside change. Sandboxie provisioning changes installed configuration and needs separate authorization; old experiment approvals do not apply to a contributor's machine. Preserve existing boxes and global settings. Production remote access is not implemented. The experimental client diagnostic switches are not real-client acceptance. No Docker files exist because this prototype uses native Windows process and window APIs. Keep runtime files and raw evidence private; publish only reviewed, sanitized files under `evidence/reference`.
