using System.Text.Json;
using AgentWatch.Configuration;

namespace AgentWatch.Analysis;

public sealed record UserMarker(string Id, DateTimeOffset TimestampUtc, DateTimeOffset RecordedUtc, string Note);
public sealed record MarkerInventory(IReadOnlyList<UserMarker> Markers, int UnreadableEntries, bool Truncated);

public static class UserMarkers
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentWatch", "marks");

    public static UserMarker Add(string directory, string note, DateTimeOffset timestamp)
    {
        note = note.Trim();
        if (note.Length is < 1 or > 500 || note.Any(char.IsControl))
            throw new ArgumentException("Marker note must contain 1..500 characters on one line without control characters.");
        var now = DateTimeOffset.UtcNow;
        if (timestamp > now.AddMinutes(5)) throw new ArgumentException("A slowdown marker cannot be more than five minutes in the future.");
        var record = new UserMarker(Guid.NewGuid().ToString("N"), timestamp.ToUniversalTime(), now, note);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{record.TimestampUtc:yyyyMMddTHHmmssfffffffZ}-{record.Id}.json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, record, JsonDefaults.Options);
        return record;
    }

    public static MarkerInventory Read(string directory, QueryPeriod period)
    {
        if (!Directory.Exists(directory)) return new([], 0, false);
        var files = Directory.EnumerateFiles(directory, "*.json").Take(5001).ToArray();
        var records = new List<UserMarker>(); var errors = 0;
        foreach (var file in files.Take(5000))
        {
            try
            {
                if (new FileInfo(file).Length > 16384 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                { errors++; continue; }
                var record = JsonSerializer.Deserialize<UserMarker>(File.ReadAllText(file), JsonDefaults.Compact);
                if (record is null || record.Note is null || record.Note.Length > 500 || record.Note.Any(char.IsControl)) { errors++; continue; }
                if (record.TimestampUtc >= period.FromUtc && record.TimestampUtc < period.ToUtc) records.Add(record);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { errors++; }
        }
        return new(records.OrderByDescending(m => m.TimestampUtc).Take(200).ToArray(), errors, files.Length > 5000 || records.Count > 200);
    }
}
