using System.Globalization;
using System.Text.Json;
using AgentWatch.Configuration;
using Microsoft.Data.Sqlite;

namespace AgentWatch.Analysis.Usage;

public sealed class UsageStore : IDisposable
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentWatch", "usage");
    private readonly SqliteConnection connection;
    public UsageStore(string directory)
    {
        Directory.CreateDirectory(directory);
        connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "usage.db"), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // Separate store: never open the system collector DB for writing here.
        command.CommandText = "PRAGMA user_version"; var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > 1) { connection.Dispose(); throw new InvalidDataException("Usage store version is newer than supported."); }
        command.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-1024;
            CREATE TABLE IF NOT EXISTS source (id TEXT PRIMARY KEY, cursor TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS usage (id TEXT PRIMARY KEY, agent TEXT NOT NULL, session TEXT NOT NULL,
              timestamp TEXT NOT NULL, source TEXT NOT NULL, basis TEXT NOT NULL, payload TEXT NOT NULL) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS usage_time ON usage(timestamp);
            CREATE INDEX IF NOT EXISTS usage_source ON usage(source,basis);
            CREATE TABLE IF NOT EXISTS activity (id TEXT PRIMARY KEY, agent TEXT NOT NULL, session TEXT NOT NULL,
              timestamp TEXT NOT NULL, payload TEXT NOT NULL) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS activity_time ON activity(timestamp);
            CREATE TABLE IF NOT EXISTS session (agent TEXT NOT NULL, id TEXT NOT NULL, first_utc TEXT NOT NULL,
              started_utc TEXT, ended_utc TEXT, last_utc TEXT NOT NULL, PRIMARY KEY(agent,id)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }
    public SourceCursor Cursor(string id)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT cursor FROM source WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string text ? JsonSerializer.Deserialize<SourceCursor>(text, JsonDefaults.Compact) ?? new() : new();
    }
    public void ImportFile(string sourceId, SourceCursor cursor, Action<Action<UsageEntry>, Action<SessionActivity>> read)
    {
        using var transaction = connection.BeginTransaction();
        using var usage = connection.CreateCommand(); usage.Transaction = transaction;
        usage.CommandText = """
            INSERT INTO usage VALUES($id,$agent,$session,$time,$source,$basis,$payload)
            ON CONFLICT(id) DO UPDATE SET timestamp=excluded.timestamp,payload=excluded.payload
            WHERE usage.session=excluded.session AND usage.agent=excluded.agent
            """;
        foreach (var name in new[] { "$id", "$agent", "$session", "$time", "$source", "$basis", "$payload" }) usage.Parameters.Add(new(name, SqliteType.Text));
        using var activity = connection.CreateCommand(); activity.Transaction = transaction;
        activity.CommandText = "INSERT OR IGNORE INTO activity VALUES($id,$agent,$session,$time,$payload)";
        foreach (var name in new[] { "$id", "$agent", "$session", "$time", "$payload" }) activity.Parameters.Add(new(name, SqliteType.Text));
        using var session = connection.CreateCommand(); session.Transaction = transaction;
        session.CommandText = """
            INSERT INTO session VALUES($agent,$id,$time,$start,$end,$time)
            ON CONFLICT(agent,id) DO UPDATE SET first_utc=MIN(session.first_utc,excluded.first_utc),
              started_utc=CASE WHEN session.started_utc IS NULL THEN excluded.started_utc
                WHEN excluded.started_utc IS NULL THEN session.started_utc ELSE MIN(session.started_utc,excluded.started_utc) END,
              ended_utc=CASE WHEN excluded.ended_utc IS NOT NULL AND excluded.last_utc>=session.last_utc THEN excluded.ended_utc
                WHEN session.ended_utc IS NOT NULL AND excluded.last_utc>session.ended_utc THEN NULL ELSE session.ended_utc END,
              last_utc=MAX(session.last_utc,excluded.last_utc)
            """;
        foreach (var name in new[] { "$agent", "$id", "$time", "$start", "$end" }) session.Parameters.Add(new(name, SqliteType.Text));
        void Touch(string agent, string id, DateTimeOffset time, string? type)
        {
            var utc = time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            session.Parameters[0].Value = agent; session.Parameters[1].Value = id; session.Parameters[2].Value = utc;
            session.Parameters[3].Value = type == "session.start" ? utc : DBNull.Value;
            session.Parameters[4].Value = type == "session.end" ? utc : DBNull.Value;
            session.ExecuteNonQuery();
        }
        read(entry =>
        {
            var values = new[] { entry.Id, entry.Agent, entry.SessionId, entry.TimestampUtc.ToUniversalTime().ToString("O"), entry.SourceFileId, entry.SourceKind, JsonSerializer.Serialize(entry, JsonDefaults.Compact) };
            for (var i = 0; i < values.Length; i++) usage.Parameters[i].Value = values[i];
            usage.ExecuteNonQuery(); Touch(entry.Agent, entry.SessionId, entry.TimestampUtc, null);
        }, entry =>
        {
            var values = new[] { entry.Id, entry.Agent, entry.SessionId, entry.TimestampUtc.ToUniversalTime().ToString("O"), JsonSerializer.Serialize(entry, JsonDefaults.Compact) };
            for (var i = 0; i < values.Length; i++) activity.Parameters[i].Value = values[i];
            activity.ExecuteNonQuery(); Touch(entry.Agent, entry.SessionId, entry.TimestampUtc, entry.Type);
        });
        if (cursor.HasResponseRecords)
        {
            using var deduplicate = connection.CreateCommand(); deduplicate.Transaction = transaction;
            deduplicate.CommandText = "DELETE FROM usage WHERE source=$source AND basis='codex.legacy-token-count'";
            deduplicate.Parameters.AddWithValue("$source", sourceId); deduplicate.ExecuteNonQuery();
        }
        using var save = connection.CreateCommand(); save.Transaction = transaction;
        save.CommandText = "INSERT INTO source VALUES($id,$cursor) ON CONFLICT(id) DO UPDATE SET cursor=excluded.cursor";
        save.Parameters.AddWithValue("$id", sourceId); save.Parameters.AddWithValue("$cursor", JsonSerializer.Serialize(cursor, JsonDefaults.Compact)); save.ExecuteNonQuery();
        transaction.Commit();
    }
    public void RecordImport(UsageImportResult result)
    {
        using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO meta VALUES('lastImport',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(result, JsonDefaults.Compact)); command.ExecuteNonQuery();
    }
    public void Dispose() => connection.Dispose();

    public static UsageInventory Read(string directory, QueryPeriod period, IReadOnlyList<PriceRule>? prices = null)
    {
        var path = Path.Combine(directory, "usage.db");
        if (!File.Exists(path)) return new([], [], [], null, false, ["Usage ещё не импортирован. Выполните usage-sync от обычного пользователя; служба не читает transcript-файлы."]);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open(); using var transaction = db.BeginTransaction(deferred: true);
        using var check = db.CreateCommand(); check.Transaction = transaction; check.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidDataException("Unsupported usage schema.");
        var entries = ReadRows<UsageEntry>(db, transaction, "usage", period, 50001);
        var activities = ReadRows<SessionActivity>(db, transaction, "activity", period, 50001);
        var truncated = entries.Count > 50000 || activities.Count > 50000;
        entries = entries.Take(50000).ToList(); activities = activities.Take(50000).ToList();
        using var query = db.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT agent,id,first_utc,started_utc,ended_utc,last_utc FROM session WHERE first_utc<$to AND last_utc>=$from ORDER BY last_utc DESC LIMIT 501";
        query.Parameters.AddWithValue("$from", period.FromUtc.ToUniversalTime().ToString("O")); query.Parameters.AddWithValue("$to", period.ToUtc.ToUniversalTime().ToString("O"));
        var sessions = new List<UsageSession>();
        var grouped = entries.GroupBy(e => (e.Agent, e.SessionId)).ToDictionary(g => g.Key, g => g.ToArray());
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                var agent = reader.GetString(0); var id = reader.GetString(1); var rows = grouped.GetValueOrDefault((agent, id)) ?? [];
                long? Sum(Func<TokenAmounts, long?> select) => rows.Length == 0 || rows.Any(r => select(r.Tokens) is null) ? null : rows.Sum(r => select(r.Tokens)!.Value);
                var estimates = rows.Select(r => UsagePricing.Estimate(r, prices ?? [])).ToArray();
                decimal? estimate = estimates.Length > 0 && estimates.All(e => e.HasValue) ? estimates.Sum(e => e!.Value) : null;
                sessions.Add(new(agent, id, DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    rows.Select(r => r.Model).OfType<string>().Distinct().Order().ToArray(),
                    rows.Select(r => r.ReasoningEffort).OfType<string>().Distinct().Order().ToArray(),
                    new(Sum(t => t.Input), Sum(t => t.Output), Sum(t => t.CacheRead), Sum(t => t.CacheWrite), Sum(t => t.ReasoningOutput),
                        agent == "codex" ? "includes-cache" : "excludes-cache", Sum(t => t.ReportedOutput), Sum(t => t.ReportedReasoningOutput)), rows.Length, estimate,
                    estimate.HasValue ? "explicit-api-equivalent-price-rules; not subscription billing" : "unknown: complete final tokens and an exact applicable price rule are required",
                    ["Токены относятся только к импортированным usage-событиям в запрошенном периоде; это не обязательно вся сессия.",
                     "First observed не равен достоверному start. Last activity не является session end; простой не закрывает сессию.",
                     agent == "claude" ? "Claude assistant output может быть промежуточным: reported output сохранён отдельно; final output/reasoning и полный cost неизвестны." : "Codex response_id дедуплицируется. При наличии response usage повторные legacy token_count не суммируются."]));
            }
        }
        using var metadata = db.CreateCommand(); metadata.Transaction = transaction; metadata.CommandText = "SELECT value FROM meta WHERE key='lastImport'";
        var import = metadata.ExecuteScalar() is string importText ? JsonSerializer.Deserialize<UsageImportResult>(importText, JsonDefaults.Compact) : null;
        using var sources = db.CreateCommand(); sources.Transaction = transaction;
        sources.CommandText = "SELECT COALESCE(SUM(COALESCE(json_extract(cursor,'$.invalidRecords'),0)),0), COALESCE(SUM(COALESCE(json_extract(cursor,'$.oversizedLines'),0)),0), COALESCE(SUM(COALESCE(json_extract(cursor,'$.sourceWasReplaced'),0)),0) FROM source";
        long sourceInvalid, sourceOversized, sourceReplaced;
        using (var sourceReader = sources.ExecuteReader()) { sourceReader.Read(); sourceInvalid = sourceReader.GetInt64(0); sourceOversized = sourceReader.GetInt64(1); sourceReplaced = sourceReader.GetInt64(2); }
        transaction.Commit();
        var notes = new List<string> { "Usage импортируется по запросу в отдельное пользовательское хранилище; постоянные Windows collectors не изменены.",
            "Модель и effort — явно записанные значения источника, не предположение по настройкам. Форматы локальных transcript-файлов могут меняться.",
            "Input Codex включает cache; input Claude не включает cache. Reasoning — часть output, не дополнительный total.",
            "Session/time overlap связывает контекст по времени, но не распределяет CPU/I/O между параллельными сессиями одного агента." };
        if (import is { BudgetReached: true } || import?.InvalidRecords > 0 || import?.OversizedLines > 0) notes.Add("Последний импорт был ограничен или пропускал некорректные/крупные записи; полнота usage не гарантирована.");
        if (sourceInvalid > 0 || sourceOversized > 0 || sourceReplaced > 0) notes.Add($"Сохранённая история источников: некорректных записей {sourceInvalid}, пропущенных крупных строк {sourceOversized}, заменённых файлов {sourceReplaced}. Полнота usage не гарантирована даже после успешного следующего импорта.");
        if (import is not null) notes.AddRange(import.Warnings.Take(10));
        return new(sessions.Take(500).ToArray(), entries, activities, import?.TimestampUtc, truncated || sessions.Count > 500, notes);
    }
    private static List<T> ReadRows<T>(SqliteConnection db, SqliteTransaction transaction, string table, QueryPeriod period, int limit)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT payload FROM {table} WHERE timestamp >= $from AND timestamp < $to ORDER BY timestamp LIMIT $limit";
        command.Parameters.AddWithValue("$from", period.FromUtc.ToUniversalTime().ToString("O")); command.Parameters.AddWithValue("$to", period.ToUtc.ToUniversalTime().ToString("O")); command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader(); var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), JsonDefaults.Compact)!);
        return result;
    }
}
