# Acceptance record — AgentWatch 0.2.0

Validation date: 2026-09-07. The candidate is built and validated for a weekly trial. It has **not replaced or restarted the installed 0.1.0 service**. That service was observed running under LocalSystem with the same PID 8040 and creation time 2026-09-07T14:15:32.275442+03:00 throughout preparation. Multi-day stability, reboot/recovery and the actual elevated upgrade remain untested.

## Reproducible candidate

- Evidence root: `artifacts/trial-preparation-20260907-160559/`.
- EXE: `release-03/publish/AgentWatch.exe` beneath that root, 78,625,937 bytes, version 0.2.0, self-contained win-x64.
- SHA-256: `FE24291ADA05FC8CCF42FC62FB6E081BCB133E8F60EC5775611B607A5A9E2666`.
- Publication: 2026-09-07T13:53:50.0806980Z. `artifacts/latest-build.json` resolves the exact candidate.
- Locked restore, Release build with warnings-as-errors, **32 unit + 47 integration tests passed, 0 failed/skipped; 0 build warnings/errors**. TRX and build output are retained in `release-03/tests/trx/` and `release-03/build/`.
- No collector sampling logic, collector database schema or dependencies were changed. The collector's version string changed. Reporting, per-run diagnostics and the separate on-demand usage import are new.

## Verification

| Area | Evidence and result |
|---|---|
| Weekly data quality | Empty/partial/coarse histories, fractional period edges, missing metrics, gaps and current-versus-historical metadata tested. Unobserved time is not fabricated as uptime or zero usage. |
| Interpretation | Memory/paging diagnosis requires sample-level overlap; separate period totals alone fail to establish it. Confidence, evidence and alternatives are present. Episode merging stops at missing coverage and coarse daily history does not create precise episodes. |
| Markers | Local CLI shorthand, timestamp validation, escaping, stable IDs and contexts extending outside the main report period tested. Final CLI acceptance returned an exact 1,800-second context around an isolated synthetic marker. |
| Usage | Modern Codex records, legacy mirror precedence, response-ID and Claude message-ID deduplication, partial-line resume, absent values, cross-turn model mismatch, source truncation, oversized/malformed records and explicit pricing semantics tested. |
| Usage storage | Cursor and metadata updates are atomic; imports are repeatable. Source-quality warnings survive later clean imports. A future schema is rejected before database changes. Synthetic conversation text is absent from the stored projection. |
| Events and lifecycle | Session ends are not inferred from turn completion or idle time. Usage/activity/marks/anomalies/gaps/run events have stable identities and temporal context. Unfinished manifests retain an unknown outcome. |
| Log preservation | Multiple runs and rotated segments remain individually readable; legacy logs are preserved. Final runtime acceptance returned both the interrupted and cleanly completed run manifests. |
| Foreground runtime | Only an owned isolated instance was killed; its replacement ran normally. Committed history and a restart gap survived; observed duration after restart was 24.8597515 seconds, without inventing the interrupted run's unsaved time. |
| Corrupt input | Startup returned 1 and left the deliberately corrupt test database byte-for-byte unchanged. Its failed-run manifest was retained. |
| CLI compatibility | report/agents/disk/anomalies/self JSON schema 1; status/doctor/snapshot; human 24h and JSON 30d queries succeeded. Existing installed history is readable by the new CLI. |
| Upgrade preparation | `Update-Service.ps1 -Plan` verified the owned installation and candidate hash without mutation. All four build/runtime/performance/update scripts passed PowerShell syntax parsing. Actual stop/archive/replace/start/rollback requires a later elevated run. |

The runtime validation artifact is `runtime-final/validation.json`. The original wrapper inherited exit code 1 from its deliberately failing corrupt-input probe even though every assertion passed. The harness now resets the success exit status after all assertions; this final script-only correction does not change the tested EXE.

## Real local-format acceptance

Two specific local source files, one per provider, were imported to an isolated workspace usage store. Import read approximately 17.06 MB total and retained 2 sessions, **136 deduplicated usage records** and 370 combined timeline events. The Claude source had 140 observed assistant records corresponding to 67 unique message IDs; Codex contributed 69 usage records.

One Codex line exceeded 1 MiB and was skipped. This is a persistent completeness warning, not an assumption that the omitted line was irrelevant. Final CLI acceptance verified that a subsequent clean Claude import did not erase that warning. Source content was not copied to the metadata database or report. Costs remained unknown without exact applicable price rules; Claude output/reasoning values remained explicitly reported-only.

Evidence: `codex-import-final.json`, `claude-import-final.json`, `cli-acceptance.json`, `weekly-live-final.json`, `weekly-live-final.md`, `events-live-final.json`, `runtime-final/weekly-marker.json` and `runtime-final/runs.json`. The marker was labelled as a validation example and stored only in the isolated test directory.

The sample weekly report covered 2.74 observed hours out of 168 requested hours. It included development workload, 1 collector warning/error count and a memory/paging association with medium confidence and alternatives. It is a demonstration of report behavior, not a week's baseline or evidence that an AI agent caused slowdowns.

## Limits and next operating step

The six-minute candidate measurement passed process exit and storage checks; ordinary-user process attribution was incomplete and is explicitly reported. See [PERFORMANCE_0.2.md](PERFORMANCE_0.2.md) for measured overhead and its limits.

Existing service recovery settings and current LocalSystem operation were inspected read-only. No service fault injection, reboot, suspend/resume or installed 0.2.0 run was performed. Old live logs have point-in-time copies; their source run is still open. All new bounded runtime and performance instances have exited.

When ready, follow [WEEKLY_TRIAL.md](WEEKLY_TRIAL.md): perform the explicit elevated upgrade, create a trial-start marker, import local usage as the ordinary user, and preserve the full week of observations and subjective markers. Imports are on demand, so unavailable or deleted source history cannot be recovered by the service.
