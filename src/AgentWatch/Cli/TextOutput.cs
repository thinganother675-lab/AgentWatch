using System.Globalization;
using System.Numerics;
using AgentWatch.Analysis;
using AgentWatch.Collectors;

namespace AgentWatch.Cli;

public static class TextOutput
{
    public static string Bytes(double? value) => value is null ? "unavailable" : value >= 1e12 ? $"{value / 1e12:F3} TB" : value >= 1e9 ? $"{value / 1e9:F3} GB" : value >= 1e6 ? $"{value / 1e6:F3} MB" : $"{value:F0} B";
    private static string ExactBytes(string? value) => value is null ? "unavailable" : Bytes((double)BigInteger.Parse(value, CultureInfo.InvariantCulture)) + $" ({value} bytes)";
    private static string Number(double? value, string unit = "") => value is null ? "unavailable" : $"{value:F2}{unit}";
    private static string Hours(double? seconds) => Number(seconds / 3600, " h");
    public static void Snapshot(SnapshotResult s)
    {
        Console.WriteLine($"AgentWatch {s.Version} snapshot {s.TimestampUtc:O}");
        Console.WriteLine($"CPU: {Number(s.Current.Cpu?.TotalPercent, "%")} total; {Number(s.Current.Cpu?.UserPercent, "%")} user; {Number(s.Current.Cpu?.KernelPercent, "%")} kernel (excludes idle)");
        Console.WriteLine($"RAM: {Bytes(s.Current.Memory?.AvailableBytes)} available / {Bytes(s.Current.Memory?.PhysicalTotalBytes)} total; commit {Bytes(s.Current.Memory?.CommitBytes)} / {Bytes(s.Current.Memory?.CommitLimitBytes)}");
        Console.WriteLine($"Paging pages/sec: input {Number(s.Current.Pdh?.PagesInputPerSecond)}, output {Number(s.Current.Pdh?.PagesOutputPerSecond)}; operations/sec: reads {Number(s.Current.Pdh?.PageReadsPerSecond)}, writes {Number(s.Current.Pdh?.PageWritesPerSecond)}");
        Console.WriteLine($"Windows physical disk (_Total): read {Bytes(s.Current.Pdh?.PhysicalDiskReadBytesPerSecond)}/s, write {Bytes(s.Current.Pdh?.PhysicalDiskWriteBytesPerSecond)}/s, queue {Number(s.Current.Pdh?.PhysicalDiskQueueLength)}");
        foreach (var a in s.Current.Agents)
            Console.WriteLine($"{a.Kind}: {a.ProcessCount} observed processes, CPU {Number(a.CpuPercent, "%")}, working set {Bytes(a.WorkingSetBytes)}, private {Bytes(a.PrivateBytes)}, interval logical writes {Bytes(a.LogicalIo?.LogicalWriteBytes)}");
        foreach (var f in s.HotFiles) Console.WriteLine($"{f.Name}: {(f.Exists ? Bytes(f.SizeBytes) : "missing/unavailable")}{(f.Error is null ? "" : "; " + f.Error)}");
        if (s.Nvme is { } n) Console.WriteLine($"NVMe PhysicalDrive{n.PhysicalDriveNumber}: {Number(n.TemperatureC, " C")}, used {n.PercentageUsed}%, host writes {ExactBytes(n.NvmeHostWriteBytesLifetime)}, media errors {n.MediaErrors}, unsafe shutdowns {n.UnsafeShutdowns}");
        else Console.WriteLine("NVMe SMART: unavailable for this token/driver; see collector health.");
        foreach (var c in s.Collectors) Console.WriteLine($"{c.State} {c.Name}: {c.Detail ?? "available"}");
    }
    public static void Status(StatusResult s)
    {
        Console.WriteLine($"AgentWatch {s.AppVersion}; Windows service: {s.ServiceState}");
        Console.WriteLine($"Database: {s.DatabasePath}; {Bytes(s.DatabaseBytes)}; WAL {Bytes(s.WalBytes)}");
        Console.WriteLine($"Last persisted sample: {s.LastPersistedSampleUtc:O}; age {Number(s.PersistenceLagSeconds, " s")}; recorded state {s.RecordedInstanceState ?? "none"}");
        Console.WriteLine($"Recorded uptime: {Hours(s.RecordedUptimeSeconds)} (as of last persistence batch)");
        foreach (var c in s.Collectors) Console.WriteLine($"{c.State} {c.Name}: {c.Detail ?? "available"}; last success {c.LastSuccessUtc:O}");
        if (s.Error is not null) Console.WriteLine("ERROR: " + s.Error);
    }
    public static void Doctor(DoctorResult d)
    {
        Console.WriteLine($"AgentWatch doctor {d.TimestampUtc:O}");
        foreach (var c in d.Checks) Console.WriteLine($"{c.State,-5} {c.Name}: {c.Detail}");
        Snapshot(d.Snapshot);
    }
    public static void Report(WatchReport r, string command)
    {
        Console.WriteLine($"AgentWatch {r.AppVersion}: {r.Period.FromUtc:O} .. {r.Period.ToUtc:O}");
        Console.WriteLine($"Observed time {Hours(r.Period.ObservedSeconds)}; stored resolution up to {r.Period.MaximumResolutionSeconds}s; included buckets {r.Period.FirstIncludedBucketUtc:O} .. {r.Period.LastIncludedBucketEndUtc:O}");
        if (command == "report")
        {
            Console.WriteLine($"CPU: avg {Number(r.System.Cpu.TotalPercent.Average, "%")}, P95 <= {Number(r.System.Cpu.P95PercentUpperBound, "%")}, peak {Number(r.System.Cpu.TotalPercent.Maximum, "%")}");
            Console.WriteLine($"RAM: minimum available {Bytes(r.System.Memory.AvailableBytes.Minimum)}; below 512 MiB {Hours(r.System.Memory.SecondsBelow512MiB)}, below 256 MiB {Hours(r.System.Memory.SecondsBelow256MiB)}; commit avg {Number(r.System.Memory.CommitPercent.Average, "%")}");
            Console.WriteLine($"Paging: estimated page-out to disk {Bytes(r.System.Paging.EstimatedPageOutToDiskBytes)}, page-in {Bytes(r.System.Paging.EstimatedPageInBytes)}; pagefile-only bytes are not isolated.");
            Console.WriteLine($"Pages input/output avg per second: {Number(r.System.Paging.PagesInputPerSecond.Average)} / {Number(r.System.Paging.PagesOutputPerSecond.Average)}; page reads/writes operations: {Number(r.System.Paging.PageReadsPerSecond.Average)} / {Number(r.System.Paging.PageWritesPerSecond.Average)}");
        }
        if (command is "report" or "agents")
            foreach (var (name, a) in r.Agents)
                Console.WriteLine($"{name}: presence {Hours(a.PresenceSeconds)}, logical reads {Bytes(a.LogicalProcessReadBytes)}, logical writes {Bytes(a.LogicalProcessWriteBytes)}, CPU avg {Number(a.CpuPercent.Average, "%")}, peak private {Bytes(a.PrivateBytes.Maximum)}, peak processes {Number(a.ProcessCount.Maximum)}");
        if (command is "report" or "disk")
        {
            Console.WriteLine($"Windows physical-disk throughput: read {Bytes(r.Storage.PhysicalDiskReadBytes)}, write {Bytes(r.Storage.PhysicalDiskWriteBytes)}; {r.Storage.PhysicalDiskScope}");
            if (r.Storage.Nvme is { } n)
            {
                Console.WriteLine($"NVMe host writes: {ExactBytes(n.First.NvmeHostWriteBytesLifetime)} -> {ExactBytes(n.Last.NvmeHostWriteBytesLifetime)}");
                Console.WriteLine($"Observed NVMe host delta: {ExactBytes(n.ObservedNvmeHostWriteDeltaBytes)}; approximate/day {Bytes(n.EstimatedHostWriteBytesPerDay)}; endpoints {n.First.TimestampUtc:O} .. {n.Last.TimestampUtc:O}");
                Console.WriteLine($"SSD temperature avg {Number(n.AverageTemperatureC, " C")}, max {Number(n.MaximumTemperatureC, " C")}; used {n.First.PercentageUsed}% -> {n.Last.PercentageUsed}%; media errors {n.First.MediaErrors} -> {n.Last.MediaErrors}; unsafe shutdowns {n.First.UnsafeShutdowns} -> {n.Last.UnsafeShutdowns}");
                Console.WriteLine($"Complete-day writes: mean {Bytes(n.MeanCompleteDayBytes)}, median {Bytes(n.MedianCompleteDayBytes)}, P95 {Bytes(n.P95CompleteDayBytes)}; counter regressions {n.CounterRegressions}; baseline learning {n.BaselineLearning}");
            }
            else Console.WriteLine("NVMe: no health history in this period.");
            foreach (var f in r.HotFiles) Console.WriteLine($"{f.Name}: {Bytes(f.FirstSizeBytes)} -> {Bytes(f.LastSizeBytes)}, maximum {Bytes(f.MaximumSizeBytes)}, observed positive size growth {Bytes(f.ObservedPositiveSizeGrowthBytes)}");
        }
        if (command is "report" or "self")
        {
            var s = r.AgentWatch;
            Console.WriteLine($"AgentWatch CPU avg {Number(s.CpuPercent.Average, "%")}; working set avg/max {Bytes(s.WorkingSetBytes.Average)} / {Bytes(s.WorkingSetBytes.Maximum)}; private avg/max {Bytes(s.PrivateBytes.Average)} / {Bytes(s.PrivateBytes.Maximum)}");
            Console.WriteLine($"AgentWatch logical writes {Bytes(s.LogicalProcessWriteBytes)}; approximate projected writes/day {Bytes(s.ProjectedLogicalWriteBytesPerDay)}; DB max {Bytes(s.DatabaseBytes.Maximum)}, WAL max {Bytes(s.WalBytes.Maximum)}");
            Console.WriteLine($"Collection P50 <= {Number(s.CollectionP50MillisecondsUpperBound, " ms")}, P95 <= {Number(s.CollectionP95MillisecondsUpperBound, " ms")}, max {Number(s.CollectionMilliseconds.Maximum, " ms")}; flush max {Number(s.FlushMilliseconds.Maximum, " ms")}; errors {Number(s.CollectorErrors)}; missed samples {Number(s.MissedSamples)}");
        }
        if (command is "report" or "anomalies")
        {
            Console.WriteLine($"Anomalies: {r.Anomalies.Count} events");
            if (command == "anomalies") foreach (var a in r.Anomalies) Console.WriteLine($"{a.StartUtc:O} .. {a.LastObservedUtc:O} {a.Severity} {a.Type}: {a.Summary}; open at last observation={a.OpenAtLastObservation}");
            else foreach (var group in r.Anomalies.GroupBy(a => a.Type)) Console.WriteLine($"  {group.Key}: {group.Count()}");
        }
        if (command == "report")
        {
            foreach (var interpretation in r.Interpretation) Console.WriteLine(interpretation);
            Console.WriteLine($"Overlap: low RAM + page-out + disk writes {Hours(r.Correlations.LowRamPageOutAndDiskWriteSeconds)}; high Codex logical writes + WAL growth {Hours(r.Correlations.CodexHighLogicalWritesWithWalGrowthSeconds)}.");
            foreach (var day in r.Daily) Console.WriteLine($"UTC {day.DayUtc:yyyy-MM-dd}: observed {Hours(day.ObservedSeconds)}, CPU {Number(day.AverageCpuPercent, "%")}, Windows disk writes {Bytes(day.PhysicalDiskWriteBytes)}, Codex logical writes {Bytes(day.CodexLogicalWriteBytes)}, page-out {Bytes(day.EstimatedPageOutToDiskBytes)}");
        }
        Console.WriteLine("Caveats:"); foreach (var limitation in r.Limitations) Console.WriteLine("- " + limitation);
    }
}
