# Performance validation — AgentWatch 0.2.0

Measured on 2026-09-07 on Windows 11 x64, 8 logical processors and 8,300,388,352 bytes physical RAM. Codex, Claude and the existing 0.1.0 LocalSystem service were active. The measurement owned a separate foreground 0.2.0 process and isolated database. Existing collectors and their intervals were preserved; no stress generator was used.

## Exact binary and method

Shipping candidate: `artifacts/trial-preparation-20260907-160559/release-03/publish/AgentWatch.exe`, 78,625,937 bytes. SHA-256: `FE24291ADA05FC8CCF42FC62FB6E081BCB133E8F60EC5775611B607A5A9E2666`.

`scripts/Measure-Overhead.ps1 -DurationSeconds 360` measured the owned process externally every five seconds using process CPU time, working set/private bytes and GetProcessIoCounters. CPU is normalized over all eight logical processors. The first 60 seconds were excluded; the steady window between measured samples lasted 295.7550604 seconds. Process exit was 0 at approximately 2026-09-07T14:04:44Z. One normal five-minute persistence batch fell within the window. Small on-demand CLI acceptance operations ran concurrently as natural workstation workload, outside the measured process.

| Metric | Result |
|---|---:|
| Steady CPU, machine-normalized | **0.0277362%** |
| Working set mean / external peak | **58,773,845 / 65,904,640 bytes (56.1 / 62.9 MiB)** |
| Private memory mean / peak | **19,635,473 / 25,620,480 bytes (18.7 / 24.4 MiB)** |
| Steady logical read / write delta | **0 / 53,822 bytes** |
| Steady write extrapolation | **15.72 MB/day**, approximate decimal units |
| Additional shutdown tail | **132,084 logical write bytes** |
| Post-warmup through exit | **185,906 logical write bytes over 299.953 seconds** |
| First externally observed cumulative startup writes | **2,034,098 bytes**, including startup/bundle extraction; not a steady rate |
| Final database / maximum sampled WAL | **81,920 / 107,152 bytes** |
| Fast samples / missed samples | **36 / 0** |
| Collection duration mean / maximum | **10.775 / 24.698 ms** |
| Run outcome / diagnostic write failures | **stopped / 0** |

The stored aggregate's CPU figure includes startup sampling and differs from the external post-warmup figure by design. Its logical I/O sample cannot include later writes from its own final persistence; the external shutdown-tail measurement is therefore reported separately.

## Attribution and scope limits

The foreground process had an ordinary Windows token. The Processes collector repeatedly warned that candidate processes had exited or were inaccessible (roughly 115–117 in logged samples). The report records **36 collector warning/error increments**, one per affected fast sample, and explicitly treats agent attribution as incomplete. Other collectors were available; there were no missed polls or log write failures. These warnings do not invalidate the separately measured CPU/RAM/I/O of the monitor itself, but this run does not prove full installed-service process visibility or its exact overhead under LocalSystem.

This is a short natural-workload observation, not a controlled regression benchmark against 0.1.0 and not a measured daily write volume. The prior version's six-minute observation was about 0.0198% CPU and 54.9 MiB mean working set under a different workload. The small absolute difference does not establish a causal performance regression. Short runs cannot establish leaks, sleep/recovery behavior, week-long stability or total log retention growth.

Logical process I/O is not physical NVMe/NAND writes. The steady projection excludes warmup and shutdown; repeating a short-run shutdown tail every few minutes would be a different operating scenario. Filesystem cache/checkpoints, source imports and report output have their own costs.

## New product layers

Session import and report generation do **not run in the service sampling loop**. There is no watcher, scheduler, network pricing lookup, dashboard or MCP server. `usage-sync` reads on demand, with a default limit of 64 MiB, 100 changed files and approximately 60 seconds per call. Per-line parsing is bounded to 1 MiB and only complete records advance the cursor. The actual two-file acceptance import read 17,058,082 bytes; this establishes format handling, not an import throughput or maximum-memory benchmark.

Per-run manifests and numbered diagnostic files add work at startup, shutdown and health transitions. Rotation preserves old segments. Indefinite retention intentionally requires manual archiving and grows with errors/run count; it is not a fixed total disk-space guarantee.

## Evidence

All paths are below `artifacts/trial-preparation-20260907-160559/performance-final/`: `measurement.json`, `external-samples.json`, `report.json`, `run.stdout.txt`, `run.stderr.txt` and `logs/runs/<run-id>/`. The measurement script uses the exact candidate manifest or explicit `-Executable` and terminates only its own bounded process. Historical 0.1.0 evidence remains in [PERFORMANCE.md](PERFORMANCE.md).
