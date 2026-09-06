using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace AgentWatch.Storage;
internal static partial class SqliteNative
{
    [LibraryImport("e_sqlite3", EntryPoint = "sqlite3_file_control", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int FileControl(nint database, string name, int operation, ref int argument);
}
