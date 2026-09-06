using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentWatch.Windows;

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool K32GetPerformanceInfo(out PerformanceInformation info, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(SafeProcessHandle process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool K32GetProcessMemoryInfo(SafeProcessHandle process, out ProcessMemoryCounters counters, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32First(SafeSnapshotHandle snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32Next(SafeSnapshotHandle snapshot, ref ProcessEntry entry);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PdhOpenQuery(string? dataSource, nuint userData, out SafePdhHandle query);
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PdhAddEnglishCounter(SafePdhHandle query, string path, nuint userData, out nint counter);
    [LibraryImport("pdh.dll")]
    internal static partial uint PdhCollectQueryData(SafePdhHandle query);
    [LibraryImport("pdh.dll")]
    internal static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhValue value);
    [LibraryImport("pdh.dll")]
    internal static partial uint PdhCloseQuery(nint query);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, byte* input, uint inputSize,
        byte* output, uint outputSize, out uint returned, nint overlapped);
}

internal sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeSnapshotHandle() : base(true) { }
    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

internal sealed class SafePdhHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdhHandle() : base(true) { }
    protected override bool ReleaseHandle() => NativeMethods.PdhCloseQuery(handle) == 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatus
{
    public uint Length, MemoryLoad;
    public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PerformanceInformation
{
    public uint Size;
    public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
    public uint HandleCount, ProcessCount, ThreadCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct IoCounters
{
    public ulong ReadOperations, WriteOperations, OtherOperations, LogicalReadBytes, LogicalWriteBytes, LogicalOtherBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMemoryCounters
{
    public uint Size, PageFaultCount;
    public nuint PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool, QuotaPeakNonpagedPool, QuotaNonpagedPool, PagefileUsage, PeakPagefileUsage, PrivateUsage;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct ProcessEntry
{
    public uint Size, Usage, ProcessId;
    public nuint DefaultHeapId;
    public uint ModuleId, Threads, ParentProcessId;
    public int PriorityClassBase;
    public uint Flags;
    public fixed char ExeFile[260];
    public string Name { get { fixed (char* p = ExeFile) return new string(p); } }
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PdhValue
{
    [FieldOffset(0)] public uint Status;
    [FieldOffset(8)] public double DoubleValue;
}
