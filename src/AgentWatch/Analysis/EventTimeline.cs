using System.Globalization;
using AgentWatch.Aggregation;
using AgentWatch.Analysis.Usage;

namespace AgentWatch.Analysis;

public sealed record TimelineEvent(string Id, string Type, DateTimeOffset TimestampUtc, DateTimeOffset? EndUtc,
    string Source, string? Agent, string? SessionId, string? RunId, string Summary, IReadOnlyDictionary<string, string> Evidence);
public sealed record EventReport(int SchemaVersion, QueryPeriod Period, IReadOnlyList<TimelineEvent> Facts, bool Truncated);
public interface IEventQueryService
{
    EventReport GetEvents(QueryPeriod period, string? marksDirectory = null, string? usageDirectory = null, CancellationToken cancellationToken = default);
}
public sealed partial class ReportService : IEventQueryService
{
    public EventReport GetEvents(QueryPeriod period, string? marksDirectory = null, string? usageDirectory = null, CancellationToken cancellationToken = default)
    {
        var report = GetWeeklyReport(period, marksDirectory, cancellationToken, usageDirectory);
        return new(1, period, report.Events, report.EventsTruncated);
    }
}

public static class EventTimeline
{
    public static EventReport Build(WeeklyReport report)
    {
        var period = new QueryPeriod(report.Measurements.Period.FromUtc, report.Measurements.Period.ToUtc);
        var events = new List<TimelineEvent>();
        foreach (var a in report.Measurements.Anomalies)
        {
            var type = a.Type is "monitoring-gap" or "service-restart-gap" or "history-dropped" ? a.Type : "anomaly." + a.Type;
            events.Add(new(UsageAdapters.StableId($"anomaly|{a.Type}|{a.Agent}|{a.StartUtc:O}"), type, a.StartUtc,
                a.LastObservedUtc, "monitor.history", a.Agent?.ToString().ToLowerInvariant(), null, null,
                a.Summary, a.Evidence.ToDictionary(e => e.Key, e => e.Value.ToString("R", CultureInfo.InvariantCulture)).Append(new KeyValuePair<string, string>("openAtLastObservation", a.OpenAtLastObservation.ToString())).ToDictionary(e => e.Key, e => e.Value)));
        }
        foreach (var mark in report.UserObservations.Markers)
            events.Add(new("mark:" + mark.Id, "user.mark", mark.TimestampUtc, null, "user.explicit", null, null, null, mark.Note,
                new Dictionary<string, string> { ["recordedUtc"] = mark.RecordedUtc.ToString("O") }));
        foreach (var run in report.RunHistory.Runs)
        {
            events.Add(new("run:" + run.Id + ":start", "service.run.start", run.StartedUtc, null, "monitor.run-manifest", null, null, run.Id,
                "Run started", new Dictionary<string, string> { ["version"] = run.AppVersion, ["mode"] = run.Mode }));
            if (run.EndedUtc is { } end) events.Add(new("run:" + run.Id + ":end", "service.run.end", end, null, "monitor.run-manifest", null, null, run.Id,
                "Run outcome: " + run.Outcome, new Dictionary<string, string> { ["outcome"] = run.Outcome, ["logFailures"] = run.LogFailures.ToString(CultureInfo.InvariantCulture) }));
        }
        foreach (var activity in report.SessionUsage.Activities)
            events.Add(new(activity.Id, activity.Type, activity.TimestampUtc, null, "usage.import", activity.Agent, activity.SessionId, null,
                activity.Type, new Dictionary<string, string> { ["sourceFileId"] = activity.SourceFileId,
                    ["sourceOffset"] = activity.SourceOffset.ToString(CultureInfo.InvariantCulture), ["turnId"] = activity.TurnId ?? "unknown" }));
        foreach (var usage in report.SessionUsage.Usage)
            events.Add(new("usage:" + usage.Id, "session.usage", usage.TimestampUtc, null, usage.SourceKind, usage.Agent, usage.SessionId, null,
                "Observed usage record", new Dictionary<string, string> { ["usageId"] = usage.Id, ["model"] = usage.Model ?? "unknown",
                    ["reasoningEffort"] = usage.ReasoningEffort ?? "unknown", ["sourceFileId"] = usage.SourceFileId,
                    ["sourceOffset"] = usage.SourceOffset.ToString(CultureInfo.InvariantCulture) }));
        var matching = events.Where(e => e.TimestampUtc < period.ToUtc && (e.EndUtc ?? e.TimestampUtc) >= period.FromUtc)
            .OrderBy(e => e.TimestampUtc).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        return new(1, period, matching.Take(20000).ToArray(), matching.Length > 20000 || report.SessionUsage.Truncated
            || report.RunHistory.Truncated || report.UserObservations.Truncated || report.Measurements.Anomalies.Count >= 10000);
    }
}

public sealed record ContextAgent(double? AverageCpuPercent, double? AverageWorkingSetBytes, double? MaximumPrivateBytes,
    double? LogicalReadBytes, double? LogicalWriteBytes, double? PresenceSeconds);
public sealed record TemporalContext(string SubjectId, string SubjectType, DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    DateTimeOffset? FirstIncludedBucketUtc, DateTimeOffset? LastIncludedBucketEndUtc, int MaximumResolutionSeconds,
    double ObservedSeconds, Statistics CpuPercent, Statistics AvailableMemoryBytes, double? EstimatedPageOutBytes,
    double? PhysicalDiskWriteBytes, IReadOnlyDictionary<string, ContextAgent> Agents, IReadOnlyList<string> OverlappingSessions,
    string Attribution);

internal sealed class ContextWindow(string id, string type, DateTimeOffset from, DateTimeOffset to)
{
    public DateTimeOffset From { get; } = from;
    public DateTimeOffset To { get; } = to;
    private readonly AggregateFrame total = new(0);
    private long? first, last;
    private int resolution = 60;
    public void Observe(AggregateFrame frame)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(frame.BucketUnixSeconds);
        if (start >= To || start.AddSeconds(frame.ResolutionSeconds) <= From) return;
        total.Merge(frame); first = Math.Min(first ?? frame.BucketUnixSeconds, frame.BucketUnixSeconds);
        last = Math.Max(last ?? long.MinValue, frame.BucketUnixSeconds + frame.ResolutionSeconds);
        resolution = Math.Max(resolution, frame.ResolutionSeconds);
    }
    public TemporalContext Build(UsageInventory inventory)
    {
        double? Sum(Metric m) => total[m].Weight > 0 ? total[m].Sum : null;
        Statistics Stats(Metric m) => new(total[m].Average, total[m].Weight > 0 ? total[m].Minimum : null,
            total[m].Weight > 0 ? total[m].Maximum : null, total[m].Weight);
        ContextAgent Agent(Metric start)
        {
            Metric M(int offset) => (Metric)((int)start + offset);
            return new(total[M(2)].Average, total[M(3)].Average, total[M(4)].Weight > 0 ? total[M(4)].Maximum : null,
                Sum(M(5)), Sum(M(6)), Sum(M(0)));
        }
        return new(id, type, From, To, first.HasValue ? DateTimeOffset.FromUnixTimeSeconds(first.Value) : null,
            last.HasValue ? DateTimeOffset.FromUnixTimeSeconds(last.Value) : null, resolution, total[Metric.Coverage].Sum,
            Stats(Metric.CpuTotal), Stats(Metric.AvailableMemory), Sum(Metric.EstimatedPageOut), Sum(Metric.PhysicalDiskWrite),
            new Dictionary<string, ContextAgent> { ["codex"] = Agent(Metric.CodexActive), ["claude"] = Agent(Metric.ClaudeActive) },
            inventory.Sessions.Where(s => s.FirstObservedUtc < To && s.LastActivityUtc >= From)
                .Select(s => s.Agent + ":" + s.SessionId).ToArray(),
            "Whole overlapping buckets; system totals and all observed processes of each agent kind. Temporal overlap only, not resource attribution to an individual session.");
    }
}
