# Public repository scope

The September 29, 2026 inventory measured file contents before publication preparation. Sizes below are MiB (1,048,576 bytes), rounded; they are not filesystem allocation sizes.

| Local area | Files | MiB | Publication decision |
| --- | ---: | ---: | --- |
| `runtime/` | 8,751 | 2,304.80 | Exclude copied clients/assets, sandbox state, session tokens, and installed configuration backup. |
| `build/` | 154 | 1.30 | Exclude generated CMake/MSVC outputs; rebuild from source. |
| `.git/` | 161 | 0.94 | Local Git metadata; not a source artifact. No earlier commit history existed. |
| `evidence/` | 69 | 0.89 | Keep selected sanitized measurements and probe images in `reference/`; exclude raw/private outputs. |
| `src/` | 32 | 0.40 | Keep authored source; exclude generated `bin/` and `obj/`. |
| `third_party/` | 82 | 0.37 | Re-fetch MinHook at its pinned commit; retain its license notices. |
| `out/` | 7 | 0.27 | Exclude generated executable and runtime files. |
| `.cache/` | 31 | 0.10 | Exclude SDK/cache/download metadata and local logs. |

The largest individual file was a third-party sprite asset, about 31.52 MiB. Size was not the reason to publish or exclude it: client redistribution rights and scope were. Git LFS is unnecessary for the retained source and small diagnostic artifacts.

## Ignored-file review

- `runtime/client/` held about 2,281.44 MiB of copied third-party client content. `runtime/recovered-client/` held another executable of about 20.74 MiB. Neither is project source.
- `runtime/boxes/` held about 2.61 MiB of registry hives, graphics caches, settings, logs, and client-module experiment state. It is machine/application data, not a reusable contribution.
- `runtime/viewers.json` contains bearer credentials. The installed Sandboxie backup can contain unrelated private configuration. Both remain private.
- `third_party/minhook/` is reproducible from the build's pinned upstream commit. The retained notice matches its license text after line-ending normalization.
- Build outputs, SDK caches, and generated C# files add no unique implementation needed by a clean checkout. No authored project source was found that needed to be recovered from these ignored directories.
- Several raw evidence files were already present in the Git index. Adding ignore rules alone would not remove them, so they were explicitly removed from the index and replaced with selected sanitized reference artifacts.

Personal paths in retained evidence are redacted without changing outcomes. The original intervention, later transport success, failed capture experiments, and limitations remain documented. `.gitignore` also excludes environment secrets and common private-key/certificate files.

## Review boundaries

The review covers publication contents, dependency provenance, documented behavior, broker token/origin checks, selected-window capture, per-process input, and known isolation gaps. It is not a completed product-security audit. Full real-client and browser acceptance remains open in `tests/manual.md`.

The public branch is `main`, under `Kizuno18/SandVNC`. Original project code uses MIT; third-party notices remain separate. The repository intentionally contains no installed Sandboxie configuration, game distribution, runtime credentials, or generated binaries.
