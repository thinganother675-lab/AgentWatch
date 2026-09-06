using AgentWatch.Models;
using AgentWatch.Windows;

namespace AgentWatch.Attribution;

public sealed class ProcessTracker
{
    private Dictionary<ProcessIdentity, (ProcessReading Reading, AgentKind Kind)> previous = new();
    private bool initialized;
    public IEnumerable<ProcessIdentity> KnownIdentities => previous.Keys;
    public static AgentKind RootKind(string name) => name.ToLowerInvariant() switch
    {
        "codex.exe" or "codex-code-mode-host.exe" => AgentKind.Codex,
        "claude.exe" => AgentKind.Claude,
        _ => AgentKind.Other
    };
    public Dictionary<ProcessIdentity, AgentKind> Classify(IReadOnlyList<ProcessReading> readings)
    {
        var byPid = new Dictionary<uint, ProcessReading>(readings.Count);
        foreach (var reading in readings) byPid[reading.Identity.Pid] = reading;
        var result = new Dictionary<ProcessIdentity, AgentKind>();
        AgentKind Resolve(ProcessReading reading, HashSet<uint> visited)
        {
            if (result.TryGetValue(reading.Identity, out var cached)) return cached;
            if (!visited.Add(reading.Identity.Pid)) return AgentKind.Other;
            var kind = RootKind(reading.Name);
            if (kind == AgentKind.Other && previous.TryGetValue(reading.Identity, out var old)) kind = old.Kind;
            if (kind == AgentKind.Other && byPid.TryGetValue(reading.ParentPid, out var parent)
                && parent.Identity.CreationFileTime <= reading.Identity.CreationFileTime)
                kind = Resolve(parent, visited);
            result[reading.Identity] = kind;
            return kind;
        }
        foreach (var reading in readings) Resolve(reading, new HashSet<uint>());
        return result;
    }
    public AgentSample[] Update(IReadOnlyList<ProcessReading> readings, double seconds, int logicalProcessors, bool reset = false, int inaccessible = 0)
    {
        if (reset) { previous.Clear(); initialized = false; }
        var kinds = Classify(readings);
        var next = new Dictionary<ProcessIdentity, (ProcessReading, AgentKind)>();
        var output = new AgentSample[2];
        for (var k = 0; k < 2; k++)
        {
            var kind = k == 0 ? AgentKind.Codex : AgentKind.Claude;
            var count = 0; var starts = 0; var exits = 0;
            var cpu = 0.0; ulong ws = 0, priv = 0;
            var allMemoryValid = true; var anyDelta = false; var allIoValid = true;
            var io = new IoCounters();
            foreach (var reading in readings)
            {
                if (kinds[reading.Identity] != kind) continue;
                count++;
                next[reading.Identity] = (reading, kind);
                if (reading.WorkingSetBytes is { } w && reading.PrivateBytes is { } p) { ws += w; priv += p; }
                else allMemoryValid = false;
                previous.TryGetValue(reading.Identity, out var old);
                if (old.Reading is null && initialized) starts++;
                var delta = CalculateDelta(old.Reading, reading, seconds, logicalProcessors);
                if (delta.HasBaseline) { cpu += delta.CpuPercent; anyDelta = true; }
                if (delta.Io is { } d) AddIo(ref io, d);
                else if (old.Reading is not null) allIoValid = false;
            }
            foreach (var (identity, old) in previous)
                if (old.Kind == kind && !next.ContainsKey(identity)) exits++;
            output[k] = new(kind, count, anyDelta || count == 0 ? cpu : null,
                allMemoryValid ? ws : null, allMemoryValid ? priv : null,
                allIoValid && (anyDelta || count == 0) ? io : null, starts, exits, inaccessible);
        }
        previous = next;
        initialized = true;
        return output;
    }
    public static ProcessDelta CalculateDelta(ProcessReading? previous, ProcessReading current, double seconds, int logicalProcessors)
    {
        if (previous is null || previous.Identity != current.Identity || seconds <= 0 || logicalProcessors <= 0 || current.CpuTicks < previous.CpuTicks)
            return new(false, 0, null);
        var cpu = 100.0 * (current.CpuTicks - previous.CpuTicks) / 10_000_000 / seconds / logicalProcessors;
        IoCounters? io = null;
        if (previous.Io is { } p && current.Io is { } c && c.ReadOperations >= p.ReadOperations && c.WriteOperations >= p.WriteOperations
            && c.OtherOperations >= p.OtherOperations && c.LogicalReadBytes >= p.LogicalReadBytes && c.LogicalWriteBytes >= p.LogicalWriteBytes && c.LogicalOtherBytes >= p.LogicalOtherBytes)
            io = new IoCounters { ReadOperations = c.ReadOperations - p.ReadOperations, WriteOperations = c.WriteOperations - p.WriteOperations,
                OtherOperations = c.OtherOperations - p.OtherOperations, LogicalReadBytes = c.LogicalReadBytes - p.LogicalReadBytes,
                LogicalWriteBytes = c.LogicalWriteBytes - p.LogicalWriteBytes, LogicalOtherBytes = c.LogicalOtherBytes - p.LogicalOtherBytes };
        return new(true, Math.Clamp(cpu, 0, 100), io);
    }
    private static void AddIo(ref IoCounters target, IoCounters source)
    {
        target.ReadOperations += source.ReadOperations; target.WriteOperations += source.WriteOperations; target.OtherOperations += source.OtherOperations;
        target.LogicalReadBytes += source.LogicalReadBytes; target.LogicalWriteBytes += source.LogicalWriteBytes; target.LogicalOtherBytes += source.LogicalOtherBytes;
    }
}
