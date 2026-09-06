using System.Diagnostics;
using System.Security.Principal;
using AgentWatch.Attribution;
using AgentWatch.Configuration;
using AgentWatch.Hosting;
using AgentWatch.Models;

namespace AgentWatch.Collectors;

public readonly record struct SampleInterval(double Seconds, double WallSeconds, bool Gap, long MissedSamples);
public sealed class SampleClock(double expectedSeconds)
{
    private long? previousTick;
    private DateTimeOffset previousUtc;
    public SampleInterval Advance(DateTimeOffset utc, long tick)
    {
        if (previousTick is null) { previousTick = tick; previousUtc = utc; return new(0, 0, false, 0); }
        var seconds = Stopwatch.GetElapsedTime(previousTick.Value, tick).TotalSeconds;
        var wall = (utc - previousUtc).TotalSeconds;
        previousTick = tick; previousUtc = utc;
        var gap = seconds <= 0 || seconds > expectedSeconds * 2.5 || Math.Abs(wall - seconds) > Math.Max(2, expectedSeconds * 0.5);
        return new(seconds, wall, gap, gap ? Math.Max(1, (long)(Math.Max(seconds, wall) / expectedSeconds) - 1) : 0);
    }
}

public sealed record SnapshotResult(string Version, DateTimeOffset TimestampUtc, string? MonitoredUserProfile,
    FastSample Current, IReadOnlyList<ProcessReading> AgentProcesses, IReadOnlyList<HotFileSample> HotFiles,
    NvmeHealth? Nvme, IReadOnlyCollection<CollectorHealth> Collectors);

public sealed class CollectorEngine : IDisposable
{
    private readonly string dataDirectory;
    private readonly SystemCollector system = new();
    private readonly ProcessCollector processes = new();
    private readonly HotFileCollector hotFiles;
    private readonly NvmeHealthCollector nvme;
    private readonly SampleClock clock;
    private PdhCollector? pdh;
    private long lastPdhInit;
    private bool processReset = true, cpuReset = true;
    private ProcessReading? previousSelf;
    private long lastErrors;
    private Task<NvmeHealth>? nvmeTask;
    private long nvmeStarted;
    private bool nvmeSlowReported;
    public CollectorHealthBook Health { get; }
    public string? Profile { get; }
    public SampleInterval LastInterval { get; private set; }
    public double LastFlushMilliseconds { get; set; }
    public CollectorEngine(WatchConfig config, string dataDirectory, LocalLog? log = null)
    {
        this.dataDirectory = dataDirectory;
        using var identity = WindowsIdentity.GetCurrent();
        Profile = config.MonitoredUserProfile ?? (identity.IsSystem ? null : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        hotFiles = new(Profile); nvme = new(config.PhysicalDriveNumber); clock = new(config.Sampling.FastSampleSeconds); Health = new(log);
    }
    public FastSample Collect()
    {
        var started = Stopwatch.GetTimestamp(); var utc = DateTimeOffset.UtcNow;
        LastInterval = clock.Advance(utc, started); var reset = LastInterval.Gap;
        var cpu = Health.Read("SystemCpu", () => system.ReadCpu(reset || cpuReset) ?? new CpuSample(double.NaN, double.NaN, double.NaN));
        cpuReset = cpu is null;
        if (cpu is not null && !double.IsFinite(cpu.TotalPercent)) cpu = null;
        var memory = Health.Read("Memory", SystemCollector.ReadMemory);
        if (pdh is null && (lastPdhInit == 0 || Stopwatch.GetElapsedTime(lastPdhInit).TotalMinutes >= 5))
        { lastPdhInit = started; pdh = Health.Read("PagingDisk", () => new PdhCollector()); }
        var paging = pdh is null ? null : Health.Read("PagingDisk", () => pdh.Read(reset),
            _ => pdh.Errors.Count == 0 ? null : string.Join("; ", pdh.Errors.Select(p => p.Key + ": " + p.Value)));
        var agents = Health.Read("Processes", () => processes.Collect(LastInterval.Seconds, reset || processReset),
            _ => processes.InaccessibleCount == 0 ? null : $"{processes.InaccessibleCount} candidate processes exited or were inaccessible; attribution is incomplete.");
        processReset = agents is null;
        var selfReading = ProcessCollector.Read(new((uint)Environment.ProcessId, 0, "AgentWatch.exe"));
        var errors = Health.ErrorCount; var deltaErrors = Math.Max(0, errors - lastErrors); lastErrors = errors;
        SelfSample? self = null;
        if (selfReading is not null)
        {
            var delta = ProcessTracker.CalculateDelta(reset ? null : previousSelf, selfReading, LastInterval.Seconds, Environment.ProcessorCount);
            self = new(delta.HasBaseline ? delta.CpuPercent : null, selfReading.WorkingSetBytes, selfReading.PrivateBytes, delta.Io,
                FileSize(Path.Combine(dataDirectory, "agentwatch.db")), FileSize(Path.Combine(dataDirectory, "agentwatch.db-wal")),
                LastFlushMilliseconds, deltaErrors, LastInterval.MissedSamples);
        }
        previousSelf = selfReading; LastFlushMilliseconds = 0;
        return new(utc, LastInterval.Seconds, cpu, memory, paging, agents ?? [], self,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, reset);
    }
    public List<HotFileSample> CollectHotFiles()
        => Health.Read("HotFiles", () => hotFiles.Read(DateTimeOffset.UtcNow),
            files => files.Any(f => f.Error is not null) ? string.Join("; ", files.Where(f => f.Error is not null).Select(f => f.Name + ": " + f.Error)) : null) ?? [];
    public void StartNvme()
    {
        if (nvmeTask is not null) return;
        nvmeStarted = Stopwatch.GetTimestamp(); nvmeSlowReported = false;
        nvmeTask = Task.Run(nvme.Read);
    }
    public bool TryCompleteNvme(out NvmeHealth? value)
    {
        value = null;
        if (nvmeTask is null) return false;
        if (!nvmeTask.IsCompleted)
        {
            if (!nvmeSlowReported && Stopwatch.GetElapsedTime(nvmeStarted).TotalSeconds >= 30)
            { nvmeSlowReported = true; Health.Record("Nvme", "WARN", "Driver query is taking over 30 seconds; collection continues and no second query is started.", false, true); }
            return false;
        }
        var completed = nvmeTask; nvmeTask = null;
        value = Health.Read("Nvme", () => completed.GetAwaiter().GetResult()); return true;
    }
    public async Task<SnapshotResult> Snapshot(CancellationToken cancellationToken = default)
    {
        Collect(); var files = CollectHotFiles(); StartNvme();
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        var current = Collect();
        if (nvmeTask is not null)
        {
            try { await nvmeTask.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            catch (Exception e) when (e is not OperationCanceledException) { /* Result/error is recorded below. */ }
        }
        NvmeHealth? smart = null;
        if (!TryCompleteNvme(out smart)) Health.Record("Nvme", "WARN", "NVMe query did not complete within the snapshot timeout.", false, true);
        return new("0.1.0", DateTimeOffset.UtcNow, Profile, current, processes.LastReadings, files, smart, Health.States.ToArray());
    }
    public static long FileSize(string path)
    {
        try { return new FileInfo(path) is { Exists: true } info ? info.Length : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
    public void Dispose() => pdh?.Dispose();
}
