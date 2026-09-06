using AgentWatch.Collectors;
using AgentWatch.Configuration;
using AgentWatch.Models;
using AgentWatch.Storage;

namespace AgentWatch.Analysis;

public sealed class AnomalyDetector(ThresholdConfig thresholds)
{
    private sealed class Condition
    {
        public DateTimeOffset StartUtc;
        public double Seconds, Peak;
        public AnomalyEvent? Event;
    }
    private readonly Dictionary<string, Condition> conditions = new();
    private readonly Dictionary<string, AnomalyEvent> pending = new();
    private readonly Dictionary<string, Queue<(DateTimeOffset Time, long Growth)>> fileGrowth = new();
    public NvmeHealth? PreviousNvme { get; set; }
    public IReadOnlyCollection<AnomalyEvent> Active => conditions.Values.Where(c => c.Event is not null).Select(c => c.Event!).ToArray();

    public void Evaluate(FastSample s)
    {
        if (s.Gap) { CloseAll(s.TimestampUtc); return; }
        if (s.DurationSeconds <= 0) return;
        var mem = s.Memory;
        Observe("ram-pressure", mem is not null && mem.AvailableBytes < (ulong)thresholds.RamWarningMiB * 1048576UL,
            s.TimestampUtc, s.DurationSeconds, thresholds.RamSustainSeconds, "warning", null, "Sustained low available RAM.",
            mem is null ? 0 : (mem.PhysicalTotalBytes - mem.AvailableBytes) / 1048576.0, "usedMemoryMiBPeak");
        Observe("ram-pressure-pageout", mem is not null && mem.AvailableBytes < (ulong)thresholds.RamCriticalMiB * 1048576UL
                && s.Pdh?.PagesOutputPerSecond >= thresholds.CriticalPageOutputPerSecond,
            s.TimestampUtc, s.DurationSeconds, thresholds.RamSustainSeconds, "critical", null,
            "Very low RAM overlaps sustained page-out to disk; pagefile-only writes are not isolated.", s.Pdh?.PagesOutputPerSecond ?? 0, "pagesOutputPerSecondPeak");
        var codex = Array.Find(s.Agents, a => a.Kind == AgentKind.Codex);
        var rate = codex?.LogicalIo?.LogicalWriteBytes / s.DurationSeconds * 60 / 1048576;
        Observe("codex-logical-writes", rate > thresholds.CodexWriteWarningMiBPerMinute, s.TimestampUtc, s.DurationSeconds,
            thresholds.CodexWriteWarningSeconds, "warning", AgentKind.Codex, "Sustained Codex logical process write I/O.", rate ?? 0, "logicalWriteMiBPerMinutePeak");
        Observe("codex-logical-writes-high", rate > thresholds.CodexWriteCriticalMiBPerMinute, s.TimestampUtc, s.DurationSeconds,
            thresholds.CodexWriteCriticalSeconds, "critical", AgentKind.Codex, "Very high sustained Codex logical process write I/O.", rate ?? 0, "logicalWriteMiBPerMinutePeak");
        Observe("agentwatch-database-size", s.Self?.DatabaseBytes > thresholds.OwnDatabaseMiB * 1048576L, s.TimestampUtc, s.DurationSeconds,
            60, "warning", null, "AgentWatch database exceeds configured size budget.", s.Self?.DatabaseBytes ?? 0, "databaseBytesPeak");
        Observe("agentwatch-wal-size", s.Self?.WalBytes > thresholds.OwnWalMiB * 1048576L, s.TimestampUtc, s.DurationSeconds,
            60, "warning", null, "AgentWatch WAL exceeds configured size budget; a long-lived reader may prevent checkpoint progress.", s.Self?.WalBytes ?? 0, "walBytesPeak");
    }
    public void EvaluateHotFiles(IEnumerable<HotFileSample> samples)
    {
        foreach (var file in samples)
        {
            if (file.Name is not ("logs_2.sqlite" or "logs_2.sqlite-wal")) continue;
            if (!fileGrowth.TryGetValue(file.Name, out var queue)) fileGrowth[file.Name] = queue = new();
            if (queue.Count > 0 && queue.Last().Time > file.TimestampUtc) queue.Clear();
            queue.Enqueue((file.TimestampUtc, Math.Max(0, file.SizeDeltaBytes ?? 0)));
            while (queue.Count > 0 && queue.Peek().Time <= file.TimestampUtc.AddMinutes(-10)) queue.Dequeue();
            while (queue.Count > 32) queue.Dequeue();
            var growth = queue.Sum(p => (double)p.Growth);
            Observe("codex-file-growth:" + file.Name, file.Error is null && growth > thresholds.HotGrowthMiBPerTenMinutes * 1048576L,
                file.TimestampUtc, 0, 0, "warning", AgentKind.Codex, $"Rapid observed size growth of {file.Name}; size growth is not write I/O.", growth, "positiveGrowthBytesInTenMinutesPeak");
            if (file.Name.EndsWith("-wal", StringComparison.Ordinal))
                Observe("codex-large-wal", file.SizeBytes > thresholds.HotWalMiB * 1048576L, file.TimestampUtc, 0, 0,
                    "warning", AgentKind.Codex, "Codex logs WAL exceeds configured size threshold.", file.SizeBytes ?? 0, "walBytesPeak");
        }
    }
    public void EvaluateNvme(NvmeHealth sample)
    {
        Observe("ssd-temperature", sample.TemperatureC >= thresholds.SsdWarningC, sample.TimestampUtc, 0, 0,
            sample.TemperatureC >= thresholds.SsdCriticalC ? "critical" : "warning", null, "High observed SSD composite temperature.", sample.TemperatureC ?? 0, "temperatureCPeak");
        Observe("nvme-critical-warning", sample.CriticalWarning != 0, sample.TimestampUtc, 0, 0,
            "critical", null, "NVMe Critical Warning is nonzero.", sample.CriticalWarning, "criticalWarningBits");
        var previous = PreviousNvme;
        if (previous is not null && previous.PhysicalDriveNumber == sample.PhysicalDriveNumber)
        {
            void Increased(string type, string before, string after, string severity)
            {
                var delta = NvmeWindow.CounterDelta(before, after);
                if (delta is null) Event(sample.TimestampUtc, type + "-reset", "warning", "NVMe lifetime counter regressed; device replacement or counter reset may have occurred.", new());
                else if (delta > 0) Event(sample.TimestampUtc, type, severity, "NVMe health counter increased.", new() { ["increase"] = (double)delta.Value });
            }
            Increased("nvme-media-errors", previous.MediaErrors, sample.MediaErrors, "critical");
            Increased("nvme-unsafe-shutdowns", previous.UnsafeShutdowns, sample.UnsafeShutdowns, "info");
            Increased("nvme-error-log", previous.ErrorInfoLogEntries, sample.ErrorInfoLogEntries, "warning");
            if (sample.PercentageUsed > previous.PercentageUsed) Event(sample.TimestampUtc, "nvme-percentage-used", "info", "NVMe Percentage Used increased.", new() { ["from"] = previous.PercentageUsed, ["to"] = sample.PercentageUsed });
            if (sample.AvailableSparePercent < previous.AvailableSparePercent) Event(sample.TimestampUtc, "nvme-spare-decrease", "warning", "NVMe Available Spare decreased.", new() { ["from"] = previous.AvailableSparePercent, ["to"] = sample.AvailableSparePercent });
            if (NvmeWindow.CounterDelta(previous.DataUnitsWritten, sample.DataUnitsWritten) is null)
                Event(sample.TimestampUtc, "nvme-host-counter-reset", "warning", "NVMe host-write counter regressed; the interval delta is unavailable.", new());
        }
        PreviousNvme = sample;
    }
    private void Observe(string type, bool condition, DateTimeOffset now, double seconds, double requiredSeconds, string severity,
        AgentKind? agent, string summary, double evidence, string evidenceKey)
    {
        if (!condition)
        {
            if (conditions.Remove(type, out var old) && old.Event is { } ended)
                pending[ended.Id] = ended with { Open = false }; // End is the last confirmed positive observation.
            return;
        }
        if (!conditions.TryGetValue(type, out var state)) conditions[type] = state = new() { StartUtc = now.AddSeconds(-seconds) };
        state.Seconds += seconds; state.Peak = Math.Max(state.Peak, evidence);
        if (state.Seconds + 0.0001 < requiredSeconds) return;
        var updated = new AnomalyEvent(state.Event?.Id ?? Guid.NewGuid().ToString("N"), state.StartUtc, now, type, severity, agent,
            summary, new() { [evidenceKey] = state.Peak, ["observedSeconds"] = state.Seconds, ["requiredSeconds"] = requiredSeconds }, true);
        state.Event = updated; pending[updated.Id] = updated;
    }
    public void Event(DateTimeOffset now, string type, string severity, string summary, Dictionary<string, double> evidence)
    {
        var e = new AnomalyEvent(Guid.NewGuid().ToString("N"), now, now, type, severity, null, summary, evidence, false); pending[e.Id] = e;
    }
    public void CloseAll(DateTimeOffset now)
    {
        foreach (var state in conditions.Values) if (state.Event is { } e) pending[e.Id] = e with { Open = false };
        conditions.Clear(); fileGrowth.Clear();
    }
    public List<AnomalyEvent> Drain()
    {
        var result = pending.Values.ToList(); pending.Clear(); return result;
    }
}
