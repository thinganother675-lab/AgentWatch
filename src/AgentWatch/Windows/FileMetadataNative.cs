using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentWatch.Windows;

internal static partial class FileMetadataNative
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileStandardInformation information, uint size);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileStandardInformation
{
    internal long AllocationSize, EndOfFile;
    internal uint NumberOfLinks;
    internal byte DeletePending, Directory;
}
