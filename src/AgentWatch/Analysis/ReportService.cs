using AgentWatch.Aggregation;
using AgentWatch.Collectors;
using AgentWatch.Configuration;
using AgentWatch.Models;
using AgentWatch.Storage;

namespace AgentWatch.Analysis;

public sealed record QueryPeriod(DateTimeOffset FromUtc, DateTimeOffset ToUtc);
public sealed record PeriodReport(DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset? FirstIncludedBucketUtc,
    DateTimeOffset? LastIncludedBucketEndUtc, double ObservedSeconds, int MaximumResolutionSeconds);
public sealed record Statistics(double? Average, double? Minimum, double? Maximum, double ValidSeconds);
public sealed record CpuReport(Statistics TotalPercent, Statistics UserPercent, Statistics KernelPercent, double? P95PercentUpperBound);
public sealed record MemoryReport(Statistics PhysicalTotalBytes, Statistics AvailableBytes, Statistics CommitBytes, Statistics CommitLimitBytes,
    Statistics CommitPercent, double? SecondsBelow512MiB, double? SecondsBelow256MiB);
public sealed record PagingReport(Statistics PagesInputPerSecond, Statistics PagesOutputPerSecond, Statistics PageReadsPerSecond,
    Statistics PageWritesPerSecond, double? EstimatedPageInBytes, double? EstimatedPageOutToDiskBytes, double? LowRamWithPageOutSeconds);
public sealed record SystemUsageReport(CpuReport Cpu, MemoryReport Memory, PagingReport Paging);
public sealed record AgentUsageReport(double? PresenceSeconds, Statistics ProcessCount, Statistics CpuPercent,
    Statistics WorkingSetBytes, Statistics PrivateBytes, double? LogicalProcessReadBytes, double? LogicalProcessWriteBytes,
    double? LogicalProcessOtherBytes, double LogicalIoValidSeconds, double? ReadOperations, double? WriteOperations,
    double? OtherOperations, double? ObservedStarts, double? ObservedExits, Statistics InaccessibleCandidateCount);
public sealed record NvmePoint(DateTimeOffset TimestampUtc, int PhysicalDriveNumber, double? TemperatureC, byte CriticalWarning,
    byte AvailableSparePercent, byte AvailableSpareThresholdPercent, byte PercentageUsed, string DataUnitsWritten,
    string NvmeHostWriteBytesLifetime, string NvmeHostReadBytesLifetime, string PowerOnHours, string PowerCycles,
    string UnsafeShutdowns, string MediaErrors, string ErrorInfoLogEntries);
public sealed record HostWriteInterval(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string? NvmeHostWriteDeltaBytes, bool ApproximatelyFullDay);
public sealed record NvmeUsageReport(NvmePoint First, NvmePoint Last, string? ObservedNvmeHostWriteDeltaBytes,
    string? ObservedNvmeHostReadDeltaBytes, double CounterCoverageSeconds, int CounterRegressions, int Samples,
    double? AverageTemperatureC, double? MaximumTemperatureC, double? EstimatedHostWriteBytesPerDay,
    IReadOnlyList<HostWriteInterval> DailyIntervals, double? MeanCompleteDayBytes, double? MedianCompleteDayBytes,
    double? P95CompleteDayBytes, HostWriteInterval? HighestCompleteDay, bool BaselineLearning);
public sealed record DiskUsageReport(string PhysicalDiskScope, double? PhysicalDiskReadBytes, double? PhysicalDiskWriteBytes,
    Statistics QueueLength, Statistics IdlePercent, NvmeUsageReport? Nvme);
public sealed record HotFileReport(string Name, DateTimeOffset FirstObservedUtc, DateTimeOffset LastObservedUtc,
    long? FirstSizeBytes, long? LastSizeBytes, long? MaximumSizeBytes, long ObservedPositiveSizeGrowthBytes,
    int Observations, int MissingObservations, int Errors);
public sealed record SelfUsageReport(Statistics CpuPercent, Statistics WorkingSetBytes, Statistics PrivateBytes,
    double? LogicalProcessReadBytes, double? LogicalProcessWriteBytes, double? ProjectedLogicalWriteBytesPerDay,
    Statistics DatabaseBytes, Statistics WalBytes, Statistics CollectionMilliseconds,
    double? CollectionP50MillisecondsUpperBound, double? CollectionP95MillisecondsUpperBound, Statistics FlushMilliseconds,
    double? CollectorErrors, double? MissedSamples, double? Samples);
public sealed record AnomalyReport(DateTimeOffset StartUtc, DateTimeOffset LastObservedUtc, string Type, string Severity,
    AgentKind? Agent, string Summary, IReadOnlyDictionary<string, double> Evidence, bool OpenAtLastObservation);
public sealed record DailyUsageReport(DateTimeOffset DayUtc, double ObservedSeconds, double? AverageCpuPercent,
    double? EstimatedPageOutToDiskBytes, double? PhysicalDiskWriteBytes, double? CodexLogicalWriteBytes, double? ClaudeLogicalWriteBytes);
public sealed record CorrelationReport(double? LowRamPageOutAndDiskWriteSeconds, double CodexHighLogicalWritesWithWalGrowthSeconds);
public sealed record WatchReport(int SchemaVersion, string AppVersion, PeriodReport Period, SystemUsageReport System,
    IReadOnlyDictionary<string, AgentUsageReport> Agents, DiskUsageReport Storage, IReadOnlyList<HotFileReport> HotFiles,
    IReadOnlyList<AnomalyReport> Anomalies, SelfUsageReport AgentWatch, IReadOnlyList<DailyUsageReport> Daily,
    CorrelationReport Correlations, IReadOnlyList<string> Interpretation, IReadOnlyList<string> Limitations);

public interface IReportQueryService
{
    WatchReport GetReport(QueryPeriod period, CancellationToken cancellationToken = default);
    SystemUsageReport GetSystemUsage(QueryPeriod period);
    IReadOnlyDictionary<string, AgentUsageReport> GetAgentUsage(QueryPeriod period);
    DiskUsageReport GetDiskUsage(QueryPeriod period);
    IReadOnlyList<HotFileReport> GetHotFiles(QueryPeriod period);
    IReadOnlyList<AnomalyReport> GetAnomalies(QueryPeriod period);
    SelfUsageReport GetMonitorHealth(QueryPeriod period);
}

public sealed class ReportService(string databasePath, ThresholdConfig? thresholds = null, int physicalDriveNumber = 0) : IReportQueryService
{
    public WatchReport GetReport(QueryPeriod period, CancellationToken cancellationToken = default)
    {
        if (period.FromUtc >= period.ToUtc) throw new ArgumentException("Report start must precede end.");
        using var connection = AgentWatchDatabase.OpenReadOnly(databasePath);
        using var transaction = connection.BeginTransaction(deferred: true);
        var total = new AggregateFrame(0); var daily = new SortedDictionary<long, AggregateFrame>();
        var files = new Dictionary<string, HotFileWindow>(); var nvmeDays = new SortedDictionary<long, NvmeWindow>();
        NvmeWindow? nvmeTotal = null; AggregateFrame? currentFrame = null;
        long? first = null, last = null; var resolution = 60; var codexWalSeconds = 0.0;
        foreach (var row in HistoryReader.Read(connection, period.FromUtc, period.ToUtc, transaction: transaction))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (row.Kind)
            {
                case 0:
                    var frame = AggregateCodec.Decode(row.BucketUnixSeconds, row.ResolutionSeconds, row.Payload);
                    total.Merge(frame); currentFrame = frame;
                    first = Math.Min(first ?? row.BucketUnixSeconds, row.BucketUnixSeconds);
                    last = Math.Max(last ?? 0, row.BucketUnixSeconds + row.ResolutionSeconds);
                    resolution = Math.Max(resolution, row.ResolutionSeconds);
                    var day = SampleAggregator.Floor(row.BucketUnixSeconds, 86400);
                    if (!daily.TryGetValue(day, out var dayFrame)) daily[day] = dayFrame = new(day, 86400);
                    dayFrame.Merge(frame); break;
                case 1:
                    var file = WindowCodec.Decode<HotFileWindow>(row.Payload);
                    if (files.TryGetValue(file.Name, out var existing)) existing.Merge(file); else files[file.Name] = file;
                    if (file.Name == "logs_2.sqlite-wal" && file.PositiveGrowthBytes > 0 && currentFrame is not null
                        && currentFrame.BucketUnixSeconds == row.BucketUnixSeconds && currentFrame.ResolutionSeconds == row.ResolutionSeconds
                        && currentFrame[Metric.CodexLogicalWrite].Average * 60 / 1048576 > (thresholds?.CodexWriteWarningMiBPerMinute ?? 100))
                        codexWalSeconds += currentFrame[Metric.Coverage].Sum;
                    break;
                case 2:
                    var nvme = WindowCodec.Decode<NvmeWindow>(row.Payload);
                    if (nvme.First.PhysicalDriveNumber != physicalDriveNumber) break;
                    if (nvmeTotal is null) nvmeTotal = WindowCodec.Decode<NvmeWindow>(row.Payload); else nvmeTotal.Merge(nvme);
                    var nvmeDay = SampleAggregator.Floor(row.BucketUnixSeconds, 86400);
                    if (nvmeDays.TryGetValue(nvmeDay, out var priorDay)) priorDay.Merge(nvme); else nvmeDays[nvmeDay] = nvme;
                    break;
            }
        }
        var anomalyEvents = HistoryReader.Anomalies(connection, period.FromUtc, period.ToUtc, transaction);
        transaction.Commit();
        var p = new PeriodReport(period.FromUtc, period.ToUtc, first.HasValue ? DateTimeOffset.FromUnixTimeSeconds(first.Value) : null,
            last.HasValue ? DateTimeOffset.FromUnixTimeSeconds(last.Value) : null, total[Metric.Coverage].Sum, resolution);
        var system = BuildSystemUsage(total); var agents = new Dictionary<string, AgentUsageReport> { ["codex"] = Agent(total, Metric.CodexActive), ["claude"] = Agent(total, Metric.ClaudeActive) };
        var disk = new DiskUsageReport("PhysicalDisk(_Total): all Windows physical disks", Sum(total, Metric.PhysicalDiskRead), Sum(total, Metric.PhysicalDiskWrite),
            Stats(total, Metric.DiskQueue), Stats(total, Metric.DiskIdle), Nvme(nvmeTotal, nvmeDays));
        var limitations = new List<string>
        {
            "Process logical I/O, Windows physical-disk throughput and NVMe host-write counters are different measurement layers and are not equivalent. NVMe host writes are not NAND writes.",
            "Polling misses short-lived processes and final I/O after a process's last observation. First observations are baselines. Agent presence is not proof of active work. Process starts/exits are observed appearances/disappearances.",
            "Pages Input/Output estimate paging to/from disk, including file-backed pages; pagefile-only bytes cannot be isolated. Page Reads/Writes count I/O operations, not pages.",
            "Observed time excludes monitoring gaps, sleep and service downtime. Rates reset after gaps. Reports normally lag the service by up to one persistence batch (default five minutes).",
            "Report boundaries include whole overlapping stored buckets; see effective bounds and maximum resolution. CPU and collection P50/P95 are histogram upper bounds, not exact quantiles.",
            "Summed process working sets can double-count shared memory. Inaccessible processes and incomplete baselines make logical I/O a lower bound.",
            "NVMe deltas cover the timestamps of observed health endpoints, including intervening sleep/service downtime. Counter regressions are excluded. Replacing a drive with higher counters at the same number cannot be detected without a persistent unique identifier.",
            "Daily NVMe intervals end at each day's last observation. Use their actual endpoint timestamps; incomplete days are excluded from daily baseline statistics.",
            "Positive file-size growth is metadata, not bytes written. Correlation is temporal overlap and does not establish causation. CPU temperature and fan speed are not measured."
        };
        if (anomalyEvents.Count == 10000) limitations.Add("Anomaly details are capped at 10,000 events for one query; request a shorter period for full detail.");
        if (p.ObservedSeconds == 0) limitations.Add("No valid fast observations were found in this period; unavailable metrics remain null.");
        return new(1, "0.1.0", p, system, agents, disk,
            files.Values.Select(f => new HotFileReport(f.Name, f.FirstUtc, f.LastUtc, f.FirstSizeBytes, f.LastSizeBytes, f.MaximumSizeBytes, f.PositiveGrowthBytes, f.Observations, f.MissingObservations, f.Errors)).ToArray(),
            anomalyEvents.Select(a => new AnomalyReport(a.StartUtc, a.EndUtc, a.Type, a.Severity, a.Agent, a.Summary, a.Evidence, a.Open)).ToArray(),
            Self(total), daily.Select(d => new DailyUsageReport(DateTimeOffset.FromUnixTimeSeconds(d.Key), d.Value[Metric.Coverage].Sum,
                d.Value[Metric.CpuTotal].Average, Sum(d.Value, Metric.EstimatedPageOut), Sum(d.Value, Metric.PhysicalDiskWrite),
                Sum(d.Value, Metric.CodexLogicalWrite), Sum(d.Value, Metric.ClaudeLogicalWrite))).ToArray(),
            new(Sum(total, Metric.PressurePageOutDiskOverlap), codexWalSeconds), Interpret(total, agents, disk), limitations);
    }
    private static Statistics Stats(AggregateFrame f, Metric m)
    { var a = f[m]; return new(a.Average, a.Weight > 0 ? a.Minimum : null, a.Weight > 0 ? a.Maximum : null, a.Weight); }
    private static double? Sum(AggregateFrame f, Metric m) => f[m].Weight > 0 ? f[m].Sum : null;
    private static SystemUsageReport BuildSystemUsage(AggregateFrame f) => new(
        new(Stats(f, Metric.CpuTotal), Stats(f, Metric.CpuUser), Stats(f, Metric.CpuKernel),
            AggregateFrame.PercentileUpperBound(f.CpuHistogram, Enumerable.Range(0, 21).Select(i => i * 5.0).ToArray(), 0.95)),
        new(Stats(f, Metric.PhysicalTotal), Stats(f, Metric.AvailableMemory), Stats(f, Metric.CommitBytes), Stats(f, Metric.CommitLimit), Stats(f, Metric.CommitPercent),
            Sum(f, Metric.MemoryBelow512), Sum(f, Metric.MemoryBelow256)),
        new(Stats(f, Metric.PagesInput), Stats(f, Metric.PagesOutput), Stats(f, Metric.PageReads), Stats(f, Metric.PageWrites),
            Sum(f, Metric.EstimatedPageIn), Sum(f, Metric.EstimatedPageOut), Sum(f, Metric.PressureWithPageOut)));
    private static AgentUsageReport Agent(AggregateFrame f, Metric start)
    {
        Metric M(int i) => (Metric)((int)start + i);
        return new(Sum(f, M(0)), Stats(f, M(1)), Stats(f, M(2)), Stats(f, M(3)), Stats(f, M(4)),
            Sum(f, M(5)), Sum(f, M(6)), Sum(f, M(7)), f[M(6)].Weight, Sum(f, M(8)), Sum(f, M(9)), Sum(f, M(10)), Sum(f, M(11)), Sum(f, M(12)), Stats(f, M(13)));
    }
    private static SelfUsageReport Self(AggregateFrame f) => new(Stats(f, Metric.SelfCpu), Stats(f, Metric.SelfWorkingSet), Stats(f, Metric.SelfPrivate),
        Sum(f, Metric.SelfLogicalRead), Sum(f, Metric.SelfLogicalWrite), f[Metric.SelfLogicalWrite].Average * 86400,
        Stats(f, Metric.DatabaseBytes), Stats(f, Metric.WalBytes), Stats(f, Metric.CollectionMs),
        LatencyPercentile(f, 0.5),
        LatencyPercentile(f, 0.95), Stats(f, Metric.FlushMs),
        Sum(f, Metric.CollectorErrors), Sum(f, Metric.MissedSamples), Sum(f, Metric.SampleCount));
    private static double? LatencyPercentile(AggregateFrame f, double percentile)
    {
        var upper = AggregateFrame.PercentileUpperBound(f.LatencyHistogram, AggregateFrame.LatencyBounds, percentile);
        return upper.HasValue ? Math.Min(upper.Value, f[Metric.CollectionMs].Maximum) : null;
    }
    private static NvmePoint Point(NvmeHealth n) => new(n.TimestampUtc, n.PhysicalDriveNumber, n.TemperatureC, n.CriticalWarning,
        n.AvailableSparePercent, n.AvailableSpareThresholdPercent, n.PercentageUsed, n.DataUnitsWritten, n.NvmeHostWriteBytesLifetime,
        n.NvmeHostReadBytesLifetime, n.PowerOnHours, n.PowerCycles, n.UnsafeShutdowns, n.MediaErrors, n.ErrorInfoLogEntries);
    private static NvmeUsageReport? Nvme(NvmeWindow? total, SortedDictionary<long, NvmeWindow> days)
    {
        if (total is null) return null;
        var intervals = new List<HostWriteInterval>(); NvmeHealth? previous = null;
        foreach (var day in days.Values)
        {
            var first = previous ?? day.First; var last = day.Last;
            var delta = NvmeWindow.CounterDelta(first.NvmeHostWriteBytesLifetime, last.NvmeHostWriteBytesLifetime);
            var hours = (last.TimestampUtc - first.TimestampUtc).TotalHours;
            var valid = delta is not null && day.CounterRegressions == 0
                && NvmeWindow.CounterDelta(first.NvmeHostReadBytesLifetime, last.NvmeHostReadBytesLifetime) is not null
                && NvmeWindow.CounterDelta(first.PowerOnHours, last.PowerOnHours) is not null;
            intervals.Add(new(first.TimestampUtc, last.TimestampUtc, hours > 0 && valid ? delta?.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                hours is >= 22 and <= 26 && valid));
            previous = day.Last;
        }
        var complete = intervals.Where(d => d.ApproximatelyFullDay && d.NvmeHostWriteDeltaBytes is not null).ToArray();
        var values = complete.Select(d => double.Parse(d.NvmeHostWriteDeltaBytes!, System.Globalization.CultureInfo.InvariantCulture)).Order().ToArray();
        double? percentile(double p) => values.Length == 0 ? null : values[Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1)];
        var maxDay = complete.OrderByDescending(d => double.Parse(d.NvmeHostWriteDeltaBytes!, System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
        return new(Point(total.First), Point(total.Last), total.CounterCoverageSeconds > 0 ? total.ObservedHostWriteDeltaBytes : null,
            total.CounterCoverageSeconds > 0 ? total.ObservedHostReadDeltaBytes : null, total.CounterCoverageSeconds, total.CounterRegressions, total.Samples,
            total.TemperatureSamples > 0 ? total.TemperatureSum / total.TemperatureSamples : null, total.MaximumTemperatureC,
            total.CounterCoverageSeconds > 0 ? double.Parse(total.ObservedHostWriteDeltaBytes, System.Globalization.CultureInfo.InvariantCulture) / total.CounterCoverageSeconds * 86400 : null,
            intervals, values.Length > 0 ? values.Average() : null, (values.Length == 0 ? null : (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2), percentile(0.95), maxDay, complete.Length < 7);
    }
    private static string[] Interpret(AggregateFrame f, IReadOnlyDictionary<string, AgentUsageReport> agents, DiskUsageReport disk)
    {
        var result = new List<string>(); var coverage = f[Metric.Coverage].Sum;
        if (coverage <= 0) return ["No fast-sample evidence is available for an interpretation."];
        var memoryCoverage = f[Metric.MemoryBelow512].Weight;
        var pressure = memoryCoverage > 0 ? f[Metric.MemoryBelow512].Sum / memoryCoverage : 0;
        result.Add(memoryCoverage > 0 ? $"Available RAM was below 512 MiB for {pressure:P1} of valid memory-observation time." : "RAM pressure cannot be assessed: no valid memory measurements are available.");
        if (pressure > 0.2 && f[Metric.EstimatedPageOut].Sum > 0)
            result.Add("Sustained memory pressure and page-out are present; they are plausible contributors to disk activity, not proof of fan or SSD-wear causation.");
        else if (f[Metric.CpuTotal].Average > 70)
            result.Add("High sustained CPU utilization is present during the observed window.");
        else result.Add("These measurements do not establish a single dominant bottleneck; compare heavy days and anomaly windows after collecting a longer baseline.");
        var codex = agents["codex"].LogicalProcessWriteBytes ?? 0; var claude = agents["claude"].LogicalProcessWriteBytes ?? 0;
        if (codex + claude > 0) result.Add($"Codex contributed {codex / (codex + claude):P1} of observed logical AI-agent writes.");
        if (disk.Nvme?.BaselineLearning is true) result.Add("Fewer than seven complete NVMe day intervals are available; the endurance baseline is still learning.");
        return result.ToArray();
    }
    public SystemUsageReport GetSystemUsage(QueryPeriod period) => GetReport(period).System;
    public IReadOnlyDictionary<string, AgentUsageReport> GetAgentUsage(QueryPeriod period) => GetReport(period).Agents;
    public DiskUsageReport GetDiskUsage(QueryPeriod period) => GetReport(period).Storage;
    public IReadOnlyList<HotFileReport> GetHotFiles(QueryPeriod period) => GetReport(period).HotFiles;
    public IReadOnlyList<AnomalyReport> GetAnomalies(QueryPeriod period) => GetReport(period).Anomalies;
    public SelfUsageReport GetMonitorHealth(QueryPeriod period) => GetReport(period).AgentWatch;
}
