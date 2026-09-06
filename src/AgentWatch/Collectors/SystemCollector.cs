using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentWatch.Models;
using AgentWatch.Windows;

namespace AgentWatch.Collectors;

public sealed class SystemCollector
{
    private CpuTimes? previous;
    public CpuSample? ReadCpu(bool reset = false)
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetSystemTimes failed");
        var current = new CpuTimes(idle, kernel, user);
        var result = reset || previous is null ? null : Calculate(previous.Value, current);
        previous = current;
        return result;
    }
    public static CpuSample? Calculate(CpuTimes previous, CpuTimes current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User) return null;
        var idle = current.Idle - previous.Idle;
        var kernel = current.Kernel - previous.Kernel;
        var user = current.User - previous.User;
        var total = (double)kernel + user;
        if (total <= 0 || idle > kernel) return null;
        return new(100 * (total - idle) / total, 100 * user / total, 100 * (kernel - idle) / total);
    }
    public static MemorySample ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GlobalMemoryStatusEx failed");
        if (!NativeMethods.K32GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<PerformanceInformation>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetPerformanceInfo failed");
        return new(status.TotalPhysical, status.AvailablePhysical, status.MemoryLoad,
            checked((ulong)info.CommitTotal * (ulong)info.PageSize), checked((ulong)info.CommitLimit * (ulong)info.PageSize), (ulong)info.PageSize);
    }
}
