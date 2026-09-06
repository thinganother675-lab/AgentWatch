# Future query adapter and token analytics

No MCP implementation, SDK, listener or Node/Python runtime is included in v1.

`IReportQueryService` is the boundary: GetReport, GetSystemUsage, GetAgentUsage, GetDiskUsage, GetHotFiles, GetAnomalies, GetMonitorHealth. `QueryPeriod` and public report records are transport-neutral; schemaVersion 1 is serialized in CLI JSON. Database rows, connections and compressed payloads are not exposed as tool contracts. All numeric units and missingness are explicit; NVMe large integers are decimal strings.

A future separately invoked stdio adapter can call this read-only layer under the reporting user's token. It should have a small fixed set of query tools, bounded periods/output, cancellation, no arbitrary SQL/file paths and no service-control or optimization tools. Host/write statistics should preserve the same measurement-layer limitations. Keep deployment/on-demand costs separate from the always-running sampler.

Example query intents already answerable through CLI/query services:

- Codex/Claude observed presence and logical read/write totals over seven days.
- Minimum available RAM, valid time below 512 MiB and estimated disk page-out over one day.
- NVMe host-write delta over an hour/day/week using actual endpoint timestamps.
- High logical-write windows overlapping Codex WAL growth, as correlation rather than proof of causation.
- AgentWatch CPU, memory, own logical I/O, history size and collector failures.

Fine-grained historical questions are constrained by retained resolution. A tool must return the effective bucket bounds, observation coverage and limitations with its answer. It must not silently turn nulls into zeroes or label agent process presence as productive work.

Future token analytics needs a separately approved data source, retention policy and privacy review. Do not infer tokens or cost from process I/O. V1 deliberately does not parse conversations, agent log contents, API responses or credentials. A future `ITokenUsageProvider` could return coarse timestamp/model/input/output/cached-token totals from an explicit supported source; it should remain optional and outside the native fast polling loop. No speculative implementation is present.
