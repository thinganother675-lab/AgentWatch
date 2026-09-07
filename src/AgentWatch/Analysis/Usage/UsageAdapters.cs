using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentWatch.Analysis.Usage;

public static class UsageAdapters
{
    public static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static JsonElement P(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var p) ? p : default;
    private static string? S(JsonElement e, string key)
    {
        var p = P(e, key); var s = p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return s is { Length: > 0 and <= 200 } && !s.Any(char.IsControl) ? s : null;
    }
    private static long? N(JsonElement e, string key)
    { var p = P(e, key); return p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) && v >= 0 ? v : null; }
    private static DateTimeOffset? Time(JsonElement e, string key)
    {
        var s = S(e, key);
        if (s is null || !(s.EndsWith('Z') || s.Length >= 6 && s[^3] == ':' && s[^6] is '+' or '-')) return null;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time.ToUniversalTime() : null;
    }

    public static void Parse(string agent, JsonElement root, SourceCursor cursor, string fileId, long offset,
        Action<UsageEntry> usage, Action<SessionActivity> activity)
    {
        var time = Time(root, "timestamp"); if (time is null) return;
        var type = S(root, "type"); var payload = P(root, "payload");
        void Event(string eventType, string session, string? turn = null) => activity(new(
            StableId($"{agent}|{fileId}|{offset}|{eventType}"), agent, session, eventType, time.Value, turn, fileId, offset));
        if (agent == "codex")
        {
            if (type == "session_meta")
            {
                cursor.SessionId = S(payload, "id") ?? S(payload, "session_id");
                if (cursor.SessionId is { } id) Event("session.start", id);
                return;
            }
            if (type == "turn_context")
            { cursor.Model = S(payload, "model"); cursor.Effort = S(payload, "effort"); cursor.TurnId = S(payload, "turn_id"); return; }
            var session = S(payload, "thread_id") ?? cursor.SessionId;
            if (session is null) return;
            var eventType = S(payload, "type");
            if (type == "event_msg" && eventType is "task_started" or "task_complete" or "turn_aborted" or "session_end")
            {
                Event(eventType switch { "task_started" => "turn.start", "task_complete" => "turn.end", "turn_aborted" => "turn.aborted", _ => "session.end" },
                    session, S(payload, "turn_id") ?? cursor.TurnId);
                return;
            }
            JsonElement tokens; string key; string basis;
            if (type == "token_usage_record")
            {
                var response = S(payload, "response_id"); if (response is null) return;
                cursor.HasResponseRecords = true; tokens = P(payload, "usage"); key = response; basis = "codex.response-usage";
            }
            else if (type == "event_msg" && eventType == "token_count" && !cursor.HasResponseRecords)
            {
                var info = P(payload, "info"); var total = P(info, "total_token_usage");
                tokens = P(info, "last_token_usage"); var totalCount = N(total, "total_tokens");
                if (tokens.ValueKind != JsonValueKind.Object || totalCount is null) return;
                var signature = StableId(total.GetRawText());
                if (signature == cursor.LastCumulativeUsage) return;
                if (cursor.LastCumulativeTotal is { } before && totalCount < before)
                    Event("usage.counter-reset", session, cursor.TurnId);
                cursor.LastCumulativeUsage = signature; cursor.LastCumulativeTotal = totalCount;
                key = $"{session}|{cursor.TurnId}|{signature}"; basis = "codex.legacy-token-count";
            }
            else return;
            if (tokens.ValueKind != JsonValueKind.Object) return;
            usage(new(StableId("codex|" + key), agent, session, S(payload, "turn_id") ?? cursor.TurnId, time.Value,
                S(payload, "turn_id") is { } usageTurn && usageTurn != cursor.TurnId ? null : cursor.Model,
                S(payload, "turn_id") is { } effortTurn && effortTurn != cursor.TurnId ? null : cursor.Effort, new(N(tokens, "input_tokens"), N(tokens, "output_tokens"), N(tokens, "cached_input_tokens"),
                    N(tokens, "cache_write_input_tokens"), N(tokens, "reasoning_output_tokens"), "includes-cache"), basis, fileId, offset));
        }
        else if (agent == "claude")
        {
            var session = S(root, "sessionId") ?? S(root, "session_id"); if (session is null) return;
            cursor.SessionId = session;
            if (type is "user" or "assistant") Event("session.activity", session);
            if (type == "system" && S(root, "subtype") == "session_end") { Event("session.end", session); return; }
            if (type != "assistant") return;
            var message = P(root, "message"); var id = S(message, "id"); var tokens = P(message, "usage");
            if (id is null || tokens.ValueKind != JsonValueKind.Object) return;
            var model = S(message, "model"); if (model == "<synthetic>") return;
            // Claude assistant records can repeat for tool blocks. ID is the accounting identity.
            // Official SDK documentation says per-step output can be a message_start placeholder.
            // Preserve it as reported-only evidence, do not advertise it as final output or compute a full cost from it.
            usage(new(StableId("claude|" + id), agent, session, null, time.Value, model, S(root, "effort"),
                new(N(tokens, "input_tokens"), null, N(tokens, "cache_read_input_tokens"), N(tokens, "cache_creation_input_tokens"),
                    null, "excludes-cache", N(tokens, "output_tokens"), N(P(tokens, "output_tokens_details"), "thinking_tokens")), "claude.assistant-usage-output-unverified", fileId, offset));
        }
    }
}
