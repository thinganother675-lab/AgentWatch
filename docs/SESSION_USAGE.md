# Session/usage and event contracts — version 0.2.0

## Collection boundary

The existing service does not inspect transcripts. `usage-sync` is an explicitly invoked, bounded CLI import into a separate per-user database. It streams local JSONL records and projects an allowlist: session/turn/response IDs, timestamps, model, explicit effort, token fields, event kind and byte-offset provenance. Prompts, assistant text, instructions, paths/cwd, tool arguments/results, attachments and credentials are neither persisted nor included in reports. The source files themselves contain conversations; processing them is an opt-in exception to v1's service-only metadata policy. The parser may encounter those bytes but ignores their contents.

Reads use file sharing compatible with active writers. Offsets advance only through complete newline-terminated records and commit atomically with their normalized metadata. Reads have byte/file/time limits; lines over 1 MiB and malformed records are skipped explicitly. Source quality flags survive later successful imports. Replaced/truncated sources are reparsed, preserving old accounting records; response/message IDs keep repeated observations idempotent. This is not a guaranteed supported transcript API: adapters are guarded, locally validated readers of observed formats. Missing or changed fields remain unknown.

## Codex adapter

- `session_meta.payload.id` or session_id identifies an explicit recorded start; `turn_context` supplies model/effort/turn_id.
- `token_usage_record.payload.response_id` is the accounting identity. Its usage fields are per-response input, cached input, cache write input, output and reasoning output. Model/effort are assigned only from a matching recorded turn context; a different turn receives null.
- Modern response records and older `event_msg.token_count` mirror one another in observed files. Modern response records take precedence per source; fallback records are removed from that source to avoid double-counting. Mixed legacy-only and modern segments in a rewritten file may therefore be incomplete; use source quality/provenance and do not infer missing totals.
- Legacy-only fallback uses total-counter fingerprints to avoid duplicate token_count notifications and last_token_usage for observed increments. It emits a counter-reset event on regression; it is less precise than response IDs.
- `task_started`, `task_complete` and `turn_aborted` describe turns, not complete session lifetimes. End is only populated for an explicit recognized session-end event; idle time never closes a session.

Official [Codex App Server documentation](https://learn.chatgpt.com/docs/app-server) describes structured thread/turn usage notifications. This implementation does not connect to App Server or infer that the JSONL representation is a guaranteed public contract. Local field examples were inspected without exposing message bodies. General token semantics are described in the [OpenAI Responses API](https://developers.openai.com/api/reference/typescript/resources/beta/subresources/responses/methods/retrieve).

## Claude adapter

The documented local transcript location is `~/.claude/projects/<project>/<session>.jsonl`; alternative roots can be supplied. The adapter reads top-level sessionId, timestamp and explicit effort, plus assistant message.id/model/usage. Repeated assistant records sharing a message ID are counted once. Input tokens exclude cache read/creation tokens, which remain separate.

Claude's official cost-tracking documentation warns that assistant per-step output_tokens can be a message_start placeholder; final counts are carried by SDK result messages. This adapter therefore preserves assistant output as reported-only and leaves final output/reasoning/cost unknown. Explicit usage.output_tokens_details.thinking_tokens is retained separately as reportedReasoningOutput, with the same reported-only qualification; it is never added a second time to output. It does not combine arbitrary SDK result totals with per-message usage, which could mix call/session/subagent scopes. No output counts are fabricated from text length. Session start is unknown when no explicit start metadata exists; first observed is retained separately.

Sources: [Claude sessions](https://code.claude.com/docs/en/sessions), [Claude SDK cost tracking and message-ID deduplication](https://code.claude.com/docs/en/agent-sdk/cost-tracking), [Claude local data and cleanup](https://code.claude.com/docs/en/claude-directory). Usage fields and client estimates do not establish subscription billing.

## Explicit price rules

`weekly --prices path.json` and `sessions --prices path.json` accept a JSON array of exact-model price rules. There is no bundled guessed tariff and no automatic network pricing lookup. Every rule has agent, model, validFromUtc, validToUtc, inputPerMillionUsd, cachedInputPerMillionUsd, cacheWritePerMillionUsd, outputPerMillionUsd and source. Provide a dated, verified price source and the tariff applicable to the recorded request class; cache TTL, batch/priority/region and service-tier differences must not be silently treated as equivalent.

Illustrative shape only; the following model and amounts are **not an actual tariff**:

```json
[
  {
    "agent": "codex",
    "model": "example-not-a-real-model",
    "validFromUtc": "2026-09-01T00:00:00Z",
    "validToUtc": "2026-10-01T00:00:00Z",
    "inputPerMillionUsd": 2,
    "cachedInputPerMillionUsd": 1,
    "cacheWritePerMillionUsd": 2,
    "outputPerMillionUsd": 10,
    "source": "Replace with the exact verified tariff and its applicability"
  }
]
```

An estimate requires exactly one applicable rule and every required token field. With Codex, cached read/write tokens are subtracted from input before applying the uncached input rate. With Claude, input is already uncached. Reasoning is not added again to output. Session cost is null if any included request cannot be priced; no partial total is presented as a full cost. The result is an API-equivalent estimate, not actual billed cost or an amortized share of a subscription.

## Storage and event contract

Collector database schema remains 1. The usage store has its own schema 1: source cursors/quality, deduplicated usage, session activity, lifetime bounds and last import metadata. It never takes the collector writer lock. User markers remain independent user-authored JSON files, and privileged service code does not consume them.

Weekly schema 1 contains measurements, quality, findings, selected episodes, user observations, run history, session usage, temporal contexts and events. Event schema 1 provides stable ID, type, timestamp, optional end/last-observed bound, source, optional agent/session/run IDs, summary and evidence. IDs are stable hashes or explicit marker/run IDs. Types include session.start/end/activity/usage, turn.start/end/aborted, usage.counter-reset, user.mark, service.run.start/end, monitoring-gap, service-restart-gap, history-dropped and anomaly.*. For ongoing anomalies the upper bound is the last observed timestamp, with openAtLastObservation in evidence; it is not an asserted end of the underlying condition.

Events are facts from specified sources. Interpretations live in a different structure with confidence, evidence, alternatives and next action. Mark context reads ±15 minutes even when that extends outside the main report period. Session context is clipped to the requested period and its observed activity envelope; it includes shared agent-kind CPU/memory/I/O, never pretends to distribute resources between concurrent sessions, and uses whole overlapping stored buckets. Metadata fields that would require command/process/session ownership inference are absent.

`IEventQueryService` and `IReportQueryService` provide a future adapter boundary. No MCP server, network listener or automatic optimization is implemented.
