using System.Text;
using AgentWatch.Cli;
namespace AgentWatch;
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { return await Commands.Run(args); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"ERROR ({e.GetType().Name}): {e.Message}");
            if (e is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 11 or 26 })
                Console.Error.WriteLine("Existing database/WAL/SHM files were preserved. Stop collection, preserve a copy, and follow docs/OPERATIONS.md for recovery.");
            return e is UnauthorizedAccessException ? 3 : e is ArgumentException ? 2 : 1;
        }
    }
}
