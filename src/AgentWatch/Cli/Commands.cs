using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentWatch.Analysis;
using AgentWatch.Collectors;
using AgentWatch.Configuration;
using AgentWatch.Hosting;
using AgentWatch.Models;
using AgentWatch.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentWatch.Cli;

public sealed class CommandOptions
{
    public string Command { get; }
    private readonly Dictionary<string, string?> values = new(StringComparer.Ordinal);
    public string? this[string key] => values.GetValueOrDefault(key);
    public bool Has(string key) => values.ContainsKey(key);
    public CommandOptions(string[] args)
    {
        Command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        if (Command is "--help" or "-h") Command = "help";
        if (Command is "--version") Command = "version";
        var allowed = new HashSet<string>(["--data-dir", "--json", "--help"]);
        if (Command is "report" or "agents" or "disk" or "anomalies" or "self") allowed.UnionWith(["--since", "--from", "--to"]);
        if (Command == "run") allowed.Add("--duration");
        if (Command == "install") allowed.UnionWith(["--profile", "--reader-sid"]);
        if (Command == "uninstall") allowed.UnionWith(["--purge-data", "--confirm-purge"]);
        for (var i = 1; i < args.Length; i++)
        {
            var key = args[i];
            if (!allowed.Contains(key) || values.ContainsKey(key)) throw new ArgumentException("Unknown or repeated option: " + key);
            if (key is "--json" or "--help" or "--purge-data" or "--confirm-purge") values.Add(key, null);
            else
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Missing value for " + key);
                values.Add(key, args[i]);
            }
        }
    }
    public static TimeSpan Duration(string text)
    {
        var match = Regex.Match(text, @"^(\d+(?:\.\d+)?)(s|m|h|d)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) throw new ArgumentException("Duration must be a positive number followed by s, m, h or d (for example 7d).");
        var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var multiplier = match.Groups[2].Value switch { "s" => 1, "m" => 60, "h" => 3600, _ => 86400 };
        var seconds = number * multiplier;
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 86400.0 * 36500) throw new ArgumentException("Duration outside supported range.");
        return TimeSpan.FromSeconds(seconds);
    }
    public QueryPeriod Period()
    {
        if (Has("--from") && Has("--since")) throw new ArgumentException("Use --from or --since, not both.");
        static DateTimeOffset Utc(string text)
        {
            if (!Regex.IsMatch(text, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
                throw new ArgumentException("Timestamps must be ISO 8601 with Z or an explicit offset.");
            return value.ToUniversalTime();
        }
        var to = this["--to"] is { } end ? Utc(end) : DateTimeOffset.UtcNow;
        var from = this["--from"] is { } start ? Utc(start) : to - Duration(this["--since"] ?? "24h");
        if (from >= to) throw new ArgumentException("Report start must precede end.");
        return new(from, to);
    }
}

public sealed record StatusResult(string AppVersion, string ServiceState, string DatabasePath, bool DatabaseExists,
    long DatabaseBytes, long WalBytes, DateTimeOffset? LastPersistedSampleUtc, double? PersistenceLagSeconds,
    string? RecordedInstanceState, double? RecordedUptimeSeconds, IReadOnlyList<CollectorHealth> Collectors, string? Error);
public sealed record DoctorCheck(string State, string Name, string Detail);
public sealed record DoctorResult(string AppVersion, DateTimeOffset TimestampUtc, IReadOnlyList<DoctorCheck> Checks, SnapshotResult Snapshot, StatusResult Status);

public static class Commands
{
    public static async Task<int> Run(string[] args)
    {
        var options = new CommandOptions(args);
        if (options.Command == "help" || options.Has("--help")) { Help(); return 0; }
        if (options.Command == "version") { Console.WriteLine("AgentWatch 0.1.0 / report schema 1 / database schema 1"); return 0; }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("AgentWatch supports Windows 11 x64 only.");
        var data = Path.GetFullPath(options["--data-dir"] ?? WatchConfig.DefaultDataDirectory);
        if (options.Command is "install" or "uninstall")
        {
            if (options.Has("--data-dir")) throw new ArgumentException("Installation always uses protected ProgramData/AgentWatch; --data-dir is for collection and queries.");
            if (options.Command == "install") await ServiceInstaller.Install(options["--profile"], options["--reader-sid"]);
            else ServiceInstaller.Uninstall(options.Has("--purge-data"), options.Has("--confirm-purge"));
            return 0;
        }
        var config = WatchConfig.Load(data, out var warnings);
        foreach (var warning in warnings) Console.Error.WriteLine("WARN: " + warning);
        switch (options.Command)
        {
            case "snapshot":
                using (var engine = new CollectorEngine(config, data))
                { var snapshot = await engine.Snapshot(); if (options.Has("--json")) Json(snapshot); else TextOutput.Snapshot(snapshot); }
                return 0;
            case "status":
                var status = Status(data); if (options.Has("--json")) Json(status); else TextOutput.Status(status);
                return status.Error is null ? 0 : 1;
            case "doctor":
                var doctor = await Doctor(config, data); if (options.Has("--json")) Json(doctor); else TextOutput.Doctor(doctor);
                return doctor.Checks.Any(c => c.State == "ERROR") ? 1 : 0;
            case "run":
                if (options["--duration"] is not { } durationText) throw new ArgumentException("Foreground validation requires --duration (for example run --duration 6m --data-dir artifacts/smoke).");
                using (var stop = new CancellationTokenSource(CommandOptions.Duration(durationText)))
                {
                    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
                    Console.CancelKeyPress += handler;
                    try { using var runHost = BuildHost(config, data); await runHost.RunAsync(stop.Token); }
                    finally { Console.CancelKeyPress -= handler; }
                }
                Console.WriteLine(Environment.ExitCode == 0 ? "AgentWatch foreground collection stopped and final flush attempted." : "AgentWatch failed; see its diagnostic log."); return Environment.ExitCode;
            case "service":
                using (var host = BuildHost(config, data)) await host.RunAsync(); return Environment.ExitCode;
            case "report": case "agents": case "disk": case "anomalies": case "self":
                var report = new ReportService(Path.Combine(data, "agentwatch.db"), config.Thresholds, config.PhysicalDriveNumber).GetReport(options.Period());
                if (options.Has("--json")) Json(report); else TextOutput.Report(report, options.Command);
                return 0;
            default: throw new ArgumentException("Unknown command: " + options.Command + ". Use AgentWatch.exe help.");
        }
    }
    private static IHost BuildHost(WatchConfig config, string data)
    {
        Directory.CreateDirectory(data);
        var log = new LocalLog(data);
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [], ContentRootPath = data });
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName);
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(new LocalLogProvider(log));
        builder.Services.AddSingleton(log); builder.Services.AddSingleton(new WatchRuntime(config, data, log));
        builder.Services.AddHostedService<MonitorWorker>(); builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        return builder.Build();
    }
    public static StatusResult Status(string data)
    {
        var path = Path.Combine(data, "agentwatch.db");
        var state = ServiceInstaller.GetState(); var exists = File.Exists(path);
        var metadata = new Dictionary<string, string>(); string? error = null;
        if (exists)
        {
            try { using var connection = AgentWatchDatabase.OpenReadOnly(path); metadata = HistoryReader.Metadata(connection); }
            catch (Exception e) when (e is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException) { error = e.Message; }
        }
        var last = metadata.TryGetValue("lastSampleUtc", out var time) && DateTimeOffset.TryParse(time, out var parsed) ? parsed : (DateTimeOffset?)null;
        var collectors = metadata.TryGetValue("collectors", out var health) ? JsonSerializer.Deserialize<List<CollectorHealth>>(health, JsonDefaults.Compact) ?? [] : new List<CollectorHealth>();
        double? uptime = metadata.TryGetValue("uptimeSeconds", out var up) && double.TryParse(up, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
        return new("0.1.0", state, path, exists, CollectorEngine.FileSize(path), CollectorEngine.FileSize(path + "-wal"), last,
            last.HasValue ? (DateTimeOffset.UtcNow - last.Value).TotalSeconds : null, metadata.GetValueOrDefault("state"), uptime, collectors, error);
    }
    private static async Task<DoctorResult> Doctor(WatchConfig config, string data)
    {
        var checks = new List<DoctorCheck> { new("OK", "Platform", $"Windows {Environment.OSVersion.Version}, x64, {Environment.ProcessorCount} logical processors") };
        var status = Status(data);
        checks.Add(new("OK", "Native SQLite", AgentWatchDatabase.NativeVersion + "; bundled native library, no system SQLite dependency."));
        checks.Add(new(status.ServiceState == "Running" ? "OK" : "WARN", "Windows service", status.ServiceState));
        checks.Add(new(Directory.Exists(data) ? "OK" : "WARN", "Data directory", data));
        checks.Add(new(ServiceInstaller.IsElevated() ? "OK" : "WARN", "Current privileges", ServiceInstaller.IsElevated() ? "Elevated; installer/service operations are available." : "Not elevated; service installation requires an administrator. Read-only reporting is supported."));
        if (status.DatabaseExists)
        {
            try
            {
                using var connection = AgentWatchDatabase.OpenReadOnly(status.DatabasePath);
                using var command = connection.CreateCommand(); command.CommandText = "PRAGMA quick_check(1)";
                var result = command.ExecuteScalar()?.ToString();
                checks.Add(new(result == "ok" ? "OK" : "ERROR", "Database integrity/schema/readability", $"Schema {AgentWatchDatabase.SchemaVersion}; quick_check={result}"));
            }
            catch (Exception e) when (e is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
            { checks.Add(new("ERROR", "Database readability", e.Message)); }
        }
        else checks.Add(new("WARN", "History", "No history database yet. Snapshot works independently; install or run collection to create history."));
        if (Directory.Exists(data))
        {
            try
            {
                var probe = Path.Combine(data, ".doctor-" + Guid.NewGuid().ToString("N"));
                using var handle = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                checks.Add(new("OK", "Current-token directory write access", "Own temporary zero-byte probe succeeded and is deleted on close."));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { checks.Add(new(ServiceInstaller.IsElevated() ? "ERROR" : "OK", "Current-token directory write access", "No write access; expected for a read-only reporting user.")); }
        }
        if (status.PersistenceLagSeconds is { } lag)
            checks.Add(new(lag <= config.Sampling.PersistBatchMinutes * 60 + config.Sampling.FastSampleSeconds * 3 ? "OK" : "WARN",
                "Persisted sample freshness", $"{lag:F0} seconds old; recorded instance state={status.RecordedInstanceState}."));
        foreach (var old in status.Collectors) checks.Add(new(old.State, "Persisted " + old.Name,
            $"Last successful observation={old.LastSuccessUtc:O}; failures={old.Errors}; {old.Detail}"));
        using var engine = new CollectorEngine(config, data); var snapshot = await engine.Snapshot();
        checks.Add(new(snapshot.MonitoredUserProfile is null ? "WARN" : "OK", "Monitored profile", snapshot.MonitoredUserProfile ?? "Configure monitoredUserProfile at installation; LocalSystem has a different profile."));
        foreach (var health in snapshot.Collectors) checks.Add(new(health.State == "ERROR" ? "WARN" : health.State, health.Name, health.Detail ?? "Native collector available."));
        checks.Add(new(snapshot.Nvme is not null ? "OK" : "WARN", "PhysicalDrive/NVMe", $"Configured PhysicalDrive{config.PhysicalDriveNumber}; SMART is {(snapshot.Nvme is null ? "unavailable for this token/driver" : "readable through IOCTL_STORAGE_QUERY_PROPERTY")}."));
        return new("0.1.0", DateTimeOffset.UtcNow, checks, snapshot, status);
    }
    public static void Json<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
    private static void Help() => Console.WriteLine("""
        AgentWatch 0.1.0 — local Windows AI-workload observability
        AgentWatch.exe install [--profile C:\Users\Name] [--reader-sid S-1-5-21-...]
        AgentWatch.exe uninstall [--purge-data --confirm-purge]
        AgentWatch.exe service
        AgentWatch.exe status | doctor | snapshot [--json]
        AgentWatch.exe report | agents | disk | anomalies | self [--since 24h|7d|30d] [--json]
        AgentWatch.exe report --from 2026-09-01T00:00:00Z --to 2026-09-06T00:00:00Z
        AgentWatch.exe run --duration 6m --data-dir C:\path\to\validation

        Collection/query commands accept --data-dir; default: %ProgramData%\AgentWatch.
        Configuration: config.json in the data directory. Defaults work without it.
        Installation requires an elevated PowerShell and the published single-file executable.
        Process logical I/O != Windows physical-disk throughput != NVMe host writes.
        No GUI, listener, network telemetry, conversation reads, or automatic optimization.
        """);
}
