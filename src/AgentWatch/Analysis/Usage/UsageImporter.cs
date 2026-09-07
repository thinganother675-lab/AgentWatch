using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentWatch.Analysis.Usage;

public static class UsageImporter
{
    public sealed record SourceFile(string Agent, string Path);
    public sealed record Discovery(IReadOnlyList<SourceFile> Files, bool Truncated);
    public static Discovery Discover(string codexSessions, string claudeProjects, DateTimeOffset since)
    {
        var result = new List<SourceFile>(); var visited = 0; var truncated = false;
        foreach (var (agent, root) in new[] { ("codex", codexSessions), ("claude", claudeProjects) })
        {
            if (!Directory.Exists(root)) continue;
            var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0));
            while (queue.TryDequeue(out var current))
            {
                if (visited >= 20000) { truncated = true; break; }
                try
                {
                    if ((File.GetAttributes(current.Path) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
                    {
                        if (++visited > 20000) { truncated = true; break; }
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        { if (current.Depth < 5) queue.Enqueue((path, current.Depth + 1)); continue; }
                        if (Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(path) >= since.UtcDateTime)
                            result.Add(new(agent, path));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { truncated = true; }
            }
        }
        return new(result.OrderBy(f => File.GetLastWriteTimeUtc(f.Path)).ToArray(), truncated);
    }

    public static UsageImportResult Import(string directory, IReadOnlyList<SourceFile> files, long maximumBytes = 64L * 1024 * 1024,
        int maximumFiles = 100, CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 1024 * 1024 or > 256L * 1024 * 1024 || maximumFiles is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "Import budget is 1..256 MiB and 1..500 changed files.");
        if (files.Any(f => f.Agent is not ("codex" or "claude"))) throw new ArgumentException("Usage agent must be codex or claude.");
        using var store = new UsageStore(directory);
        long bytesRead = 0; var fileCount = 0; var considered = 0; var usageCount = 0; var activities = 0; var invalid = 0; var oversized = 0;
        var warnings = new List<string>(); var budget = false; var timer = Stopwatch.StartNew();
        foreach (var source in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytesRead >= maximumBytes || fileCount >= maximumFiles || timer.Elapsed > TimeSpan.FromSeconds(60)) { budget = true; break; }
            considered++;
            if (source.Agent is not ("codex" or "claude")) throw new ArgumentException("Usage agent must be codex or claude.");
            var fullPath = Path.GetFullPath(source.Path); var id = UsageAdapters.StableId(fullPath.ToUpperInvariant());
            try
            {
                AgentWatch.Hosting.ServiceInstaller.EnsureNoReparse(fullPath);
                using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
                var cursor = store.Cursor(id); var length = input.Length;
                var prefixLength = cursor.PrefixLength > 0 ? cursor.PrefixLength : (int)Math.Min(4096, length);
                var prefix = new byte[prefixLength]; var actualPrefix = input.Read(prefix, 0, prefix.Length); bytesRead += actualPrefix;
                var hash = Convert.ToHexString(SHA256.HashData(prefix.AsSpan(0, actualPrefix)));
                if (length < cursor.Offset || cursor.PrefixHash is not null && cursor.PrefixHash != hash)
                {
                    cursor = new() { SourceWasReplaced = true };
                    if (warnings.Count < 20) warnings.Add($"Source {id[..12]} was truncated/replaced; reparsed, existing deduplicated accounting history retained.");
                }
                cursor.PrefixHash = hash; cursor.PrefixLength = actualPrefix;
                if (cursor.Offset >= length) continue;
                input.Position = cursor.Offset; fileCount++;
                var beforeOffset = cursor.Offset;
                store.ImportFile(id, cursor, (saveUsage, saveActivity) =>
                {
                    foreach (var line in Lines(input, length, Math.Max(0, maximumBytes - bytesRead), count => bytesRead += count, cancellationToken))
                    {
                        cursor.Offset = line.EndOffset;
                        if (line.TooLarge) { oversized++; cursor.OversizedLines++; continue; }
                        try
                        {
                            using var json = JsonDocument.Parse(line.Utf8!, new JsonDocumentOptions { MaxDepth = 64 });
                            UsageAdapters.Parse(source.Agent, json.RootElement, cursor, id, line.EndOffset,
                                entry => { saveUsage(entry); usageCount++; }, entry => { saveActivity(entry); activities++; });
                        }
                        catch (JsonException) { invalid++; cursor.InvalidRecords++; }
                        if (timer.Elapsed > TimeSpan.FromSeconds(60)) { budget = true; break; }
                    }
                });
                if (cursor.Offset < length && warnings.Count < 20)
                    warnings.Add($"Source {id[..12]} has unread or incomplete trailing data; next usage-sync resumes at byte {cursor.Offset}.");
                if (cursor.Offset == beforeOffset && input.Position > beforeOffset) budget = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { invalid++; if (warnings.Count < 20) warnings.Add($"Source {id[..12]} unavailable: {e.GetType().Name}."); }
        }
        var result = new UsageImportResult(DateTimeOffset.UtcNow, considered, fileCount, bytesRead, usageCount, activities, invalid, oversized,
            budget || bytesRead >= maximumBytes, warnings);
        store.RecordImport(result); return result;
    }

    private sealed record Line(long EndOffset, byte[]? Utf8, bool TooLarge);
    private static IEnumerable<Line> Lines(FileStream stream, long snapshotLength, long budget, Action<int> counted, CancellationToken cancellationToken)
    {
        const int maximumLine = 1024 * 1024;
        var buffer = new byte[65536]; using var line = new MemoryStream(); var large = false;
        long readTotal = 0;
        while (stream.Position < snapshotLength && readTotal < budget)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = stream.Position;
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, Math.Min(snapshotLength - start, budget - readTotal)));
            if (count == 0) break; readTotal += count; counted(count);
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    if (line.Length > 0 || large) yield return new(start + i + 1, large ? null : line.ToArray(), large);
                    else yield return new(start + i + 1, null, true);
                    line.SetLength(0); large = false;
                }
                else if (!large)
                {
                    if (line.Length >= maximumLine) { large = true; line.SetLength(0); }
                    else line.WriteByte(buffer[i]);
                }
            }
        }
        // An unfinished last line is deliberately not committed: concurrent append may complete it later.
    }
}
