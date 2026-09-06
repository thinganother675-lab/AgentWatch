# Security and privacy

AgentWatch observes local system performance. It has no application HTTP/TCP listener, custom IPC query service, MCP server, outbound telemetry, update agent, subprocess loop or remote command path. The production collection loop uses Windows APIs and its own SQLite database. NuGet/SDK downloads belong only to development scripts.

## Read scope

- System CPU/memory and persistent PDH counters.
- Toolhelp executable basenames, process IDs/parent IDs, creation times, CPU/I/O/memory. Process basenames and metrics may appear in explicit `snapshot --json`; historical storage contains group aggregates rather than a per-process history.
- Query-only `PhysicalDriveN` handle, shared read/write, access mask 0. Only IOCTL_STORAGE_QUERY_PROPERTY, NVMe log page 02h. No firmware command, device write, secure erase, ATA bridge probing or SMART polling utility.
- Metadata for the four known Codex database/WAL files. No SQL queries, content reads, directory watching or recursive scanning under `.codex` or `.claude`. Profile/.codex/file reparse points are rejected.
- Own configuration, diagnostics and history files. No environment dump, tokens, browser state, prompts, message bodies, log contents or source-file contents.

## Installation boundary

Installation explicitly requires an elevated token and a published single-file executable. It does not attempt UAC bypass or automatic elevation. The LocalSystem account is used for practical hardware/process access; the application does not expose that privilege through a listener. The measured foreground run uses the current ordinary user token and the same monitoring Host; SCM/LocalSystem validation still requires an elevated machine test.

Paths are fixed to `%ProgramFiles%\AgentWatch` and `%ProgramData%\AgentWatch`. Nonempty directories require the exact AgentWatch ownership marker. Installation rejects reparse-point ancestors and hard-linked files before changing existing file ACLs. DACL inheritance is disabled and replaced with explicit SYSTEM/Administrators full control and the designated reporting user's read/execute access. The owner is Administrators. Installing from another account requires explicit reader SID/profile overrides.

New service data inherits the protected directory ACL. Existing files receive protected explicit ACLs. Native bundle extraction is configured in the service-specific `Environment` registry value to use `%ProgramFiles%\AgentWatch\runtime`; the ordinary reporting user cannot write there. This is not a machine-wide environment change. The installed service binary path is fully quoted. Uninstall checks the service's actual ImagePath before stopping/deleting it, disables autostart to cancel pending recovery, and removes only owned files. Runtime cleanup validates all entries before recursive deletion; an in-use cache is retained and reported.

The installing user may itself be a member of Administrators with a filtered token. The intended normal-token right is read access; raising that user's token restores their administrator authority. This ACL is not intended to restrict an elevated administrator or a compromised LocalSystem process.

## Storage and reporting

One writer is enforced with an exclusive own lock-file handle. Normal query connections open read-only and use parameterized period queries. No raw SQL is accepted by the CLI. Runtime does not execute migration code or plugins from the data directory. JSON configuration is capped at 128 KiB; structured compressed history has bounded decoding. Configuration values are validated before use. Protected configuration prevents an ordinary reader from changing the LocalSystem service's collection paths or cadence.

Own logs contain startup/shutdown, capability transitions and rate-limited failures, with two files capped around 1 MiB each. Errors may include local paths and executable basenames; reports are local machine activity metadata and should be shared deliberately. `--json` writes only to the caller's stdout.

WAL/SHM are retained with PERSIST_WAL for read-only users. Opening arbitrary live history with `immutable=1` would be incorrect and is not used. Backups/recovery preserve the DB with its WAL and SHM. No security settings, Defender exclusions, pagefile parameters, power plan, fan controller, agent files or unrelated services are changed.

## Known boundaries

The binary is not code-signed. Integrity can be checked against the locally generated SHA-256 acceptance artifact; production distribution should use an appropriate signing/release process. Service ACLs, recovery and extraction under LocalSystem were implemented and reviewed but cannot be certified by a normal-user foreground test. Unsupported drivers/access failures remain visible capabilities rather than reasons to alter Windows security.

The ordinary .NET runtime diagnostic endpoint is distinct from an AgentWatch query protocol: on Windows, the runtime creates its standard OS-protected named pipe, accessible to the launching identity or privileged accounts. AgentWatch does not start an EventPipe/ETW session or attach a tracing tool. Runtime diagnostic behavior is documented by [Microsoft](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/diagnostic-port); it is not a network listener or an implemented MCP transport.