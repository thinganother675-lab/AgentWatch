using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentWatch.Windows;

internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeServiceHandle() : base(true) { }
    protected override bool ReleaseHandle() => ServiceNative.CloseServiceHandle(handle);
}
internal static partial class ServiceNative
{
    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeServiceHandle OpenManager(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeServiceHandle Create(SafeServiceHandle manager, string name, string displayName, uint access,
        uint serviceType, uint startType, uint errorControl, string binaryPath, string? group, nint tag,
        string? dependencies, string? account, string? password);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeServiceHandle OpenService(SafeServiceHandle manager, string name, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool Configure(SafeServiceHandle service, uint infoLevel, void* info);
    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Change(SafeServiceHandle service, uint type, uint start, uint errorControl, string? binaryPath,
        string? group, nint tag, string? dependencies, string? account, string? password, string? displayName);
    [LibraryImport("advapi32.dll", EntryPoint = "DeleteService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Delete(SafeServiceHandle service);
    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint handle);
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFile(string source, string? destination, uint flags);
}
[StructLayout(LayoutKind.Sequential)]
internal struct ServiceAction { public uint Type, Delay; }
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ServiceFailureActions
{
    public uint ResetPeriod;
    public nint RebootMessage, Command;
    public uint Count;
    public ServiceAction* Actions;
}
