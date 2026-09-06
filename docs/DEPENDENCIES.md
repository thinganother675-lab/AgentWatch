# Dependency decision and reproducibility

Verified on 2026-09-06. Production targets Windows 11 x64 / .NET 10 LTS, SDK 10.0.400, runtime 10.0.11. SDK archive SHA-512 and official URL are pinned in `scripts/Bootstrap-Dotnet.ps1`; `global.json` pins SDK 10.0.400. Bootstrap checks the checksum before project-local extraction. No PATH, global environment, registry or system SDK install is required.

| Dependency | Version | Purpose / license |
|---|---|---|
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.11 | SCM lifetime, Generic Host; MIT |
| Microsoft.Data.Sqlite / Core | 10.0.11 | Small synchronous SQLite API without ORM; MIT |
| SQLitePCLRaw.bundle_e_sqlite3, core, provider, lib | 2.1.13 | Bundled native SQLite and interop; package license Apache-2.0; SQLite engine public domain |
| Actual native SQLite returned by sqlite_version() | 3.53.3 | Verified in the published binary, not inferred from wrapper version |
| Microsoft.NET.Test.Sdk | 18.9.0 | Development/test only; MIT |
| MSTest adapter/framework | 4.4.0 | Development/test only; MIT |

Transitive identities, resolved versions and NuGet content hashes are checked into each project's `packages.lock.json`. NuGet auditing is enabled. No ORM, command framework, permanent tracing library, web framework, Python, Node, WMI package or MCP runtime was added. Generic Host defaults/environment/file watchers and default logging providers are disabled; the service uses a bounded local warning/error logger. Packages with EventLog/configuration APIs can be present transitively without those providers being active.

## SQLite choice

Native SQLite is shipped with the application instead of relying on Windows' system `winsqlite3.dll` version. The compatible SQLitePCLRaw 2.1.13 patch family was explicitly pinned; the actual native version remains 3.53.3. Do not equate wrapper package numbering with SQLite release numbering. This build includes the [WAL-reset fix](https://www.sqlite.org/wal.html); startup rejects SQLite below 3.51.3. [SQLite 3.53.4](https://www.sqlite.org/releaselog/3_53_4.html) and [SQLitePCLRaw 3.0.5](https://github.com/ericsink/SQLitePCL.raw/releases/tag/v3.0.5) were available at review time; the latter changes the native package family. A major wrapper/native-package migration was not necessary to obtain the WAL fix for this v1. Future dependency updates should explicitly verify native version, license, publish extraction and concurrent WAL tests again.

SQLite is used synchronously because its async methods do not make file I/O asynchronous. The writer executes small prepared batches every five minutes, not every fast sample. WAL/NORMAL deliberately accepts recent-data loss while avoiding per-sample fsync overhead. No connection pooling, external SQLite installation or permanent helper process is needed. Read-only report connections and retained WAL/SHM were tested while the writer is open and after graceful closure.

## Publish and licensing

Publish uses self-contained win-x64, PublishSingleFile, IncludeNativeLibrariesForSelfExtract, no trimming and no NativeAOT. There is exactly one distributed EXE. Native extraction is an expected .NET single-file behavior; the service receives a protected cache path. Managed debug information is embedded, so a separate PDB is not required. The development process/tree fixture stays in test outputs.

The Microsoft NuGet libraries listed above declare MIT licenses; SQLitePCLRaw NuGet nuspecs declare Apache-2.0 and SQLite itself is public domain. The SDK distribution also supplies its own license and third-party notices, preserved verbatim in docs/licenses. Preserve upstream license notices when distributing. See [Microsoft.Data.Sqlite docs](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/), [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw), [SQLite copyright](https://www.sqlite.org/copyright.html), and [single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview). The repo does not invent a separate commercial license or sign the executable.
