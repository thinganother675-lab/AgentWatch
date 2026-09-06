using System.Diagnostics;
using System.Text.Json;
using AgentWatch.Aggregation;
using AgentWatch.Analysis;
using AgentWatch.Collectors;
using AgentWatch.Configuration;
using AgentWatch.Models;
using AgentWatch.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;

namespace AgentWatch.Hosting;

public sealed class WatchRuntime(WatchConfig config, string dataDirectory, LocalLog log)
{
    public async Task Run(CancellationToken cancellationToken)
    {
        using var database = new AgentWatchDatabase(Path.Combine(dataDirectory, "agentwatch.db"));
        using var engine = new CollectorEngine(config, dataDirectory, log);
        var aggregator = new SampleAggregator(); var detector = new AnomalyDetector(config.Thresholds);
        var queue = new Queue<PersistBatch>(); var collecting = new PersistBatch();
        var startedUtc = DateTimeOffset.UtcNow; var startedTick = Stopwatch.GetTimestamp();
        var lastHot = startedTick; var lastNvme = startedTick; var lastFlush = startedTick;
        var lastRetention = startedTick - (long)(Stopwatch.Frequency * 86400.0);
        using (var reader = AgentWatchDatabase.OpenReadOnly(database.Path))
        {
            var metadata = HistoryReader.Metadata(reader);
            if (metadata.TryGetValue("lastRetentionUtc", out var retentionText) && DateTimeOffset.TryParse(retentionText, out var retentionUtc))
                lastRetention = startedTick - (long)(Stopwatch.Frequency * Math.Clamp((startedUtc - retentionUtc).TotalSeconds, 0, 86400));
            if (metadata.TryGetValue("lastNvme", out var smart)) detector.PreviousNvme = JsonSerializer.Deserialize<NvmeHealth>(smart, JsonDefaults.Compact);
            if (metadata.TryGetValue("lastSampleUtc", out var oldText) && DateTimeOffset.TryParse(oldText, out var oldUtc))
                detector.Event(startedUtc, "service-restart-gap", "info", "Service restarted; process counters are re-baselined and unobserved time is excluded.",
                    new() { ["secondsSinceLastPersistedSample"] = Math.Max(0, (startedUtc - oldUtc).TotalSeconds) });
        }
        log.Write("INFO", "AgentWatch 0.1.0 starting; local collection only.");
        var latest = engine.Collect();
        collecting.HotFiles.AddRange(engine.CollectHotFiles()); detector.EvaluateHotFiles(collecting.HotFiles);
        engine.StartNvme();

        void Capture()
        {
            latest = engine.Collect();
            if (latest.Gap)
                detector.Event(latest.TimestampUtc, "monitoring-gap", "info", "Sleep, delayed sampling or a clock correction interrupted observation; rate baselines reset.",
                    new() { ["monotonicSeconds"] = engine.LastInterval.Seconds, ["wallSeconds"] = engine.LastInterval.WallSeconds, ["missedSamples"] = engine.LastInterval.MissedSamples });
            aggregator.Add(latest); detector.Evaluate(latest);
            collecting.Frames.AddRange(aggregator.Drain(latest.TimestampUtc));
            if (engine.TryCompleteNvme(out var nvme))
            {
                lastNvme = Stopwatch.GetTimestamp();
                if (nvme is not null) { collecting.Nvme.Add(nvme); detector.EvaluateNvme(nvme); }
            }
        }
        void Flush(bool shutdown = false)
        {
            if (shutdown) collecting.Frames.AddRange(aggregator.Drain(DateTimeOffset.UtcNow, true));
            collecting.Anomalies.AddRange(detector.Drain());
            collecting.Metadata["appVersion"] = "0.1.0";
            collecting.Metadata["startedUtc"] = startedUtc.ToString("O");
            collecting.Metadata["lastSampleUtc"] = latest.TimestampUtc.ToString("O");
            collecting.Metadata["lastFlushUtc"] = DateTimeOffset.UtcNow.ToString("O");
            collecting.Metadata["lastSnapshot"] = JsonSerializer.Serialize(latest, JsonDefaults.Compact);
            collecting.Metadata["collectors"] = JsonSerializer.Serialize(engine.Health.States, JsonDefaults.Compact);
            collecting.Metadata["state"] = shutdown ? "stopped" : "running";
            collecting.Metadata["processId"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            collecting.Metadata["uptimeSeconds"] = Stopwatch.GetElapsedTime(startedTick).TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (detector.PreviousNvme is { } smart) collecting.Metadata["lastNvme"] = JsonSerializer.Serialize(smart, JsonDefaults.Compact);
            queue.Enqueue(collecting); collecting = new();
            var maximumBatches = Math.Max(12, 360 / config.Sampling.PersistBatchMinutes);
            while (queue.Count > maximumBatches)
            {
                var dropped = queue.Dequeue();
                detector.Event(DateTimeOffset.UtcNow, "history-dropped", "warning", "Storage remained unavailable; oldest RAM batch dropped to bound monitor memory.",
                    new() { ["droppedObservedSeconds"] = dropped.Frames.Sum(f => f[Metric.Coverage].Sum) });
            }
            while (queue.TryPeek(out var batch))
            {
                try
                {
                    database.Persist(batch); queue.Dequeue();
                    engine.LastFlushMilliseconds = database.LastFlushMilliseconds;
                    engine.Health.Record("Storage", "OK", null, true, false);
                }
                catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
                {
                    engine.Health.Record("Storage", "ERROR", $"{e.GetType().Name}: {e.Message}; bounded pending batches={queue.Count}", false, true);
                    if (shutdown) log.Write("ERROR", $"Shutdown could not persist {queue.Count} pending batches; existing history was preserved.");
                    break;
                }
            }
        }
        Flush();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.Sampling.FastSampleSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Capture();
                if (Stopwatch.GetElapsedTime(lastHot).TotalSeconds >= config.Sampling.HotFilesSeconds)
                {
                    var files = engine.CollectHotFiles(); collecting.HotFiles.AddRange(files); detector.EvaluateHotFiles(files); lastHot = Stopwatch.GetTimestamp();
                }
                if (Stopwatch.GetElapsedTime(lastNvme).TotalMinutes >= config.Sampling.NvmeHealthMinutes)
                { engine.StartNvme(); lastNvme = Stopwatch.GetTimestamp(); }
                if (Stopwatch.GetElapsedTime(lastFlush).TotalMinutes >= config.Sampling.PersistBatchMinutes)
                { Flush(); lastFlush = Stopwatch.GetTimestamp(); }
                if (Stopwatch.GetElapsedTime(lastRetention).TotalDays >= 1)
                {
                    try { database.ApplyRetention(DateTimeOffset.UtcNow, config.Retention, cancellationToken); engine.Health.Record("Retention", "OK", null, true, false); }
                    catch (Exception e) when (e is SqliteException or IOException)
                    { engine.Health.Record("Retention", "ERROR", e.Message, false, true); }
                    lastRetention = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            Capture(); detector.CloseAll(DateTimeOffset.UtcNow); Flush(true);
            log.Write("INFO", "AgentWatch stopped; final flush attempted.");
        }
    }
}

public sealed class MonitorWorker(WatchRuntime runtime, IHostApplicationLifetime lifetime, LocalLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await runtime.Run(stoppingToken); }
        catch (Exception e)
        {
            log.Write("ERROR", $"Fatal monitor failure: {e.GetType().Name}: {e.Message}");
            // StopHost reports a clean SERVICE_STOPPED; an SCM-hosted fatal failure must terminate nonzero for recovery.
            if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()) Environment.Exit(1);
            Environment.ExitCode = 1; lifetime.StopApplication();
        }
    }
}
