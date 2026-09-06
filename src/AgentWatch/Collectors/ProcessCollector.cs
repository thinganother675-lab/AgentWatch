using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentWatch.Attribution;
using AgentWatch.Models;
using AgentWatch.Windows;

namespace AgentWatch.Collectors;

public sealed class ProcessCollector
{
    private readonly ProcessTracker tracker = new();
    public IReadOnlyList<ProcessReading> LastReadings { get; private set; } = [];
    public int InaccessibleCount { get; private set; }
    public static List<ProcessNode> Snapshot()
    {
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Toolhelp process snapshot failed");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var list = new List<ProcessNode>(256);
        if (!NativeMethods.Process32First(snapshot, ref entry))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Process32First failed");
        do { list.Add(new(entry.ProcessId, entry.ParentProcessId, entry.Name)); }
        while (NativeMethods.Process32Next(snapshot, ref entry));
        var lastError = Marshal.GetLastPInvokeError();
        if (lastError != 18) throw new Win32Exception(lastError, "Process32Next failed");
        return list;
    }
    public static ProcessReading? Read(ProcessNode node)
    {
        using var process = NativeMethods.OpenProcess(0x1000, false, node.Pid);
        if (process.IsInvalid || !NativeMethods.GetProcessTimes(process, out var created, out _, out var kernel, out var user)) return null;
        IoCounters? io = NativeMethods.GetProcessIoCounters(process, out var counters) ? counters : null;
        var hasMemory = NativeMethods.K32GetProcessMemoryInfo(process, out var memory, (uint)Marshal.SizeOf<ProcessMemoryCounters>());
        return new(new(node.Pid, created), node.ParentPid, node.Name, kernel + user, io,
            hasMemory ? (ulong)memory.WorkingSet : null, hasMemory ? (ulong)memory.PrivateUsage : null);
    }
    public AgentSample[] Collect(double seconds, bool reset = false)
    {
        var nodes = Snapshot();
        // Foreground validation can be launched by Codex; monitor work belongs only to self metrics.
        nodes.RemoveAll(n => n.Pid == (uint)Environment.ProcessId || n.Name.Equals("AgentWatch.exe", StringComparison.OrdinalIgnoreCase));
        var relevant = new HashSet<uint>();
        foreach (var node in nodes) if (ProcessTracker.RootKind(node.Name) != AgentKind.Other) relevant.Add(node.Pid);
        foreach (var identity in tracker.KnownIdentities) relevant.Add(identity.Pid);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in nodes) if (relevant.Contains(node.ParentPid) && relevant.Add(node.Pid)) changed = true;
        }
        var readings = new List<ProcessReading>(relevant.Count);
        InaccessibleCount = 0;
        foreach (var node in nodes)
        {
            if (!relevant.Contains(node.Pid)) continue;
            var reading = Read(node);
            if (reading is not null) readings.Add(reading); else InaccessibleCount++;
        }
        LastReadings = readings;
        return tracker.Update(readings, seconds, Environment.ProcessorCount, reset, InaccessibleCount);
    }
}
