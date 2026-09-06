using AgentWatch.Aggregation;
using AgentWatch.Configuration;
using Microsoft.Data.Sqlite;

namespace AgentWatch.Storage;

public sealed partial class AgentWatchDatabase
{
    public void ApplyRetention(DateTimeOffset now, RetentionConfig retention, CancellationToken cancellationToken = default)
    {
        EnsureWalBudget();
        using var transaction = connection.BeginTransaction();
        RollUp(60, 900, SampleAggregator.Floor(now.AddDays(-retention.MinuteDays).ToUnixTimeSeconds(), 900), transaction, cancellationToken);
        RollUp(900, 86400, SampleAggregator.Floor(now.AddDays(-retention.FifteenMinuteDays).ToUnixTimeSeconds(), 86400), transaction, cancellationToken);
        if (retention.DailyDays > 0)
        {
            using var delete = connection.CreateCommand(); delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM history WHERE resolution_s=86400 AND bucket_utc < $cut";
            delete.Parameters.AddWithValue("$cut", SampleAggregator.Floor(now.AddDays(-retention.DailyDays).ToUnixTimeSeconds(), 86400)); delete.ExecuteNonQuery();
        }
        using var receipts = connection.CreateCommand(); receipts.Transaction = transaction;
        receipts.CommandText = "DELETE FROM batch_receipt WHERE committed_utc < $cut";
        receipts.Parameters.AddWithValue("$cut", now.AddDays(-30).ToUnixTimeSeconds()); receipts.ExecuteNonQuery();
        using var meta = connection.CreateCommand(); meta.Transaction = transaction;
        meta.CommandText = "INSERT INTO meta VALUES ('lastRetentionUtc',$time) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        meta.Parameters.AddWithValue("$time", now.ToString("O")); meta.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
    }
    private void RollUp(int source, int target, long cutoff, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var select = HistorySelect(transaction); using var upsert = HistoryUpsert(transaction);
        using var delete = connection.CreateCommand(); delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM history WHERE resolution_s=$res AND bucket_utc=$time AND kind=$kind AND item=$item";
        AddKeyParameters(delete); delete.Prepare();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<HistoryRow>(512);
            using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT resolution_s,bucket_utc,kind,item,payload FROM history WHERE resolution_s=$res AND bucket_utc < $cut ORDER BY bucket_utc,kind,item LIMIT 512";
                query.Parameters.AddWithValue("$res", source); query.Parameters.AddWithValue("$cut", cutoff);
                using var reader = query.ExecuteReader();
                while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3), (byte[])reader[4]));
            }
            if (rows.Count == 0) break;
            var rollups = new Dictionary<(long Bucket, int Kind, string Item), HistoryRow>();
            foreach (var row in rows)
            {
                var bucket = SampleAggregator.Floor(row.BucketUnixSeconds, target);
                var rolled = row with { ResolutionSeconds = target, BucketUnixSeconds = bucket };
                var key = (bucket, row.Kind, row.Item);
                if (rollups.TryGetValue(key, out var prior)) rolled = rolled with { Payload = MergePayload(rolled, prior.Payload) };
                rollups[key] = rolled;
            }
            foreach (var row in rollups.Values) MergeRow(row, select, upsert);
            // Fine data is removed only after every corresponding coarse write succeeded, in this same transaction.
            foreach (var row in rows) { SetKey(delete, row); delete.ExecuteNonQuery(); }
        }
    }
}

public static class HistoryReader
{
    public static IEnumerable<HistoryRow> Read(SqliteConnection connection, DateTimeOffset from, DateTimeOffset to,
        int? kind = null, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT resolution_s,bucket_utc,kind,item,payload FROM history
            WHERE ((resolution_s=60 AND bucket_utc>$from-60 AND bucket_utc<$to)
               OR (resolution_s=900 AND bucket_utc>$from-900 AND bucket_utc<$to)
               OR (resolution_s=86400 AND bucket_utc>$from-86400 AND bucket_utc<$to))
              AND ($kind IS NULL OR kind=$kind)
            ORDER BY bucket_utc,resolution_s,kind,item
            """;
        command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds()); command.Parameters.AddWithValue("$to", Math.Ceiling((to.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond));
        command.Parameters.AddWithValue("$kind", kind.HasValue ? kind.Value : DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3), (byte[])reader[4]);
    }
    public static Dictionary<string, string> Metadata(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT key,value FROM meta";
        using var reader = command.ExecuteReader(); var result = new Dictionary<string, string>();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }
    public static List<AnomalyEvent> Anomalies(SqliteConnection connection, DateTimeOffset from, DateTimeOffset to, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM anomaly WHERE end_utc >= $from AND start_utc < $to ORDER BY start_utc LIMIT 10000";
        command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds()); command.Parameters.AddWithValue("$to", Math.Ceiling((to.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond));
        using var reader = command.ExecuteReader(); var result = new List<AnomalyEvent>();
        while (reader.Read()) result.Add(System.Text.Json.JsonSerializer.Deserialize<AnomalyEvent>(reader.GetString(0), JsonDefaults.Compact)!);
        return result;
    }
}
