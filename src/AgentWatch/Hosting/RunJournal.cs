using System.Text.Json;
using AgentWatch.Configuration;

namespace AgentWatch.Hosting;

public sealed record RunRecord(string Id, string AppVersion, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc,
    int ProcessId, string Mode, string Outcome, SamplingConfig Sampling, long LogFailures);

public static class RunJournal
{
    public static string NewId() => $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    public static void Save(string directory, RunRecord record, bool create)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "run.json");
        if (create)
        {
            using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, record, JsonDefaults.Options);
            return;
        }
        // Replace only this process's own manifest; diagnostic segments are never overwritten.
        var temporary = Path.Combine(directory, "run-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, record, JsonDefaults.Options);
        File.Move(temporary, target, true);
    }

    public static RunInventory Read(string dataDirectory, DateTimeOffset from, DateTimeOffset to)
    {
        var root = Path.Combine(dataDirectory, "logs", "runs");
        if (!Directory.Exists(root)) return new([], 0, false, "Earlier versions used shared agentwatch.log files; they are preserved but cannot provide structured run outcomes.");
        var directories = Directory.EnumerateDirectories(root).Take(5001).ToArray();
        var records = new List<RunRecord>(); var errors = 0;
        foreach (var directory in directories.Take(5000))
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { errors++; continue; }
                var file = Path.Combine(directory, "run.json");
                if (!File.Exists(file) || new FileInfo(file).Length > 32768
                    || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { errors++; continue; }
                var record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), JsonDefaults.Compact);
                if (record is null || record.StartedUtc >= to || (record.EndedUtc is { } end && end < from)) continue;
                records.Add(record);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { errors++; }
        }
        return new(records.OrderByDescending(r => r.StartedUtc).Take(200).ToArray(), errors,
            directories.Length > 5000 || records.Count > 200,
            "Outcome 'started' means no final outcome was saved; it does not prove the process is still running. No run logs are automatically deleted.");
    }
}

public sealed record RunInventory(IReadOnlyList<RunRecord> Runs, int UnreadableEntries, bool Truncated, string Caveat);
