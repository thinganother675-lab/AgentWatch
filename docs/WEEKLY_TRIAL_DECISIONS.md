# Weekly trial preparation — 2026-09-07

Status: implementation scope authorized in conversation; native engineering review, not an approved artifact from the unavailable stage-zero-research skill.

## Existing run is protected

At inspection AgentWatch 0.1.0 was running under LocalSystem, PID 8040, started 2026-09-07 14:15:32 +03:00. It has delayed automatic startup and two 60-second recovery actions. The ordinary reporting user has read/execute access to ProgramData and can read current history. Persisted collector health was OK. This confirms installed operation/configuration, not restart/recovery/sleep testing.

No existing service/process will be stopped, no installed EXE replaced, and no production database modified by this preparation. New builds and validation runs use unique artifact directories. Copies of the existing diagnostic logs were saved before development. They are point-in-time copies, not final logs of the still-running instance. The untracked docs.zip belongs to the user and is outside this change.

## Data sufficiency

| Question | Existing evidence | Decision |
|---|---|---|
| Was RAM scarce, and did paging coincide? | weighted memory thresholds, page rates, sample-level overlap totals | sufficient; explain overlap without claiming pagefile-only bytes or causation |
| Did CPU stay busy? | weighted CPU, min/max, histogram and minute history | sufficient; distinguish whole-period average from selected episodes |
| What load did agents create? | tree-level CPU, logical bytes, presence, inaccessible counts | sufficient for observed lower bounds, not command/task history |
| What changed on the SSD? | NVMe endpoints, exact host counters, health, regressions | sufficient after enough time; not NAND writes or process attribution |
| Can results be trusted? | metric valid durations, global coverage, missed/error counters and latest collector metadata | expose this explicitly; do not call unobserved time data loss or machine uptime |
| Did measured load feel slow? | no subjective observations | add explicit user-authored local timestamp markers |
| What happened in each run? | shared rotating diagnostic files, latest instance metadata | add unique run manifests and numbered diagnostic segments without automatic deletion |

No schema migration, extra polling, new dependency, listener, LLM call, ETW session, full process-command history or top-process stream is needed for this trial. Query-time weekly analysis uses existing minute aggregates. Old schema-1 histories remain usable by both versions. Version 0.2.0 introduces a separate versioned weekly report; existing report schema stays 1.

## Analysis rules and limits

Weekly reports are bounded to 31 days. Requested duration, stored bucket span, actual observed duration, and per-metric valid duration are separate. Whole overlapping buckets cannot reveal exact fractional-edge coverage. Latest collector state/freshness describe the most recent persisted instance, not historical availability throughout the requested week. Lack of observations does not prove the computer was powered off.

Selected episodes are stored-bucket windows: low memory with observed page-out, high mean CPU, or high observed agent logical write rates. Thresholds are transparent heuristics for inspection, not automatic diagnoses. Adjacent matching buckets may be merged; sleep/gap periods with insufficient bucket coverage interrupt merging. Evidence and recommendations state uncertainty. User markers remain per-user files and are not consumed by the privileged service.

Run logs use logs/runs/<UTC-PID-random-id>/ with a start/end manifest and numbered segments. Segments rotate by size without replacing earlier segments. The manifest stores only version, mode, sampling, timestamps, process ID, outcome and log errors. An unfinished manifest means unknown outcome, not proof of a running process. Legacy logs remain in place. Retention of diagnostics is manual; indefinite preservation necessarily grows with run count and errors.

## Acceptance

Test empty/partial/coarse/missing-metric history, sample overlap versus coincidental period totals, stale metadata, bounded episodes, marker validation and escaping, per-run log preservation, clean/failing outcomes, and old-history readability. Test controlled foreground stop/restart only against new disposable histories. Publish to a versioned directory and measure the shipping binary. Confirm the original service has the same PID and creation time at the end.

For a live database archive use SQLite's supported online backup API, not independent filesystem copies of DB/WAL/SHM. Microsoft's BackupDatabase helper performs a complete backup and may briefly block writers; do not schedule it in the sampler. Sources: [Microsoft.Data.Sqlite online backup](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup), [SQLite online backup](https://www.sqlite.org/backup.html).

## Authorized session/usage extension

The user subsequently authorized four additions: Codex/Claude session usage, timestamped subjective marks with ±15-minute context, explicit facts/interpretations with confidence/evidence/alternatives, and a shared event model. These fit the existing query boundary. A separate per-user schema-1 usage database is added; the no-migration decision above applies to the collector history.

Import remains manual and bounded, outside the privileged service and sampling loop. Only allowlisted source metadata is persisted. Session end, model, effort and token categories are nullable; idle periods and mismatched turn context are not evidence for missing fields. Claude assistant output and thinking-token values remain reported-only because final usage is not established by these records. Cost requires an explicit exact-model dated price rule and complete final counts; unknown values are not replaced by estimates from text.

Temporal contexts compare session activity with existing shared agent-kind/system metrics. Concurrent sessions are not assigned individual resource shares. Source quality and query truncation are part of the output. Stable event IDs cover provider activity, user marks, monitored anomalies, data gaps and run lifecycle; no MCP transport was added.

Final build/runtime/format acceptance is recorded in VALIDATION_0.2.md and the shipping binary's short overhead measurement in PERFORMANCE_0.2.md. The ordinary-user measurement exposed incomplete process access and reports it explicitly. The installed service is still the protected 0.1.0 run; upgraded LocalSystem behavior is a later validation step.
