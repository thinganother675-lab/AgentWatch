using System.Text.Json;
using AgentWatch.Configuration;

namespace AgentWatch.Analysis.Usage;

public sealed record TokenAmounts(long? Input, long? Output, long? CacheRead, long? CacheWrite,
    long? ReasoningOutput, string InputSemantics, long? ReportedOutput = null, long? ReportedReasoningOutput = null);
public sealed record UsageEntry(string Id, string Agent, string SessionId, string? TurnId, DateTimeOffset TimestampUtc,
    string? Model, string? ReasoningEffort, TokenAmounts Tokens, string SourceKind, string SourceFileId, long SourceOffset);
public sealed record SessionActivity(string Id, string Agent, string SessionId, string Type, DateTimeOffset TimestampUtc,
    string? TurnId, string SourceFileId, long SourceOffset);
public sealed record UsageSession(string Agent, string SessionId, DateTimeOffset FirstObservedUtc,
    DateTimeOffset? StartedUtc, DateTimeOffset? EndedUtc, DateTimeOffset LastActivityUtc,
    IReadOnlyList<string> Models, IReadOnlyList<string> ReasoningEfforts, TokenAmounts TokensInRequestedPeriod,
    int UsageRecords, decimal? CostEstimateUsd, string CostBasis, IReadOnlyList<string> Limitations);
public sealed record UsageInventory(IReadOnlyList<UsageSession> Sessions, IReadOnlyList<UsageEntry> Usage,
    IReadOnlyList<SessionActivity> Activities, DateTimeOffset? LastImportUtc, bool Truncated, IReadOnlyList<string> Notes);
public sealed record UsageImportResult(DateTimeOffset TimestampUtc, int FilesConsidered, int FilesRead,
    long BytesRead, int UsageRecords, int ActivityRecords, int InvalidRecords, int OversizedLines,
    bool BudgetReached, IReadOnlyList<string> Warnings);

public sealed class SourceCursor
{
    public long Offset { get; set; }
    public long InvalidRecords { get; set; }
    public long OversizedLines { get; set; }
    public bool SourceWasReplaced { get; set; }
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? TurnId { get; set; }
    public bool HasResponseRecords { get; set; }
    public string? LastCumulativeUsage { get; set; }
    public long? LastCumulativeTotal { get; set; }
    public string? PrefixHash { get; set; }
    public int PrefixLength { get; set; }
}

public sealed record PriceRule(string Agent, string Model, DateTimeOffset ValidFromUtc, DateTimeOffset ValidToUtc,
    decimal InputPerMillionUsd, decimal CachedInputPerMillionUsd, decimal CacheWritePerMillionUsd,
    decimal OutputPerMillionUsd, string Source);

public static class UsagePricing
{
    public static IReadOnlyList<PriceRule> Load(string? path)
    {
        if (path is null) return [];
        if (new FileInfo(path).Length > 131072) throw new InvalidDataException("Pricing file exceeds 128 KiB.");
        var rules = JsonSerializer.Deserialize<List<PriceRule>>(File.ReadAllText(path), JsonDefaults.Options) ?? [];
        if (rules.Count > 200 || rules.Any(r => string.IsNullOrWhiteSpace(r.Model) || string.IsNullOrWhiteSpace(r.Source)
            || r.ValidFromUtc >= r.ValidToUtc || r.InputPerMillionUsd < 0 || r.CachedInputPerMillionUsd < 0
            || r.CacheWritePerMillionUsd < 0 || r.OutputPerMillionUsd < 0)) throw new InvalidDataException("Invalid explicit price rules.");
        return rules;
    }
    public static decimal? Estimate(UsageEntry entry, IReadOnlyList<PriceRule> rules)
    {
        var matches = rules.Where(r => r.Agent == entry.Agent && r.Model == entry.Model
            && entry.TimestampUtc >= r.ValidFromUtc && entry.TimestampUtc < r.ValidToUtc).ToArray();
        if (matches.Length != 1) return null;
        var p = matches[0]; var t = entry.Tokens;
        if (t.Input is null || t.Output is null || t.CacheRead is null || t.CacheWrite is null) return null;
        var uncached = t.InputSemantics == "includes-cache" ? t.Input.Value - t.CacheRead.Value - t.CacheWrite.Value
            : t.InputSemantics == "excludes-cache" ? t.Input.Value : -1;
        if (uncached < 0) return null;
        // Reasoning is a subset of output, never charged twice. These are explicit API-equivalent estimates, not bills.
        return (uncached * p.InputPerMillionUsd + t.Output.Value * p.OutputPerMillionUsd
            + t.CacheRead.Value * p.CachedInputPerMillionUsd + t.CacheWrite.Value * p.CacheWritePerMillionUsd) / 1_000_000m;
    }
}
