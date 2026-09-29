# Experimental results and remaining acceptance

This report preserves the September 9, 2026 development results and incorporates the later artifacts found during public-release preparation on September 29. **Two fully usable remote application sessions have not been demonstrated.** The public repository contains original source, tests, and selected sanitized evidence, rather than a ready-to-deploy remote-access product.

## What the evidence establishes

The [synthetic regression record](evidence/reference/concurrent-results.json) contains 13 passing checks against real Win32 probe processes. Coverage includes independent W holds, 200 rapid key transitions, modifiers and mouse input, controller takeover, disconnect, heartbeat expiry, stale sequences, delayed capture, message filtering, resizing, and window replacement. These tests use Node HTTP/WebSocket clients; they do not establish browser usability or physical-input isolation.

[Frame-order tests](evidence/reference/frame-order.json) reproduced a pre-input PNG after a fixed 250 ms wait, using an intentional 650 ms publication delay. Waiting for a capture associated with an acknowledged input sequence supported 30 repeated independent-hold cycles. Frame metadata records what the adapter acknowledged before capture; it does not prove an arbitrary application processed or displayed the input.

Earlier synthetic captures using `PW_CLIENTONLY | PW_RENDERFULLCONTENT` sometimes omitted diagnostic text. [The original failure](evidence/reference/capture-partial-before/frame-order.json) and [the unsuccessful GDI-flush attempt](evidence/reference/capture-flush-attempt/frame-order.json) are preserved with probe images. Synthetic capture was changed to `PW_CLIENTONLY`; subsequent regression passed. This identifies the experimentally failing capture path, not a proven lower-level compositor cause.

[Boxed-probe transport](evidence/reference/box-transport.json) passed independent key-state checks in `SandVnc_A` and `SandVnc_B`. The broker derives pipe namespaces from Sandboxie's `SbieApi_QueryBoxPath`, then checks the connecting PID's box, executable, and window. No global IPC exception was needed for that test.

## Real-client experiment: preserve the whole sequence

1. A window-only PXG `PrintWindow` snapshot produced 1,367 × 768 pixels, 772,062 encoded bytes, and 95.6034 ms capture-plus-encoding time. The [measurement](evidence/reference/pxg-capture.json) is retained; the game screenshot is excluded from the public repository. One snapshot does not demonstrate streaming or input.
2. An initial experiment copied the client's `assets` and `win32` directories, then used the box-restricted launcher to load the adapter into two child processes. Handshake validation failed. Defender recorded `Behavior:Win32/DefenseEvasion.A!ml` and removed the workspace executable. The [intervention record](evidence/reference/client-security-block.json) describes that point in time.
3. A later local recovery log records restoration of the copied executable with elevation. This happened after the intervention record was written; therefore its `clientCopyExists: false` field and statement that no restoration had occurred are historical observations, not the final state.
4. A retained [failed handshake](evidence/reference/client-transport-failure.json) reports native status 4 and no bound window. A separate, later [transport result](evidence/reference/client-transport.json) reports status 0, a held key in both processes, release in A, and the key still held in B.
5. The successful transport record has `applied: 0` throughout. It proves adapter state separation in that run, **not application message consumption, movement, menu operation, or gameplay**. No full real-client acceptance record was found.

The old README and report stopped at the earlier quarantine event. Public-release documentation corrects that incomplete account. No client experiment, recovery, antivirus-setting change, or exclusion was performed as part of publication. The preparation script still checks the retained intervention record and refuses to prepare another copy automatically.

The detection label alone does not establish that the original client is malicious. Likewise, a later successful transport result does not establish that the loading method is safe or generally compatible.

## Measurements and their limits

Historical [idle](evidence/reference/resources-idle.json) and [reader](evidence/reference/resources-readers.json) samples measured the same two synthetic processes over about five seconds. CPU percentages refer to one logical core. The broker used 11.53% of one core and 54.31 MiB working set without readers, versus 34.60% and 76.07 MiB with two HTTP readers.

[Combined frame traffic](evidence/reference/bandwidth.json) was about 0.619 Mbit/s. Ten [input-to-changed-PNG observations](evidence/reference/visual-timing.json) ranged from 45.96 to 101.63 ms. These values exclude browser presentation and WAN latency. Per-process GPU samples were unavailable, so null values must not be read as zero. The older `resources.json` sample predates demand-based capture and is not the final idle baseline.

The original environment recorded Windows 11 Pro 10.0.26200, Ryzen 7 2700X, Radeon RX 580 2048SP, MSVC 19.44, Windows SDK 10.0.26100.0, .NET SDK 10.0.400, and a VS 2022 Win32 build with pinned MinHook v1.3.4. These historical versions are context, not a statement of the latest supported releases.

## Open acceptance criteria

- Validate real application behavior, auxiliary windows, Raw Input/SDL compatibility, native controls, IME, wheel/touch, and multiple input threads.
- Test the effective Sandboxie filesystem policy through open/save/export dialogs, URI handlers, and child processes. Copy-on-write does not prevent host-file reads.
- Verify physical administrator input does not affect controlled targets or vice versa.
- Exercise actual target stalls, lifecycle failures before a valid handshake, reconnect races, slow consumers, and network congestion. The current capture-delay test is not a hung-game test.
- Measure browser decoding/presentation and actual input-to-photon latency against a real-client baseline.
- Design and validate remote authentication, transport security, and deployment before supporting anything beyond loopback.
- Audit clipboard paths and hostile local-process behavior beyond the limited hooked APIs.

The [manual acceptance plan](tests/manual.md) spells out the required scenarios. The local diagnostic switches do not enable a supported production mode.

## Publication review

The September 29 publication check exported only the staged source into a fresh directory, downloaded the pinned MinHook dependency, built the native targets and broker, and ran `scripts/test.ps1` to completion with exit code 0. The broker build reported zero warnings and zero errors. All 13 concurrency checks, 30 synchronized hold cycles, and 10 input-to-changed-PNG trials passed. The [fresh regression record](evidence/reference/publication-2026-09-29/concurrent-results.json), [capture-order record](evidence/reference/publication-2026-09-29/frame-order.json), and [timing samples](evidence/reference/publication-2026-09-29/visual-timing.json) are separate from the historical records. PowerShell parsing, configuration-template preview, and the client-preparation guard also passed. These checks did not run the real client or modify Sandboxie settings.

The release preparation makes personal client/tool paths explicit parameters, renders Sandboxie test paths relative to the checkout, and preserves dependency notices. Runtime tokens, raw logs, installed configuration backups, registry hives, copied executables, proprietary client assets, and real-game screenshots are excluded. See [PUBLICATION.md](PUBLICATION.md) for the file-size and ignored-file review.

Primary API references: [PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow), [GdiFlush](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-gdiflush), and [GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate).
