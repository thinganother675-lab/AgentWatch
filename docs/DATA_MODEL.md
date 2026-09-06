# Data model and measurement semantics

Database schema 1, report schema 1, aggregate codec 1. UTC timestamps use integer Unix seconds for indexed buckets and ISO 8601 offsets inside structured payloads. Durations are monotonic seconds. All byte metrics use bytes internally; human-readable output uses explicit decimal GB/TB or binary MiB as labeled.

## Tables

| Table | Key | Content |
|---|---|---|
| history | resolution_s, bucket_utc, kind, item | Compressed aggregate, hot-file window, or NVMe window |
| meta | key | Version, capability health, last snapshot, instance timestamps and last retention |
| anomaly | id | First/last confirmed times, severity, type, evidence, open state |
| batch_receipt | id | Commit receipt prevents a retried batch from adding its totals twice |

`history` and `meta` use WITHOUT ROWID. History resolutions are 60, 900 and 86400 seconds. Kinds: 0 = all fast metrics in one frame (`item` empty); 1 = one of four hot-file basenames; 2 = physical drive number. A metric is not a separate database row. Aggregate payloads use fixed, append-only numeric metric IDs and Brotli Optimal (batch path only). NVMe/hot-file windows use bounded compressed JSON. There is no raw ten-second sample table, command-line table, conversation table, or process-event trace.

## Fast metrics

Each available gauge keeps `sum(value × valid seconds)`, valid seconds, minimum and maximum. A missing metric contributes no weight. A rate's integrated sum is its byte/operation/time total. Starts, exits, missed samples and errors remain additive counts. Splitting an observation across minute boundaries splits its duration and totals proportionally. Whole-frame rollups merge these accumulators, avoiding averages of averages.

- System CPU = `(delta kernel + delta user - delta idle) / (delta kernel + delta user)`. Windows kernel time includes idle. User and busy kernel components sum to total.
- Process CPU uses kernel+user delta / monotonic elapsed / logical processor count. Process identity is PID+creation FILETIME. First observations are baselines, never lifetime counter additions. Counter regression invalidates the corresponding delta.
- Agent logical I/O integrates read/write/other transfer bytes and operations. Roots are case-insensitive `codex.exe`, `claude.exe`, `codex-code-mode-host.exe`. Descendants require observed parentage and valid creation ordering; an already verified living identity may remain attributed after its parent exits. Arbitrary `node.exe` and ambiguous orphan identities are excluded. AgentWatch itself is excluded from agent totals, including foreground validation launched by Codex.
- Agent presence means at least one observed process. Starts/exits mean observed appearances/disappearances, including access-related disappearance. Summed working sets may double-count shared pages; private bytes and working set are distinct gauges.
- Available RAM, total physical RAM, memory load, commit bytes/limit/percentage come from native memory APIs. Page size comes from GetPerformanceInfo.
- Pages Input/Output are pages per second. Page Reads/Writes are operations per second. Estimated page bytes multiply the page count by this machine's page size. They include disk-backed paging beyond the pagefile and cannot prove pagefile-only traffic.
- PDH PhysicalDisk(_Total) read/write bytes, queue length and idle percentage cover all physical disks. NVMe counters cover only the configured drive.
- CPU P95 is a weighted 5-percentage-point histogram upper bound. Collection P50/P95 are sample-weighted histogram upper bounds, clamped by the actual maximum. They are intentionally not labeled exact quantiles.

## File metadata and NVMe

File windows retain first/last/min/max size, first/last observation, positive growth, count/missing/error count. Shrinkage is allowed. Positive growth is a lower-bound metadata observation, not bytes written. Files checked: `.codex/logs_2.sqlite`, its `-wal`, `.codex/state_5.sqlite`, its `-wal`. No SQLite connection is made to these files and their contents are never opened.

NVMe Data Units counters are full 128-bit little-endian integers; one data unit is **512,000 bytes**. Byte multiplication uses BigInteger, so even a maximum UInt128 does not overflow. JSON carries decimal strings, preserving precision beyond JavaScript's safe integer range. Golden vector: `0xAAB764 = 11,188,068`, producing **5,728,290,816,000 bytes**. Kelvin 322 gives 48.85 °C; zero/65535 temperature is unavailable.

NVMe windows retain first/last SMART points, health endpoints, sensor values in stored samples, count, temperature statistics and observed counter deltas/coverage. Counter regressions and power-on-hour regressions split valid coverage; negative deltas are excluded. Windows merges bridge adjacent valid endpoints so retention preserves deltas. Reports filter the configured drive number; data from a previously configured drive remains stored separately. A replacement drive with higher counters at the same number cannot be detected reliably without a durable unique device identifier.

`disk --since 1h`, `24h`, `7d` returns observed host deltas in that period, using actual SMART endpoint times. Daily intervals run from the previous day's last endpoint to the current day's last endpoint. Only intervals 22–26 hours long with valid counters contribute to mean, median, P95 and maximum-day baseline. Fewer than seven complete intervals is a learning period. A day with only one point has no invented delta. Projected bytes/day is an extrapolation over counter coverage; it is not a measured full day.

## Retention and query boundaries

Every 24 hours one transaction:

1. Merge closed minute buckets older than 14 days into aligned 15-minute buckets.
2. Merge closed 15-minute buckets older than 180 days into UTC daily buckets.
3. Delete fine rows only after corresponding coarse rows were written in that transaction.
4. Optionally delete daily buckets after `dailyDays` when nonzero; zero retains them indefinitely. Prune commit receipts after 30 days and record retention time.

All three history kinds downsample. Rollups read at most 512 source rows at a time; the transaction may span more pages. Cancellation/failure before commit leaves original rows intact. Retention is idempotent. No automatic VACUUM runs; SQLite reuses freed pages, so logical retention need not shrink the physical DB file. Anomalies are sparse durable events, retained indefinitely; one query returns at most 10,000 and explicitly reports that cap.

Reports stream the union of overlapping stored buckets in a read-only transaction. They include each bucket in full, including partially overlapping boundaries, and expose effective first/end bucket and maximum resolution. A daily row cannot answer an exact hour from six months ago. Requested-period wall time is distinct from observed seconds and metric-specific valid seconds. No coverage is fabricated during downtime, sleep, failed process baselines or unknown values.

## Failure and persistence budgets

SQLite WAL, synchronous NORMAL, busy_timeout 5 s, cache 1 MiB, automatic checkpoint after 256 pages, journal_size_limit 4 MiB. PERSIST_WAL preserves WAL/SHM after graceful close so a user with directory read permission can still query. Ordinary readers use Mode=ReadOnly and query_only=ON; they do not become checkpoint writers.

Auto-checkpoint/journal_size_limit alone cannot cap a WAL pinned by a reader. Before each batch/retention transaction, a WAL at 64 MiB triggers a nonblocking truncate checkpoint. If the reader prevents it, persistence pauses and retries at the next batch; the RAM backlog remains bounded to about six hours. The watermark can be exceeded by the current transaction; it is a growth stop, not an exact file-size ceiling. Close the holding reader to recover. At prolonged storage failure old uncommitted batches are discarded and their lost coverage is reported. A normal shutdown flushes once; an unflushable RAM backlog cannot survive process exit.

Corruption, unsupported schema, denied access and unavailable metrics are reported explicitly. Existing database bytes are not deleted, rewritten as a new database, or silently renamed. A newer schema requires a newer binary.

Sustained anomaly observations coalesce under one event ID. The end timestamp is the last confirmed positive observation. Severity is the most recently observed level; the evidence dictionary retains the peak value for the episode. Open/closed state is as of the last persisted observation, not a live notification channel.