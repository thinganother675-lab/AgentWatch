# Acceptance record — AgentWatch 0.1.0

Validation date: 2026-09-06. This is a tested first implementation, not a claim of completed multi-day burn-in. Current install state: **service not installed**; bounded validation processes stop after their checks. The development token is not elevated. The shipping EXE, test logs and exact hashes are described in `artifacts/build/publish.json`, `artifacts/runtime-final/validation.json` and PERFORMANCE.md.

## Build and test evidence

`scripts/Build.ps1` successfully performed clean, locked restore, Release build with warnings treated as errors, all tests and self-contained single-file publish. **54 tests passed: 32 unit and 22 integration cases; zero skipped or failed; build has zero warnings/errors.** The test suite includes two data rows for the storage-layout experiment. Production and test outputs are separate.

| Check | Evidence / outcome |
|---|---|
| CPU math | Idle-inclusive kernel subtraction, user/kernel/total, zero/reset counters |
| Process deltas | First observation, PID reuse, disappearing process, counter regression, CPU normalization |
| Tree attribution | Native codex-named fixture with child/grandchild, unrelated helper excluded, verified orphans/cycles covered |
| Process I/O | Own helper writes exactly 16,777,216 file bytes; observed logical WriteTransferCount delta includes them plus only small stdout/runtime traffic |
| Aggregation | Minute boundary splits, duration-weighted averages, min/max, totals and missingness, codec round trip |
| Time gaps | Long delay/backward clock cases; no fake CPU/coverage; missed/error counts retained |
| NVMe parsing | Full UInt128 range, little endian, truncated/invalid descriptor, 512,000-byte unit, independent sensor offsets |
| Golden baseline | 0xAAB764 -> 11,188,068 data units -> 5,728,290,816,000 bytes; 322 K -> 48.85 °C |
| NVMe history | Exact string deltas, counter regression, drive separation, power-hour regression across midnight excluded from day baseline |
| Anomalies | Sustained RAM condition/recovery/gaps; distinct temperature/health events and severities |
| JSON queries | Schema 1, UTC offsets, null missing values, precision-preserving NVMe strings, fractional upper-period boundary |
| SQLite | Atomic/idempotent batches, read-only writer-concurrent query, retained WAL/SHM after close, single writer guard |
| Retention | Both tiers, idempotence, cancellation and injected second-tier failure roll back fine-row deletion; synthetic week keeps all totals |
| WAL backpressure | Stable pinned reader, bounded pause, recovery after reader release |
| Corruption/schema | Corrupt bytes and future schema preserved; explicit failure |
| Installer boundaries | Ordinary file metadata/quoted absolute paths checked; non-elevated install returns 3 before creating installation directories |

Full logs: `artifacts/build/{clean,restore,build,test,publish}.log`; current TRX files are named in the current `test.log`. The directory also retains earlier test runs for comparison; do not sum all historical TRX files as one run.

## Published executable and actual Windows checks

The publish directory contains exactly one EXE, 78,441,617 bytes. The .NET 10 runtime and native SQLite are bundled. A native `sqlite_version()` query in the published binary returns **3.53.3**. Both twelve-minute and final bounded runs remove DOTNET_ROOT from their child environment. Source SDK is 10.0.400, runtime 10.0.11; package locks and SDK archive hash are pinned.

On this machine the native system CPU/memory, process tree/I/O/memory, all four paging counters on Russian Windows, PhysicalDisk(_Total), the four Codex file metadata observations, and the native NVMe SMART request all work under the ordinary token. This is actual execution, not parser-only validation. Current values are timestamped in `artifacts/runtime-final/snapshot.json` and `doctor.json`; PERFORMANCE.md contains the resource measurements.

`scripts/Verify-Runtime.ps1` starts a separate 90-second validation instance, deliberately terminates only that returned process after the initial batch, restarts the same isolated history for 25 seconds, and verifies a restart-gap event and only about 25 seconds of committed coverage. The lost uncommitted observations are not silently fabricated. `quick_check` succeeds after recovery. A separate malformed own database causes startup exit code 1 and remains byte-for-byte unchanged.

Published `report`, `agents`, `disk`, `anomalies`, `self`, `snapshot`, `status`, `doctor` return successfully on the generated history; text output, JSON, and 24 h / 7 d / 30 d periods are exercised. Unsupported configuration/periods and unavailable history are explicit errors. The foreground monitor itself is excluded from agent totals.

## Installation status and remaining environment checks

Non-elevated `install` was executed and rejected with exit code 3. `%ProgramFiles%\AgentWatch` and `%ProgramData%\AgentWatch` were not created by that attempt. No SCM service was registered, and no user agent was stopped. There was no UAC bypass or global Windows configuration change.

The installer implements LocalSystem, delayed auto start, two 60-second recovery restarts then none, owner/ACL protection, user profile/SID capture, protected native extraction and a DB startup health check. For a fatal SCM-hosted exception, the worker uses nonzero process termination; a normal clean StopHost would not itself invoke recovery. Normal shutdown retains final-batch behavior. These behaviors follow the [Microsoft service hosting guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service), but actual SCM execution is **not tested by a foreground run**.

Still requiring an elevated installation test: SCM start/restart/stop/start, queried delayed-start/recovery configuration, LocalSystem driver access, installed ACL enforcement under an ordinary reporting token, native extraction in the service context, Windows reboot and real sleep/resume. Exact commands are in OPERATIONS.md. No installed service is left broken or collecting silently in this session.

## Boundaries of v1

Polling may miss short-lived processes and final I/O; process working sets include shared pages. Presence is not productive time. Process logical I/O, Windows disk throughput and NVMe host traffic cannot be equated. Paging counters cannot isolate pagefile-only writes. Physical-drive replacement with higher counters at the same number is not reliably identifiable. Reports have batch delay and whole-bucket boundaries; old data has coarser resolution. CPU temperature, fan speed, exact NAND wear, individual file I/O, token/cost analytics and MCP are absent. More-than-64-processor-group systems and unsupported storage bridges require separate validation.

Sustained multi-day service overhead and a complete seven-day NVMe baseline remain operational observations to collect after installation. The synthetic storage week and short native runs validate implementation and expose budgets; they do not establish this laptop's long-term SSD-write baseline or the cause of fan activity.
