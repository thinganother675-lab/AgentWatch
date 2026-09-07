# Future on-demand query adapter

No MCP server, SDK, listener, dashboard or Node/Python runtime is included in AgentWatch 0.2.0.

IReportQueryService exposes the existing report schema 1. IEventQueryService exposes event schema 1 with stable IDs, UTC times, sources, optional agent/session/run identity and explicit evidence. GetWeeklyReport provides FACTS, quality, interpretations with confidence/evidence/alternatives, selected episodes, local session usage and marker contexts. See SESSION_USAGE.md for schema and source limits.

A future separately invoked stdio adapter can use these read-only APIs under the reporting user's token. Keep a small fixed set of tools, bounded periods/results, cancellation, no arbitrary SQL or file paths, and no service-control or optimization actions. Always return coverage, missingness, effective bucket bounds and provenance with the answer. Session/resource links are temporal overlap, not per-session attribution. Preserve unknown fields as null.

Token metadata import is implemented as a separate explicit CLI action. A future adapter should not trigger transcript scans implicitly while answering a normal metric query. It may report the last import time and ask the caller to refresh the normalized store explicitly. Price estimates need complete final tokens and an exact dated rule, and are not subscription billing.

Useful future questions now have local CLI equivalents: what happened ±15 minutes around a marker; which sessions had observed activity in an episode; which facts support a memory-pressure interpretation; what alternative explanations remain; and which source/coverage limits weaken the answer. Full command/task histories and PID-to-session ownership are still unavailable and must not be inferred.
