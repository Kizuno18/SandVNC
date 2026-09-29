# Evidence and generated outputs

`reference/` contains selected development artifacts retained for public review. These are historical observations, not a claim that every experiment passes today. Personal absolute paths are replaced with `<workspace>` or `<client-install>`; statuses, measurements, timestamps, hashes, and failures are retained. Synthetic PNGs show only purpose-built probes.

The September 9 records include successful synthetic tests, boxed-probe transport, a later real-client transport-only result, and earlier failures. The Defender record describes the earlier intervention; a subsequent recovery was recorded in a local log, as explained in `REPORT.md`.

`reference/publication-2026-09-29/` contains a fresh synthetic regression run built from an export of the public source without existing build outputs, dependency downloads, or runtime data. It is separate from the historical records.

Builds and tests write fresh results directly under `evidence/`. Those files are ignored to prevent accidental publication of machine paths, logs, real-application screenshots, or credentials. Promote a reviewed result into `reference/` deliberately rather than force-adding an entire run.

Excluded from publication: raw build/recovery logs, client/window inventories, real-game screenshots, installed configuration backups, empty client-module experiment dumps, and all runtime data. The preparation script checks both the original local intervention record and its retained public reference so cloning the repository does not silently remove that guard.
