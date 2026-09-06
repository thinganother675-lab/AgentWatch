# Operations

## Install and verify

Build/publish first. From an elevated PowerShell, run the published EXE (not `dotnet run` or the development apphost):

```powershell
& 'C:\sarychev\Codex\AgentWatch\artifacts\publish\AgentWatch.exe' install
```

The default reader and monitored profile are those of the installing identity. If a different administrator performs installation, get the intended user's SID by running `[Security.Principal.WindowsIdentity]::GetCurrent().User.Value` in that user's session, then supply it explicitly:

```powershell
.\artifacts\publish\AgentWatch.exe install --profile 'C:\Users\Name' --reader-sid 'S-1-5-21-...'
```

Only these installation locations are used:

- `%ProgramFiles%\AgentWatch\AgentWatch.exe`, ownership marker and protected `runtime` extraction cache.
- `%ProgramData%\AgentWatch\config.json`, own history/WAL/SHM/writer lock, ownership marker, two bounded log files.
- SCM service `AgentWatch`, account LocalSystem, own process, delayed automatic start; two restarts after 60 seconds, then no action, reset failure count after 24 hours.

Installation refuses an existing service rather than overwriting an unrelated configuration. It starts the new service, waits for Running and verifies a fresh `startedUtc` in the database. A failed post-create installation is disabled and stopped, with its evidence preserved. Correct the cause and use `uninstall` before retrying.

On an elevated machine, verify the complete SCM path:

```powershell
Get-Service AgentWatch
sc.exe qc AgentWatch
sc.exe qfailure AgentWatch
sc.exe qfailureflag AgentWatch
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\AgentWatch' -Name DelayedAutoStart,Environment
.\artifacts\publish\AgentWatch.exe doctor
Restart-Service AgentWatch
Stop-Service AgentWatch
Start-Service AgentWatch
.\artifacts\publish\AgentWatch.exe status
```

Verify the delayed start value is 1, two restart actions followed by NONE, the quoted owned ImagePath, and the protected runtime extraction path. Check `Get-Acl` for both owned directories and the generated DB/WAL/SHM. In the intended user's ordinary session, confirm `report --since 24h --json` succeeds and modifying the service/data files is denied. Do not weaken ACLs just to make a query work. Actual SCM/LocalSystem/reboot and normal-user ACL tests require elevation and were not performed in the normal-token development session.

## Configuration and scheduling

Use the root `config.example.json` as the field reference. Installed config captures the real monitored profile; LocalSystem's own profile is not the desired Codex profile. Normal cadence is 10 seconds fast, 60 seconds metadata, 15 minutes NVMe, five minutes persistence. Shortening these intervals changes both measurement resolution and overhead. Restart the service after edits; there is no config file watcher.

Initial RAM warning is available memory below 512 MiB for 120 observed seconds. Critical RAM adds below 256 MiB and page output >=100 pages/s for the same duration. Codex logical-write warning is >100 MiB/min for 600 seconds; critical >500 MiB/min for 300 seconds. Codex logs WAL >512 MiB or observed positive logs/its WAL growth >500 MiB in ten minutes produces a warning. SSD warning >=65 °C, critical >=70 °C. Health changes are event-based; an unsafe-shutdown increase is informational. Own DB/WAL warnings use 256/32 MiB with a 60-second sustain. These are configurable starting points, not diagnoses or automatic optimizations.

Retention is daily and transactional. `dailyDays: 0` preserves daily history. No regularly scheduled VACUUM, checkpoints per fast sample or external task scheduler is required. A five-minute persistence batch can be lost on a crash; NORMAL durability additionally permits recent committed WAL loss on power interruption. A graceful stop captures a final sample and flushes pending aggregates.

## Status and degraded operation

`snapshot` obtains fresh native readings and works without history. `status` reads the service state and last persisted metadata, including its age and recorded instance state/uptime. That recorded uptime is not a live guarantee that the service still runs. `doctor` checks the actual schema/quick_check, directory rights under the current token, collector capabilities, configured drive and native SQLite version. A directory that is read-only to the reporting user is expected. Fresh SMART may still be inaccessible on other drivers/tokens; the other collectors continue.

An inaccessible process, vanished PID or missing counter is not a zero measurement. Process identities re-baseline after restart, missed observations, long delays and clock jumps. Sleep/downtime is excluded from fast coverage. NVMe lifetime-counter endpoints can span sleep/downtime; reports show their actual times. Do not interpret either time domain as laptop uptime outside monitor coverage.

Storage errors keep at most roughly six hours of pending batches in RAM; oldest batches then drop with an explicit coverage-loss event. A WAL at 64 MiB triggers a nonblocking checkpoint; a pinned external reader pauses persistence until it releases its snapshot. Own log files remain bounded around 2 MiB total. Look at the last error and release long report/SQLite sessions rather than deleting a live WAL. Disk-full/denied access may prevent the final flush; the error log reports the remaining batch count. Fatal startup/schema errors terminate an SCM-hosted process with Environment.Exit(1), after the runtime has unwound its cleanup. A clean StopHost alone would not trigger Windows recovery. Foreground validation returns exit code 1 through the host. Normal user/SCM stops remain graceful with the final batch flush.

## Backup and corruption

For a consistent simple backup, stop **only AgentWatch**, copy its own DB/WAL/SHM and configuration into a new protected backup directory, then start AgentWatch. Back up all existing companion files together; a live `Copy-Item` of only the main DB is not a reliable backup. The product does not supply an automatic backup service.

```powershell
Stop-Service AgentWatch
$backup = Join-Path 'C:\AgentWatchBackups' (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Path $backup | Out-Null
Get-ChildItem -LiteralPath "$env:ProgramData\AgentWatch" -File |
  Where-Object Name -In @('agentwatch.db','agentwatch.db-wal','agentwatch.db-shm','config.json') |
  Copy-Item -Destination $backup
Start-Service AgentWatch
```

On detected corruption, preserve the existing files and logs; do not delete WAL/SHM, run VACUUM, overwrite the DB, or silently create a fresh history. Stop the service, make a complete copy, and inspect/recover a **copy** using a compatible SQLite tool or restore a verified backup. If choosing a new history, archive the entire old database set explicitly first. `doctor` detects incompatibility/quick_check failure but is not a recovery utility.

## Upgrade and uninstall

Keep a copy of the published new EXE outside Program Files. In elevated PowerShell, invoke the existing `uninstall` from a publish copy; it disables/stops/removes AgentWatch but retains ProgramData history/config. Then run the new published `install`, which preserves history, validates the schema and reapplies ACLs. V1 supports schema 1 only and rejects future versions. Back up before a later version with real schema migrations.

```powershell
.\artifacts\publish\AgentWatch.exe uninstall
# Explicit irreversible removal of AgentWatch's known data/config files:
.\artifacts\publish\AgentWatch.exe uninstall --purge-data --confirm-purge
```

Uninstall validates ownership and ImagePath. Unknown files and markers are retained; it never deletes another application's directory. If invoked from the installed EXE, Windows may hold that file/cache open: its EXE removal is scheduled at reboot and an in-use cache is reported. Invoke uninstall from the external publish copy for immediate cleanup of unloaded binaries. No reboot is initiated by AgentWatch.

## Exit codes and diagnostics

0 = command succeeded; 1 = runtime/collector validation/history error; 2 = invalid command/option/period; 3 = denied access or required elevation. `doctor` returns 1 for ERROR checks; WARN capabilities, no installed service, learning baseline or expected read-only rights remain usable diagnostic output. JSON is written to stdout; configuration warnings and failures use stderr. Report commands fail clearly when no history exists. Native collector errors are isolated and rate-limited; no exception is turned into a false measurement.
