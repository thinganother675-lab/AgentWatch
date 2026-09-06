# AgentWatch architecture

Version 0.1.0, Windows 11 x64, .NET 10 LTS. This is the implementation design, not an approved Stage-0 artifact. The complete user specification is in USER_REQUIREMENTS.ru.md.

```text
Windows Service / bounded foreground validation
  -> persistent native collectors (10 s, monotonic clock)
  -> RAM: interval splitting into UTC minute aggregates
  -> bounded pending batches (5 min)
  -> SQLite WAL, NORMAL, one writer
       -> read-only query services -> CLI report DTOs
       -> future on-demand stdio MCP adapter

Metadata-only hot files: 60 s     NVMe health: 15 min
Retention: daily, atomic rollup-before-delete
```

## Decisions

* One executable and one production project. Native interop, collectors, aggregation, storage, analysis and hosting have separate modules. A small development-only helper exercises real process I/O and process trees.
* Use Microsoft.Extensions.Hosting.WindowsServices and Microsoft.Data.Sqlite; no ORM, WMI polling, tracing, process spawning, web server or network client in the monitoring loop. Workstation GC; no trimming or AOT. SDK and restore caches are local to the project.
* Kernel CPU includes idle; subtract it. Process CPU is normalized over all logical processors. Counter baselines are in RAM and identified by PID plus creation FILETIME. A lost observation resets the baseline. First observations contribute no lifetime I/O. Attribution only follows a verified living parent with an earlier creation time, or a previously verified identity. Arbitrary node.exe processes are not agent roots.
* No raw-sample time series is stored; meta retains only the latest persisted snapshot as diagnostic context. Aggregates retain weighted sums, valid durations, min/max and totals, so coarse rollups remain mathematically correct and unavailable data stays unavailable. UTC interval splitting avoids assigning a whole ten-second observation to the wrong minute. Durations use Stopwatch; long delays or wall-clock discontinuities produce gaps and reset rate baselines.
* All physical-disk PDH instances are combined using `_Total`, explicitly labeled. NVMe queries target configurable physical drive 0 by default. A change of configured drive number or lifetime counter regression invalidates cross-device deltas; no negative subtraction or silent wraparound. Raw 128-bit values are decimal strings in SQLite and report JSON; multiplied byte values use BigInteger so the full 128-bit input range remains valid.
* `Memory\\Pages Output/sec` estimates bytes paged out to disk. These counters alone cannot isolate pagefile writes from mapped-file writes or prove SSD wear caused by an agent. NVMe host writes are controller host traffic, not NAND program/erase traffic. These corrections to the requested report wording are deliberate.
* WAL + synchronous NORMAL balances corruption resistance with acceptable loss of an unflushed batch on power loss. WAL autocheckpoint targets 256 pages; a 64 MiB writer watermark adds backpressure if a reader pins the journal, prepared statements share a five-minute transaction, retention runs daily. Read-only queries never mutate the database. No recurring VACUUM. Corruption stops persistence and is reported; existing files are preserved for explicit operator recovery, including their WAL/SHM.
* Retention uses a single tiered aggregate table keyed by resolution, UTC bucket, kind and item, with one compressed fast-metric frame per bucket and separate bounded hot-file/NVMe windows. Only closed UTC buckets roll up. Fine rows are deleted in the same transaction after coarser rows are inserted. Daily aggregates are indefinite by default. Hot-file and NVMe history also downsample so they cannot become unbounded minute/quarter-hour tables.
* Service uses LocalSystem for practical NVMe access. Installation captures the installing user's SID/profile; protected Program Files and ProgramData trees grant SYSTEM/Administrators control and that SID read access to history. An explicit profile/SID override supports installation from another administrative account. The service takes no untrusted commands and opens no listener.
* Collector failures are isolated, rate-limited and persisted as capability state. Storage failures keep a bounded RAM backlog and report dropped coverage rather than growing memory indefinitely. An exclusive own lock-file handle excludes concurrent services against one database.
* Installation is an explicit CLI operation requiring an elevated token. Development never bypasses UAC. Uninstall preserves history; purge additionally requires an explicit confirmation flag. Existing unrelated paths/reparse points are rejected at installation boundaries.

## Official references checked before implementation

* [Worker / AddWindowsService](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)
* [Single-file publishing and native extraction](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
* [GetSystemTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)
* [GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex), [GetPerformanceInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getperformanceinfo)
* [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes), [GetProcessIoCounters](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters), [process memory](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex)
* [Toolhelp snapshot](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/nf-tlhelp32-createtoolhelp32snapshot)
* [Language-neutral PDH counters](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw)
* [NVMe protocol queries](https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices), [NVME_HEALTH_INFO_LOG](https://learn.microsoft.com/en-us/windows/win32/api/nvme/ns-nvme-nvme_health_info_log)
* [SQLite connection strings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings), [SQLite synchronous API](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async), [SQLite WAL semantics](https://www.sqlite.org/wal.html)

Implementation and validation details are maintained in DATA_MODEL.md, OPERATIONS.md, SECURITY.md and PERFORMANCE.md.

Batch compression uses Brotli Optimal after the measured comparison in PERFORMANCE.md: the same synthetic week shrank from about 57 MB to 14.6 MB and native logical writes including checkpoints fell from about 39.5 to 15.4 MB per synthetic day. The physical table remains WITHOUT ROWID; no sample interval, retained metric or codec version was changed. Fatal SCM-hosted failures terminate the process nonzero after runtime cleanup; a clean StopHost would not invoke recovery.