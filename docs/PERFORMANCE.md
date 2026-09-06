# Performance validation — 2026-09-06

Windows 11 x64 build 26200, 8 logical processors, physical RAM reported by Windows 8,300,388,352 bytes. Ordinary user token, Russian Windows counters. Codex was present; Claude was absent. The measured program is self-contained, using the same Generic Host and monitoring loop as the service. Actual SCM/LocalSystem operation is still an elevated validation step.

## Shipping executable: final six-minute confirmation

The final EXE is 78,441,617 bytes, SHA-256 `B371A3A696FB9908158456B8C672F490F08144A61EF44C4AAEDC02F16310F3E2`. The run completed at **2026-09-06 18:14:50 UTC** with exit code 0. After a 60-second warmup, 295.6669847 seconds were measured externally; one normal five-minute persistence batch fell in that window.

| Metric | Final shipping EXE |
|---|---:|
| CPU, normalized across 8 logical processors | **0.0198175%** |
| Working set mean / external peak | **57,598,430 / 60,739,584 bytes (54.9 / 57.9 MiB)** |
| Private memory mean / peak | **19,638,272 / 21,381,120 bytes (18.7 / 20.4 MiB)** |
| Logical read / write delta after warmup | **0 / 37,080 bytes** |
| Steady-window write extrapolation | **10.84 MB/day** (approximate, decimal) |
| Additional shutdown tail | **94,600 logical write bytes** |
| Fast samples / collector errors / missed samples | **36 / 0 / 0** |
| Collection mean / maximum | **8.486 / 11.856 ms** |
| Collection P50 / P95 upper bounds | **<=10 / <=11.856 ms** |
| Largest recorded batch flush | **11.208 ms** |
| WAL peak | **90,672 bytes** |
| Main DB after normal stop / final WAL | **53,248 bytes (52 KiB) / 0 bytes** |

Evidence: `artifacts/performance-final/measurement.json`, `external-samples.json`, `report.json`. Native exit counters confirm 131,680 post-warmup logical write bytes through process exit, including the separately reported final batch/checkpoint tail. The monitor's own ten-second sampling saw a 61,956,096-byte peak working set; this may exceed the five-second observer's peak because their sample times differ. Across the longer and final selected-storage runs, the largest observed working set was 70,025,216 bytes (66.8 MiB).

## Twelve-minute run of the selected storage implementation

After choosing the lower-write compression, a 720-second run used the default 10 s / 60 s / 15 min / 5 min cadence. The first minute was excluded from the external steady measurement. The later final patch affects fatal service exit signaling and diagnostics, not the sampling or storage cadence; the shipping EXE was then measured again above.

| Metric | Measured result |
|---|---:|
| External steady duration | 656.5068691 s |
| CPU normalized across 8 processors | 0.0211227% |
| Working set mean / peak | 60,971,784 / 70,025,216 bytes (58.1 / 66.8 MiB) |
| Private memory mean / peak | 22,492,532 / 30,011,392 bytes (21.5 / 28.6 MiB) |
| Logical read / write delta | 0 / 86,520 bytes |
| Steady-window write extrapolation | 11.39 MB/day, decimal |
| Additional shutdown/final-batch/checkpoint tail | 102,792 logical write bytes |
| Fast observations / collector errors / missed samples | 72 / 0 / 0 |
| Collection mean / maximum | 8.317 / 13.256 ms |
| Collection P50 / P95 upper bounds | <=10 / <=13.256 ms |
| Largest recorded flush | 17.494 ms |
| WAL peak while running | 140,112 bytes |
| Database after normal stop | 61,440 bytes; WAL 0, SHM retained |

Evidence: `artifacts/performance-optimized/measurement.json`, `external-samples.json`, `report.json`. Binary SHA-256 for that run: `12BDF4606CBB363A7E331F4EDA17C487D4A27E97B63D1127434EE7A09A55FD01`. This is separate from the final shipping hash recorded above and in `artifacts/build/publish.json`.

## Method and limits of the extrapolation

`scripts/Measure-Overhead.ps1` starts only its own bounded AgentWatch process without DOTNET_ROOT, uses GetProcessIoCounters and native-backed process CPU/memory information every five seconds, keeps measurements in RAM and writes one result after completion. CPU is `delta process CPU seconds / elapsed seconds / logical processors × 100`. There is no stress generator, forced GC, working-set trimming, Windows optimization or termination of the user's agents. The external observer is a separate development process; its own writes are not charged to AgentWatch.

The steady write extrapolation includes the five-minute batch writes falling within the measured window. A twelve-minute run does not reach the normal 256-page auto-checkpoint threshold on this dataset. It is **not** proof of a complete-day budget. Startup/extraction and the final shutdown flush/checkpoint are separate; the exit counters retain the latter explicitly. Charging a complete shutdown every twelve minutes would describe repeatedly restarted validation instances, rather than an always-running service. File growth and GetProcessIoCounters are not physical NVMe/NAND write measurements.

The product's own report includes the first valid post-start samples and has slightly different endpoints from the five-second external observer. Its whole-run CPU/write projection therefore differs from the external post-warmup projection. Collection quantiles are histogram upper bounds; the maximum is directly observed. The final shutdown flush cannot report its own completion latency back into the already closed history. Histories are read only after the writer has stopped for the final report.

Scenario with all AI agents absent was not isolated because Codex was in use. No agent was killed to manufacture it. Long idle/active service runs, repeated sleep/reboot cycles, sustained heap behavior and driver hangs require later burn-in. These short runs stay well below the 0.3% CPU target and the 100 MB memory investigation threshold, with mean working set near the requested range. Occasional managed/native allocation peaks are reported, not hidden by forced memory trimming.

## Checkpoint-aware storage experiment and selected change

The accelerated fixture generates a deterministic synthetic week: 60,480 ten-second observations, 10,080 minute frames plus four hot-file rows per minute, 50,400 rows total. It persists every five synthetic minutes using the production writer and measures test-process logical writes through native counters. Periodic SQLite checkpoints are included. Native collector polling, real elapsed service time and changing production capability metadata are not simulated; this is a storage-layout comparison, not a replacement for the real-process run.

| Same synthetic input | DB after week | Logical writes per synthetic day, including periodic checkpoints |
|---|---:|---:|
| Brotli Fastest, WITHOUT ROWID | 57.05 MB | 39.52 MB |
| Brotli Fastest, ordinary rowid | 51.32 MB | 35.57 MB |
| **Brotli Optimal, WITHOUT ROWID (selected)** | **about 14.56 MB** | **about 15.39 MB** |
| Brotli Optimal, ordinary rowid | about 15.8 MB | about 15.5 MB |

The less dense payloads made the original choice too expensive. Ordinary rows alone saved only about 10%. Brotli Optimal reduced enough payload to make the existing WITHOUT ROWID layout the smaller choice. Compression runs only in batch/retention paths. The codec and SQLite schema remain compatible with earlier fixtures; all values, minute resolution and counters are retained. The faster total fixture runtime after compression primarily reflects fewer pages to write, not an increased sample interval. This result also matches SQLite's advice to [measure WITHOUT ROWID with the actual row sizes](https://www.sqlite.org/withoutrowid.html).

Current evidence: `artifacts/tests/history-volume-without-rowid.json`, `history-volume-rowid.json`; original comparisons: `layout-fastest-without-rowid.json`, `layout-fastest-rowid.json`. Small differences between repeat runs come from receipt IDs/page placement and test-host I/O. The synthetic fixture is not a full service write-budget guarantee: real metadata, anomalies, retention and workload variability also contribute. Both experiments show why the first short-window projection alone was insufficient.

## Database growth and WAL behavior

At the selected layout the synthetic minute tier grows about 2.08 MB per synthetic day, or roughly 29 MB for fourteen days. That estimate covers only the minute portion of this specific fixture; coarser history, indexes, free-page reuse, metadata and anomalies affect the total. It must not be extrapolated linearly forever because old minutes are downsampled.

After the synthetic week is aged past 180 days, retention leaves 35 daily rows and preserves 604,800 observed seconds. The physical DB remains around 14.6 MB because there is no VACUUM; freed pages remain available for reuse. This was deliberately checked, not mistaken for failed retention. Daily anomaly history is sparse and retained indefinitely; the configured own-DB warning is 256 MiB.

The synthetic normal WAL peak is approximately 1.1 MB. A separate pinned-reader test uses a deliberately small 128 KiB watermark, confirms writes pause before 256 KiB, holds a stable read snapshot, then releases it and verifies persistence resumes. Production uses a 64 MiB watermark. The last in-flight transaction can overshoot that watermark; automatic checkpoint and journal_size_limit alone do not cap a reader-pinned WAL. No multi-GB write test or disk-filling test was performed.

## Repeat the measurement

```powershell
.\scripts\Measure-Overhead.ps1 -DurationSeconds 720 -OutputDirectory .\artifacts\another-measurement
.\artifacts\publish\AgentWatch.exe self --since 24h
```

Use a new output directory. After elevated installation, compare a full day of the always-running service, including natural auto-checkpoints and a daily retention cycle. Inspect both logical writes and DB/WAL sizes, and keep the process/physical/NVMe measurement layers distinct. No deferred job or monitoring automation was installed by this development task.
