using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentWatch.Cli;
using AgentWatch.Configuration;
using AgentWatch.Storage;
using Microsoft.Data.Sqlite;

namespace AgentWatch.Analysis;

public sealed record LocalDayReport(string LocalDate, bool PartialDay, WatchReport Measurements);
public sealed record BundleFile(string Name, long Bytes, string Sha256);
public sealed record AnalysisManifest(int SchemaVersion, string AppVersion, string State, string TimeZone,
    QueryPeriod RequestedPeriod, DateTimeOffset SnapshotStartedUtc, DateTimeOffset SnapshotCompletedUtc,
    string SourceDatabase, DateTimeOffset? LastPersistedSampleUtc, IReadOnlyList<BundleFile> Files,
    IReadOnlyList<string> Caveats);

public static class AnalysisBundle
{
    public const long MaximumSnapshotBytes = 512L * 1024 * 1024;

    // SQLite online backup includes committed WAL data; copying DB/WAL separately is unsafe.
    public static void Snapshot(string sourcePath, string destinationPath)
    {
        using var source = AgentWatchDatabase.OpenReadOnly(sourcePath);
        using (var check = source.CreateCommand())
        {
            check.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
            if (Convert.ToInt64(check.ExecuteScalar()) > MaximumSnapshotBytes)
                throw new IOException("History exceeds the 512 MiB on-demand snapshot budget; use report/weekly instead.");
        }
        using (new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString());
        target.Open();
        source.BackupDatabase(target);
        using var finalize = target.CreateCommand();
        finalize.CommandText = "PRAGMA journal_mode=DELETE;";
        finalize.ExecuteNonQuery();
    }

    public static IReadOnlyList<(string Date, bool Partial, QueryPeriod Period)> LocalPeriods(QueryPeriod period, TimeZoneInfo zone)
    {
        if (period.FromUtc >= period.ToUtc || (period.ToUtc - period.FromUtc).TotalDays > 31)
            throw new ArgumentException("Analysis requires a positive period of at most 31 days.");
        var result = new List<(string, bool, QueryPeriod)>();
        var day = TimeZoneInfo.ConvertTime(period.FromUtc, zone).Date;
        static DateTimeOffset Boundary(DateTime day, TimeZoneInfo zone)
        {
            day = DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(day))
                throw new ArgumentException("This timezone has an invalid local midnight in the requested period; choose UTC for this analysis.");
            // If midnight repeats, use its first occurrence as the single shared day boundary.
            var offset = zone.IsAmbiguousTime(day) ? zone.GetAmbiguousTimeOffsets(day).Max() : zone.GetUtcOffset(day);
            return new DateTimeOffset(day, offset).ToUniversalTime();
        }
        for (var count = 0; count < 33; count++, day = day.AddDays(1))
        {
            var start = Boundary(day, zone); var end = Boundary(day.AddDays(1), zone);
            if (start >= period.ToUtc) break;
            var from = start > period.FromUtc ? start : period.FromUtc;
            var to = end < period.ToUtc ? end : period.ToUtc;
            if (from < to) result.Add((day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), from != start || to != end, new(from, to)));
        }
        return result;
    }

    public static AnalysisManifest Create(string dataDirectory, string outputDirectory, QueryPeriod period, TimeZoneInfo zone,
        WatchConfig config, string? marksDirectory = null, string? usageDirectory = null, string? pricingPath = null)
    {
        var periods = LocalPeriods(period, zone); // Validate before creating artifacts.
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Analysis output must be a new directory; prior reports are never overwritten.");
        var source = Path.Combine(Path.GetFullPath(dataDirectory), "agentwatch.db");
        var started = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(output);
        try
        {
            Directory.CreateDirectory(Path.Combine(output, "history"));
            var snapshot = Path.Combine(output, "history", "agentwatch.db");
            Snapshot(source, snapshot);
            var completed = DateTimeOffset.UtcNow;
            var service = new ReportService(snapshot, config.Thresholds, config.PhysicalDriveNumber, config.Sampling);
            var weekly = service.GetWeeklyReport(period, marksDirectory, usageDirectory: usageDirectory,
                pricingPath: pricingPath, runDirectory: dataDirectory, generatedUtc: completed);
            var days = periods.Select(p => new LocalDayReport(p.Date, p.Partial, service.GetReport(p.Period))).ToArray();
            void Write(string name, string text)
            {
                using var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(text);
            }
            void Json(string name, object value) => Write(name, JsonSerializer.Serialize(value, JsonDefaults.Options));
            Json("report.json", weekly.Measurements);
            Json("weekly.json", weekly);
            Json("days.json", days);
            Json("history/config.json", new { config.Thresholds, config.Sampling, config.PhysicalDriveNumber });
            Write("weekly.md", WeeklyText.Render(weekly));
            Write("summary.md", Render(weekly, days, zone));
            var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
            {
                using var stream = File.OpenRead(path);
                return new BundleFile(Path.GetRelativePath(output, path), stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
            }).ToArray();
            var manifest = new AnalysisManifest(1, "0.2.1", "complete", zone.Id, period, started, completed, source,
                weekly.Quality.LastPersistedSampleUtc, files,
                ["All resource reports read the same retained SQLite snapshot. Snapshot time is distinct from the requested end time.",
                 "Marks, runs and normalized usage are read separately once for weekly.json, not atomically with history. Their captured values remain in weekly.json. No transcript import is triggered.",
                 "Whole overlapping buckets are included. Coarse buckets crossing local midnight can occur in multiple days; do not sum daily rows as exact period totals.",
                 "history/agentwatch.db is a full history backup (up to 512 MiB), not a period-only export. Delete/archive the bundle manually when no longer needed."]);
            Json("manifest.json", manifest); // Completion marker is written last.
            return manifest;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(output, "failed.json"), JsonSerializer.Serialize(new { state = "failed", error = e.Message }, JsonDefaults.Options));
            throw;
        }
    }

    private static string Render(WeeklyReport report, IReadOnlyList<LocalDayReport> days, TimeZoneInfo zone)
    {
        static string N(double? value, string format = "F2") => value?.ToString(format, System.Globalization.CultureInfo.InvariantCulture) ?? "нет данных";
        var b = new StringBuilder("# AgentWatch — анализ нагрузки\n\n");
        b.AppendLine($"Часовой пояс: `{zone.Id}`. Период: {TimeZoneInfo.ConvertTime(report.Measurements.Period.FromUtc, zone):O} — {TimeZoneInfo.ConvertTime(report.Measurements.Period.ToUtc, zone):O}.");
        b.AppendLine($"\nНаблюдалось **{N(report.Quality.ObservedSeconds / 3600)} ч** из {N(report.Quality.RequestedSeconds / 3600)} календарных часов. Пропуски не считаются нулевой нагрузкой.");
        b.AppendLine("\n| Локальный день | Наблюдалось, ч | CPU, % | RAM минимум, МБ | RAM <512 МиБ, мин | Доля валидного времени, % | Физическая запись, ГБ |");
        b.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var d in days)
        {
            var r = d.Measurements; var m = r.System.Memory;
            b.AppendLine($"| {d.LocalDate}{(d.PartialDay ? " (часть дня)" : "")} | {N(r.Period.ObservedSeconds / 3600)} | {N(r.System.Cpu.TotalPercent.Average)} | {N(m.AvailableBytes.Minimum / 1e6)} | {N(m.SecondsBelow512MiB / 60)} | {N(m.AvailableBytes.ValidSeconds > 0 ? m.SecondsBelow512MiB / m.AvailableBytes.ValidSeconds * 100 : null)} | {N(r.Storage.PhysicalDiskWriteBytes / 1e9)} |");
        }
        b.AppendLine("\nДни сравниваются по наблюдаемому времени, без контроля набора задач. Целые бакеты на границах могут повторяться в соседних днях; итоги периода берите из report.json.");
        b.AppendLine("\n## Выводы по периоду\n");
        foreach (var finding in report.Findings)
        {
            b.AppendLine($"- **{finding.Summary}** Уверенность: {finding.Confidence}. {string.Join(" ", finding.Evidence)} Следующий шаг: {finding.NextAction}");
            b.AppendLine($"  Альтернативы: {string.Join(" ", finding.AlternativeExplanations)}");
        }
        var self = report.Measurements.AgentWatch;
        b.AppendLine($"\nAgentWatch: CPU {N(self.CpuPercent.Average, "F4")}%; working set средний/максимум {N(self.WorkingSetBytes.Average / 1e6)}/{N(self.WorkingSetBytes.Maximum / 1e6)} МБ; логическая запись {N(self.LogicalProcessWriteBytes / 1e6)} МБ.");
        b.AppendLine("\n## Качество и границы выводов\n");
        foreach (var note in report.Quality.Notes.Concat(report.Measurements.Limitations)) b.AppendLine("- " + note);
        b.AppendLine("\nПодробные эпизоды, отметки и сессии: [weekly.md](weekly.md). Исходные измерения: [report.json](report.json), локальные дни: [days.json](days.json). Контрольные суммы и происхождение: [manifest.json](manifest.json).\n");
        return b.ToString();
    }
}
