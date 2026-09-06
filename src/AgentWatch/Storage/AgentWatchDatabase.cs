using System.Diagnostics;
using System.Text.Json;
using AgentWatch.Aggregation;
using AgentWatch.Collectors;
using AgentWatch.Configuration;
using AgentWatch.Models;
using Microsoft.Data.Sqlite;

namespace AgentWatch.Storage;

public sealed class PersistBatch
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public List<AggregateFrame> Frames { get; } = [];
    public List<HotFileSample> HotFiles { get; } = [];
    public List<NvmeHealth> Nvme { get; } = [];
    public List<AnomalyEvent> Anomalies { get; } = [];
    public Dictionary<string, string> Metadata { get; } = new();
}

public sealed partial class AgentWatchDatabase : IDisposable
{
    public const int SchemaVersion = 1;
    private readonly SqliteConnection connection;
    private readonly FileStream writerLock;
    private readonly long maximumWalBytes;
    public string Path { get; }
    public double LastFlushMilliseconds { get; private set; }
    public AgentWatchDatabase(string path, long maximumWalBytes = 64L * 1024 * 1024)
    {
        if (maximumWalBytes < 128 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumWalBytes));
        this.maximumWalBytes = maximumWalBytes;
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        writerLock = new FileStream(Path + ".writer-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            connection.Open();
            if (!Version.TryParse((string)Scalar("SELECT sqlite_version()")!, out var nativeVersion) || nativeVersion < new Version(3, 51, 3))
                throw new InvalidOperationException("SQLite 3.51.3 or newer is required (WAL-reset fix).");
            var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
            if (version > SchemaVersion) throw new InvalidDataException($"Database schema {version} is newer than supported {SchemaVersion}; upgrade AgentWatch.");
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA wal_autocheckpoint=256; PRAGMA journal_size_limit=4194304; PRAGMA cache_size=-1024; PRAGMA temp_store=MEMORY;");
            // Retain WAL/SHM so the installing user can open a WAL database with directory read access only.
            var persistWal = 1;
            var result = SqliteNative.FileControl(connection.Handle!.DangerousGetHandle(), "main", 10, ref persistWal);
            if (result != 0) throw new InvalidOperationException($"SQLite PERSIST_WAL failed: {result}");
            if (version == 0)
            {
                using var transaction = connection.BeginTransaction();
                Execute("""
                    CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
                    CREATE TABLE history (
                      resolution_s INTEGER NOT NULL CHECK(resolution_s IN (60,900,86400)),
                      bucket_utc INTEGER NOT NULL, kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),
                      item TEXT NOT NULL, payload BLOB NOT NULL,
                      PRIMARY KEY(resolution_s,bucket_utc,kind,item)) WITHOUT ROWID;
                    CREATE TABLE anomaly (id TEXT PRIMARY KEY, start_utc INTEGER NOT NULL, end_utc INTEGER NOT NULL,
                      type TEXT NOT NULL, severity TEXT NOT NULL, payload TEXT NOT NULL);
                    CREATE INDEX anomaly_time ON anomaly(end_utc);
                    CREATE TABLE batch_receipt (id TEXT PRIMARY KEY, committed_utc INTEGER NOT NULL) WITHOUT ROWID;
                    PRAGMA user_version=1;
                    """, transaction);
                transaction.Commit();
            }
        }
        catch { connection.Dispose(); writerLock.Dispose(); throw; }
    }
    public static SqliteConnection OpenReadOnly(string path)
    {
        var result = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            result.Open(); using var command = result.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000; PRAGMA cache_size=-1024;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version != SchemaVersion) throw new InvalidDataException($"Database schema {version} is not supported by this AgentWatch ({SchemaVersion}).");
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public void Persist(PersistBatch batch)
    {
        EnsureWalBudget();
        var timer = Stopwatch.StartNew();
        using var transaction = connection.BeginTransaction();
        using var receipt = connection.CreateCommand(); receipt.Transaction = transaction;
        receipt.CommandText = "INSERT OR IGNORE INTO batch_receipt VALUES ($id,$time)";
        receipt.Parameters.AddWithValue("$id", batch.Id); receipt.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (receipt.ExecuteNonQuery() == 0) { transaction.Rollback(); return; }
        using var select = HistorySelect(transaction); using var upsert = HistoryUpsert(transaction);
        foreach (var frame in batch.Frames) MergeRow(new(frame.ResolutionSeconds, frame.BucketUnixSeconds, 0, "", AggregateCodec.Encode(frame)), select, upsert);
        foreach (var file in batch.HotFiles)
            MergeRow(new(60, SampleAggregator.Floor(file.TimestampUtc.ToUnixTimeSeconds(), 60), 1, file.Name, WindowCodec.Encode(HotFileWindow.From(file))), select, upsert);
        foreach (var nvme in batch.Nvme)
            MergeRow(new(60, SampleAggregator.Floor(nvme.TimestampUtc.ToUnixTimeSeconds(), 60), 2, nvme.PhysicalDriveNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), WindowCodec.Encode(NvmeWindow.From(nvme))), select, upsert);
        using var anomalyCommand = connection.CreateCommand(); anomalyCommand.Transaction = transaction;
        anomalyCommand.CommandText = "INSERT INTO anomaly VALUES ($id,$start,$end,$type,$severity,$payload) ON CONFLICT(id) DO UPDATE SET end_utc=excluded.end_utc,severity=excluded.severity,payload=excluded.payload";
        foreach (var name in new[] { "$id", "$start", "$end", "$type", "$severity", "$payload" }) anomalyCommand.Parameters.Add(new SqliteParameter(name, SqliteType.Text));
        anomalyCommand.Prepare();
        foreach (var anomaly in batch.Anomalies)
        {
            var values = new object[] { anomaly.Id, anomaly.StartUtc.ToUnixTimeSeconds(), anomaly.EndUtc.ToUnixTimeSeconds(), anomaly.Type, anomaly.Severity, JsonSerializer.Serialize(anomaly, JsonDefaults.Compact) };
            for (var i = 0; i < values.Length; i++) anomalyCommand.Parameters[i].Value = values[i];
            anomalyCommand.ExecuteNonQuery();
        }
        using var metadata = connection.CreateCommand(); metadata.Transaction = transaction;
        metadata.CommandText = "INSERT INTO meta VALUES ($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        metadata.Parameters.Add("$key", SqliteType.Text); metadata.Parameters.Add("$value", SqliteType.Text); metadata.Prepare();
        foreach (var pair in batch.Metadata) { metadata.Parameters[0].Value = pair.Key; metadata.Parameters[1].Value = pair.Value; metadata.ExecuteNonQuery(); }
        transaction.Commit(); LastFlushMilliseconds = timer.Elapsed.TotalMilliseconds;
    }
    private SqliteCommand HistorySelect(SqliteTransaction transaction)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM history WHERE resolution_s=$res AND bucket_utc=$time AND kind=$kind AND item=$item";
        AddKeyParameters(command); command.Prepare(); return command;
    }
    private SqliteCommand HistoryUpsert(SqliteTransaction transaction)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO history VALUES ($res,$time,$kind,$item,$payload) ON CONFLICT(resolution_s,bucket_utc,kind,item) DO UPDATE SET payload=excluded.payload";
        AddKeyParameters(command); command.Parameters.Add("$payload", SqliteType.Blob); command.Prepare(); return command;
    }
    private static void AddKeyParameters(SqliteCommand command)
    {
        command.Parameters.Add("$res", SqliteType.Integer); command.Parameters.Add("$time", SqliteType.Integer);
        command.Parameters.Add("$kind", SqliteType.Integer); command.Parameters.Add("$item", SqliteType.Text);
    }
    private static void SetKey(SqliteCommand command, HistoryRow row)
    {
        command.Parameters[0].Value = row.ResolutionSeconds; command.Parameters[1].Value = row.BucketUnixSeconds;
        command.Parameters[2].Value = row.Kind; command.Parameters[3].Value = row.Item;
    }
    private static byte[] MergePayload(HistoryRow row, byte[] previous)
    {
        switch (row.Kind)
        {
            case 0:
                var frame = AggregateCodec.Decode(row.BucketUnixSeconds, row.ResolutionSeconds, previous);
                frame.Merge(AggregateCodec.Decode(row.BucketUnixSeconds, row.ResolutionSeconds, row.Payload)); return AggregateCodec.Encode(frame);
            case 1:
                var file = WindowCodec.Decode<HotFileWindow>(previous); file.Merge(WindowCodec.Decode<HotFileWindow>(row.Payload)); return WindowCodec.Encode(file);
            case 2:
                var nvme = WindowCodec.Decode<NvmeWindow>(previous); nvme.Merge(WindowCodec.Decode<NvmeWindow>(row.Payload)); return WindowCodec.Encode(nvme);
            default: throw new InvalidDataException("Unknown history kind.");
        }
    }
    private static void MergeRow(HistoryRow row, SqliteCommand select, SqliteCommand upsert)
    {
        SetKey(select, row); var previous = select.ExecuteScalar() as byte[];
        SetKey(upsert, row); upsert.Parameters[4].Value = previous is null ? row.Payload : MergePayload(row, previous);
        upsert.ExecuteNonQuery();
    }
    internal object? Scalar(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    internal void Execute(string sql, SqliteTransaction? transaction = null)
    { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose() { connection.Dispose(); writerLock.Dispose(); }
}
