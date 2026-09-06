using AgentWatch.Windows;

namespace AgentWatch.Models;

public enum AgentKind { Other, Codex, Claude }
public readonly record struct ProcessIdentity(uint Pid, ulong CreationFileTime);
public sealed record ProcessNode(uint Pid, uint ParentPid, string Name);
public sealed record ProcessReading(ProcessIdentity Identity, uint ParentPid, string Name, ulong CpuTicks,
    IoCounters? Io, ulong? WorkingSetBytes, ulong? PrivateBytes);
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
public sealed record CpuSample(double TotalPercent, double UserPercent, double KernelPercent);
public sealed record MemorySample(ulong PhysicalTotalBytes, ulong AvailableBytes, uint MemoryLoadPercent,
    ulong CommitBytes, ulong CommitLimitBytes, ulong PageSize)
{
    public ulong UsedBytes => PhysicalTotalBytes - AvailableBytes;
    public double CommitPercent => CommitLimitBytes == 0 ? 0 : 100.0 * CommitBytes / CommitLimitBytes;
}
public sealed record PdhSample(double? PagesInputPerSecond, double? PagesOutputPerSecond,
    double? PageReadsPerSecond, double? PageWritesPerSecond, double? PhysicalDiskReadBytesPerSecond,
    double? PhysicalDiskWriteBytesPerSecond, double? PhysicalDiskQueueLength, double? PhysicalDiskIdlePercent);
public sealed record ProcessDelta(bool HasBaseline, double CpuPercent, IoCounters? Io);
public sealed record AgentSample(AgentKind Kind, int ProcessCount, double? CpuPercent,
    ulong? WorkingSetBytes, ulong? PrivateBytes, IoCounters? LogicalIo, int ObservedStarts, int ObservedExits,
    int InaccessibleProcesses);
public sealed record SelfSample(double? CpuPercent, ulong? WorkingSetBytes, ulong? PrivateBytes, IoCounters? LogicalIo,
    long DatabaseBytes, long WalBytes, double FlushMilliseconds, long CollectorErrors, long MissedSamples);
public sealed record FastSample(DateTimeOffset TimestampUtc, double DurationSeconds, CpuSample? Cpu, MemorySample? Memory,
    PdhSample? Pdh, AgentSample[] Agents, SelfSample? Self, double CollectionMilliseconds, bool Gap);
public sealed record HotFileSample(DateTimeOffset TimestampUtc, string Name, bool Exists, long? SizeBytes,
    long? SizeDeltaBytes, DateTimeOffset? LastWriteUtc, string? Error = null);
public sealed record CollectorHealth(string Name, string State, string? Detail, DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc, long Errors);
