using Microsoft.Data.Sqlite;

namespace AgentWatch.Storage;

public sealed partial class AgentWatchDatabase
{
    public static string NativeVersion
    {
        get
        {
            using var probe = new SqliteConnection("Data Source=:memory:;Pooling=False"); probe.Open();
            using var command = probe.CreateCommand(); command.CommandText = "SELECT sqlite_version()";
            return (string)command.ExecuteScalar()!;
        }
    }
    private void EnsureWalBudget()
    {
        var wal = new FileInfo(Path + "-wal");
        if (!wal.Exists || wal.Length < maximumWalBytes) return;
        // A long-lived reader can prevent auto-checkpoint from recycling the WAL. Pause
        // this writer at a bounded watermark instead of filling the disk indefinitely.
        Execute("PRAGMA busy_timeout=0");
        try
        {
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var result = command.ExecuteReader();
            if (!result.Read() || result.GetInt32(0) != 0)
                throw new IOException("AgentWatch WAL budget reached; a reader prevents checkpoint. Persistence is paused until that reader releases its snapshot.");
        }
        finally { Execute("PRAGMA busy_timeout=5000"); }
    }
}
