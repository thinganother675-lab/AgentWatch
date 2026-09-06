using AgentWatch.Models;

namespace AgentWatch.Collectors;

public sealed class HotFileCollector(string? profile)
{
    public static readonly string[] FileNames = ["logs_2.sqlite", "logs_2.sqlite-wal", "state_5.sqlite", "state_5.sqlite-wal"];
    private readonly Dictionary<string, long> previous = new();
    public List<HotFileSample> Read(DateTimeOffset now)
    {
        var result = new List<HotFileSample>(4);
        foreach (var name in FileNames)
        {
            if (profile is null) { result.Add(new(now, name, false, null, null, null, "No monitored user profile configured.")); continue; }
            try
            {
                var folder = Path.Combine(profile, ".codex");
                var path = Path.Combine(folder, name);
                foreach (var item in new[] { profile, folder, path })
                    if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Monitoring reparse points is disabled.");
                var info = new FileInfo(path);
                var size = info.Length;
                long? delta = previous.TryGetValue(name, out var old) ? size - old : null;
                result.Add(new(now, name, true, size, delta, new DateTimeOffset(info.LastWriteTimeUtc)));
                previous[name] = size;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            { previous.Remove(name); result.Add(new(now, name, false, null, null, null)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { previous.Remove(name); result.Add(new(now, name, false, null, null, null, e is UnauthorizedAccessException ? "Access denied to file metadata." : "Metadata unavailable or reparse point.")); }
        }
        return result;
    }
}
