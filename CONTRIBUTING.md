# Contributing to SandVNC

Start with one observable behavior. A small reproduction that tells us why an application fails is as valuable as a patch that makes it work.

1. Follow the README's Windows quick start and run `pwsh -NoProfile -File ./scripts/test.ps1`.
2. Create a branch for your change. Describe the problem and expected behavior in an issue or pull request.
3. Keep the change focused. Match the surrounding C++, C#, JavaScript, and PowerShell conventions; use English for source and documentation.
4. Test the affected behavior. Input, capture, authorization, and process-lifecycle changes need a regression case that would fail without the fix. Documentation changes need accurate commands and working links.
5. Run `git diff --check` and inspect the complete diff before submitting.

For a bug report, include Windows and tool versions, the exact command, expected versus actual results, and whether the target is a synthetic probe or a real application. Include relevant sanitized evidence, even when the experiment failed. Do not call adapter acknowledgment latency input-to-photon latency.

Generated results stay under ignored `evidence/` paths. Copy only reviewed samples into `evidence/reference/` and document their provenance. Never include viewer URLs/tokens, credentials, private paths, complete Sandboxie configuration, registry hives, client executables, proprietary assets, or screenshots of personal data.

Changes to Sandboxie settings require the machine owner's approval. Do not broaden IPC/filesystem permissions, disable security controls, or add a desktop-capture/global-input fallback just to pass a test. Preserve existing boxes and unrelated running applications.

If you find a security problem, provide the smallest sanitized reproduction. Do not post working session credentials or private files in a public issue. The current prototype is local-only and has not completed a full security audit.

Original contributions are submitted under the repository's MIT License. Keep third-party notices intact and identify any new dependency and its license in your pull request.
