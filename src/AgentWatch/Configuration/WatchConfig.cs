using System.Text.Json;

namespace AgentWatch.Configuration;

public sealed class WatchConfig
{
    public string? MonitoredUserProfile { get; set; }
    public string? ReaderSid { get; set; }
    public int PhysicalDriveNumber { get; set; }
    public SamplingConfig Sampling { get; set; } = new();
    public RetentionConfig Retention { get; set; } = new();
    public ThresholdConfig Thresholds { get; set; } = new();
    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AgentWatch");

    public static WatchConfig Load(string dataDirectory, out List<string> warnings)
    {
        warnings = [];
        var path = Path.Combine(dataDirectory, "config.json");
        WatchConfig config;
        if (!File.Exists(path)) config = new();
        else
        {
            if (new FileInfo(path).Length > 131_072) throw new InvalidDataException("AgentWatch config.json exceeds 128 KiB.");
            try { config = JsonSerializer.Deserialize<WatchConfig>(File.ReadAllText(path), JsonDefaults.Options) ?? new(); }
            catch (JsonException e) { throw new InvalidDataException($"Invalid AgentWatch config.json at line {e.LineNumber}; correct it before starting."); }
        }
        config.Validate(warnings);
        return config;
    }

    public void Validate(List<string> warnings)
    {
        Sampling ??= new(); Retention ??= new(); Thresholds ??= new();
        int Safe(int value, int min, int max, int fallback, string field)
        {
            if (value >= min && value <= max) return value;
            warnings.Add($"{field} is outside {min}..{max}; using {fallback}."); return fallback;
        }
        Sampling.FastSampleSeconds = Safe(Sampling.FastSampleSeconds, 2, 60, 10, "fastSampleSeconds");
        Sampling.PersistBatchMinutes = Safe(Sampling.PersistBatchMinutes, 1, 30, 5, "persistBatchMinutes");
        Sampling.HotFilesSeconds = Safe(Sampling.HotFilesSeconds, 30, 3600, 60, "hotFilesSeconds");
        Sampling.NvmeHealthMinutes = Safe(Sampling.NvmeHealthMinutes, 1, 1440, 15, "nvmeHealthMinutes");
        Retention.MinuteDays = Safe(Retention.MinuteDays, 1, 90, 14, "minuteDays");
        Retention.FifteenMinuteDays = Safe(Retention.FifteenMinuteDays, Retention.MinuteDays + 1, 730, 180, "fifteenMinuteDays");
        Retention.DailyDays = Safe(Retention.DailyDays, 0, 36500, 0, "dailyDays");
        if (Retention.DailyDays > 0 && Retention.DailyDays <= Retention.FifteenMinuteDays)
        { Retention.DailyDays = 0; warnings.Add("dailyDays must exceed fifteenMinuteDays; keeping daily history indefinitely."); }
        PhysicalDriveNumber = Safe(PhysicalDriveNumber, 0, 255, 0, "physicalDriveNumber");
        Thresholds.RamWarningMiB = Safe(Thresholds.RamWarningMiB, 64, 32768, 512, "ramWarningMiB");
        Thresholds.RamCriticalMiB = Safe(Thresholds.RamCriticalMiB, 32, Thresholds.RamWarningMiB, Math.Min(256, Thresholds.RamWarningMiB), "ramCriticalMiB");
        Thresholds.RamSustainSeconds = Safe(Thresholds.RamSustainSeconds, 20, 3600, 120, "ramSustainSeconds");
        Thresholds.CriticalPageOutputPerSecond = Safe(Thresholds.CriticalPageOutputPerSecond, 1, 1_000_000, 100, "criticalPageOutputPerSecond");
        Thresholds.CodexWriteWarningMiBPerMinute = Safe(Thresholds.CodexWriteWarningMiBPerMinute, 1, 100000, 100, "codexWriteWarningMiBPerMinute");
        Thresholds.CodexWriteCriticalMiBPerMinute = Safe(Thresholds.CodexWriteCriticalMiBPerMinute, 1, 100000, 500, "codexWriteCriticalMiBPerMinute");
        Thresholds.CodexWriteWarningSeconds = Safe(Thresholds.CodexWriteWarningSeconds, 20, 86400, 600, "codexWriteWarningSeconds");
        Thresholds.CodexWriteCriticalSeconds = Safe(Thresholds.CodexWriteCriticalSeconds, 20, 86400, 300, "codexWriteCriticalSeconds");
        Thresholds.HotWalMiB = Safe(Thresholds.HotWalMiB, 1, 100000, 512, "hotWalMiB");
        Thresholds.HotGrowthMiBPerTenMinutes = Safe(Thresholds.HotGrowthMiBPerTenMinutes, 1, 100000, 500, "hotGrowthMiBPerTenMinutes");
        Thresholds.SsdWarningC = Safe(Thresholds.SsdWarningC, 30, 120, 65, "ssdWarningC");
        Thresholds.SsdCriticalC = Safe(Thresholds.SsdCriticalC, Thresholds.SsdWarningC, 150, Math.Max(70, Thresholds.SsdWarningC), "ssdCriticalC");
        Thresholds.OwnDatabaseMiB = Safe(Thresholds.OwnDatabaseMiB, 16, 10000, 256, "ownDatabaseMiB");
        Thresholds.OwnWalMiB = Safe(Thresholds.OwnWalMiB, 4, 10000, 32, "ownWalMiB");
        if (MonitoredUserProfile is not null && !Path.IsPathFullyQualified(MonitoredUserProfile))
            throw new InvalidDataException("monitoredUserProfile must be an absolute Windows profile path.");
    }
}

public sealed class SamplingConfig
{
    public int FastSampleSeconds { get; set; } = 10;
    public int PersistBatchMinutes { get; set; } = 5;
    public int HotFilesSeconds { get; set; } = 60;
    public int NvmeHealthMinutes { get; set; } = 15;
}
public sealed class RetentionConfig
{
    public int MinuteDays { get; set; } = 14;
    public int FifteenMinuteDays { get; set; } = 180;
    public int DailyDays { get; set; }
}
public sealed class ThresholdConfig
{
    public int RamWarningMiB { get; set; } = 512;
    public int RamCriticalMiB { get; set; } = 256;
    public int RamSustainSeconds { get; set; } = 120;
    public int CriticalPageOutputPerSecond { get; set; } = 100;
    public int CodexWriteWarningMiBPerMinute { get; set; } = 100;
    public int CodexWriteCriticalMiBPerMinute { get; set; } = 500;
    public int CodexWriteWarningSeconds { get; set; } = 600;
    public int CodexWriteCriticalSeconds { get; set; } = 300;
    public int HotWalMiB { get; set; } = 512;
    public int HotGrowthMiBPerTenMinutes { get; set; } = 500;
    public int SsdWarningC { get; set; } = 65;
    public int SsdCriticalC { get; set; } = 70;
    public int OwnDatabaseMiB { get; set; } = 256;
    public int OwnWalMiB { get; set; } = 32;
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, IncludeFields = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
