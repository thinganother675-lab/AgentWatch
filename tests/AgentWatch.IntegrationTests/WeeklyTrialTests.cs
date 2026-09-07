using System.Text;
using System.Text.Json;
using AgentWatch.Aggregation;
using AgentWatch.Analysis;
using AgentWatch.Cli;
using AgentWatch.Configuration;
using AgentWatch.Hosting;
using AgentWatch.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.IntegrationTests;

[TestClass]
public sealed class WeeklyTrialTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
    private static AggregateFrame Frame(DateTimeOffset start, double cpu = 30, double memoryMiB = 1000, double seconds = 60, bool paging = true)
    {
        var f = new AggregateFrame(start.ToUnixTimeSeconds());
        f.Add(Metric.Coverage, 1, seconds); f.Add(Metric.CpuTotal, cpu, seconds);
        f.Add(Metric.AvailableMemory, memoryMiB * 1048576, seconds);
        f.Add(Metric.MemoryBelow512, memoryMiB < 512 ? 1 : 0, seconds);
        f.Add(Metric.CodexActive, 1, seconds); f.Add(Metric.CodexCpu, cpu / 2, seconds); f.Add(Metric.CodexLogicalWrite, 1024, seconds);
        if (paging)
        { f.Add(Metric.PagesOutput, 10, seconds); f.Add(Metric.EstimatedPageOut, 40960, seconds); f.Add(Metric.PhysicalDiskWrite, 81920, seconds); f.Add(Metric.DiskQueue, .1, seconds); f.Add(Metric.PressureWithPageOut, memoryMiB < 512 ? 1 : 0, seconds); }
        return f;
    }
    private static WeeklyReport Report(TestDirectory d, QueryPeriod? period = null)
        => new ReportService(d.DatabasePath).GetWeeklyReport(period ?? new(Start, Start.AddHours(1)),
            Path.Combine(d.DirectoryPath, "marks"), usageDirectory: Path.Combine(d.DirectoryPath, "usage"));

    [TestMethod] public void EmptyHistoryRemainsUnknownInsteadOfHealthy()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var r = Report(d); Assert.AreEqual("no-data", r.Quality.State);
        Assert.IsNull(r.Measurements.System.Cpu.TotalPercent.Average); Assert.AreEqual("low", r.Findings.Single().Confidence);
        Assert.IsTrue(r.Findings.Single().AlternativeExplanations.Count > 0); Assert.AreEqual(0, r.Episodes.Count);
    }
    [TestMethod] public void QualitySeparatesRequestedPeriodSpanAndMissingMetricCoverage()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var batch = new AgentWatch.Storage.PersistBatch(); batch.Frames.Add(Frame(Start, paging: false)); batch.Frames.Add(Frame(Start.AddMinutes(9), paging: false)); db.Persist(batch);
        var r = Report(d); Assert.AreEqual(3600, r.Quality.RequestedSeconds); Assert.AreEqual(600, r.Quality.StoredBucketSpanSeconds);
        Assert.AreEqual(120, r.Quality.ObservedSeconds); Assert.AreEqual(480, r.Quality.UnobservedWithinStoredSpanSeconds);
        Assert.AreEqual(0, r.Quality.Metrics.Single(m => m.Metric == "paging.pageOut").ValidSeconds);
        Assert.AreEqual("limited", r.Quality.State);
    }
    [TestMethod] public void FractionalRequestDoesNotAdvertiseMoreThan100PercentCoverage()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var batch = new AgentWatch.Storage.PersistBatch(); batch.Frames.Add(Frame(Start)); db.Persist(batch);
        var r = Report(d, new(Start.AddSeconds(1), Start.AddSeconds(2)));
        Assert.IsTrue(r.Quality.BoundaryBucketsExtendOutsideRequest); Assert.IsNull(r.Quality.PercentOfRequestedPeriod);
        Assert.AreEqual(60, r.Measurements.Period.ObservedSeconds);
    }
    [TestMethod] public void MarkerGetsAllThirtyMinutesIncludingOutsideRequestedReportEdges()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var batch = new AgentWatch.Storage.PersistBatch();
        for (var minute = -15; minute < 15; minute++) batch.Frames.Add(Frame(Start.AddMinutes(minute), cpu: minute < 0 ? 20 : 60)); db.Persist(batch);
        var mark = UserMarkers.Add(Path.Combine(d.DirectoryPath, "marks"), "slow", Start);
        var r = Report(d, new(Start, Start.AddMinutes(1))); var context = r.Contexts.Single();
        Assert.AreEqual(mark.Id, context.SubjectId); Assert.AreEqual(1800, context.ObservedSeconds);
        Assert.AreEqual(40, context.CpuPercent.Average); Assert.AreEqual(60, r.Measurements.Period.ObservedSeconds);
        Assert.AreEqual(60, r.Measurements.System.Cpu.TotalPercent.Average);
        Assert.AreEqual(Start.AddMinutes(-15), context.FromUtc); Assert.AreEqual(Start.AddMinutes(15), context.ToUtc);
    }
    [TestMethod] public void PeriodTotalsAloneDoNotCreateMemoryPagingDiagnosis()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var batch = new AgentWatch.Storage.PersistBatch();
        for (var i = 0; i < 5; i++)
        {
            var f = Frame(Start.AddMinutes(i), memoryMiB: 200); f.Metrics[(int)Metric.PressureWithPageOut] = new();
            f.Add(Metric.PressureWithPageOut, 0, 60); batch.Frames.Add(f);
        }
        db.Persist(batch); var r = Report(d);
        Assert.IsFalse(r.Findings.Any(f => f.Code == "memory-paging")); Assert.IsTrue(r.Findings.Any(f => f.Code == "low-memory"));
    }
    [TestMethod] public void SustainedOverlapHasEvidenceConfidenceAndAlternativeExplanations()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var batch = new AgentWatch.Storage.PersistBatch(); for (var i = 0; i < 4; i++) batch.Frames.Add(Frame(Start.AddMinutes(i), memoryMiB: 200)); db.Persist(batch);
        var r = Report(d); var finding = r.Findings.Single(f => f.Code == "memory-paging");
        Assert.AreEqual("medium", finding.Confidence); Assert.IsTrue(finding.Evidence.Count > 0); Assert.IsTrue(finding.AlternativeExplanations.Count > 0);
        Assert.AreEqual(240, r.Episodes.Single().ObservedSeconds);
        var markdown = WeeklyText.Render(r); StringAssert.Contains(markdown, "FACTS"); StringAssert.Contains(markdown, "INTERPRETATION");
    }
    [TestMethod] public void EpisodeSelectionIsBoundedAndDoesNotBridgeMissingMinutes()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var batch = new AgentWatch.Storage.PersistBatch(); for (var i = 0; i < 30; i++) batch.Frames.Add(Frame(Start.AddMinutes(i * 2), cpu: 90)); db.Persist(batch);
        var r = Report(d); Assert.AreEqual(30, r.MatchingEpisodeCount); Assert.AreEqual(12, r.Episodes.Count);
        Assert.IsTrue(r.Episodes.All(e => e.ToUtc - e.FromUtc == TimeSpan.FromMinutes(1)));
    }
    [TestMethod] public void HistoricalQueryShowsCurrentMetadataAsCurrentNotHistoricalCoverage()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var batch = new AgentWatch.Storage.PersistBatch(); batch.Frames.Add(Frame(Start));
        batch.Metadata["lastSampleUtc"] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O"); batch.Metadata["state"] = "stopped";
        batch.Metadata["collectors"] = JsonSerializer.Serialize(new[] { new CollectorHealth("Nvme", "ERROR", "not supported", null, DateTimeOffset.UtcNow, 1) }, JsonDefaults.Compact);
        db.Persist(batch); var r = Report(d); Assert.AreEqual("stale", r.Quality.Freshness);
        Assert.AreEqual("stopped", r.Quality.LastRecordedState); Assert.AreEqual(60, r.Quality.Metrics.Single(m => m.Metric == "system.cpu").ValidSeconds);
    }
    [TestMethod] public void DailyRollupsAreNotPresentedAsPreciseEpisodes()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var batch = new AgentWatch.Storage.PersistBatch(); batch.Frames.Add(Frame(Start, cpu: 99)); db.Persist(batch);
        db.ApplyRetention(Start.AddDays(200), new()); var r = Report(d, new(Start.Date, Start.Date.AddDays(1)));
        Assert.AreEqual(86400, r.Measurements.Period.MaximumResolutionSeconds); Assert.AreEqual(0, r.Episodes.Count);
    }
    [TestMethod] public void MarkerSyntaxEscapingAndEventIdAreStable()
    {
        using var d = new TestDirectory(); using var db = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath);
        var options = new CommandOptions(["mark", "ноут начал тормозить", "--at", Start.ToString("O")]); Assert.AreEqual("ноут начал тормозить", options["--note"]);
        Assert.ThrowsExactly<ArgumentException>(() => new CommandOptions(["mark", "one", "--note", "two"]));
        var note = "<script>alert(1)</script> [link](https://example.invalid) | test";
        var marker = UserMarkers.Add(Path.Combine(d.DirectoryPath, "marks"), note, Start);
        var r = Report(d); var first = r.Events.Single(); var next = Report(d).Events.Single(); Assert.AreEqual(first.Id, next.Id);
        Assert.AreEqual("mark:" + marker.Id, first.Id); Assert.AreEqual("user.mark", first.Type);
        var markdown = WeeklyText.Render(r); Assert.IsFalse(markdown.Contains("<script>", StringComparison.Ordinal));
        Assert.ThrowsExactly<ArgumentException>(() => UserMarkers.Add(d.DirectoryPath, "bad\u001b", Start));
        Assert.ThrowsExactly<ArgumentException>(() => UserMarkers.Add(d.DirectoryPath, "future", DateTimeOffset.UtcNow.AddDays(1)));
    }
    [TestMethod] public void EachRunAndEveryRotatedSegmentSurviveSubsequentRuns()
    {
        using var d = new TestDirectory(); var legacy = Path.Combine(d.DirectoryPath, "agentwatch.log"); File.WriteAllText(legacy, "legacy-do-not-touch");
        var first = new LocalLog(d.DirectoryPath, 1024); first.BeginRun(new(), "test");
        for (var i = 0; i < 6; i++) first.Write("WARN", new string('x', 1500)); first.Complete("stopped");
        var segments = Directory.GetFiles(first.RunDirectory, "*.log"); Assert.IsTrue(segments.Length >= 5);
        var prior = segments.ToDictionary(path => path, File.ReadAllText);
        var second = new LocalLog(d.DirectoryPath, 1024); second.BeginRun(new(), "test"); second.Complete("failed");
        foreach (var (path, text) in prior) Assert.AreEqual(text, File.ReadAllText(path));
        Assert.AreEqual("legacy-do-not-touch", File.ReadAllText(legacy)); Assert.AreNotEqual(first.RunId, second.RunId);
        var history = RunJournal.Read(d.DirectoryPath, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.AreEqual(2, history.Runs.Count); Assert.IsTrue(history.Runs.All(r => r.EndedUtc.HasValue)); Assert.AreEqual(0, first.Failures);
    }
    [TestMethod] public void UnfinishedRunDoesNotInventStopOrRunningState()
    {
        using var d = new TestDirectory(); var log = new LocalLog(d.DirectoryPath); log.BeginRun(new(), "test");
        var run = RunJournal.Read(d.DirectoryPath, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)).Runs.Single();
        Assert.IsNull(run.EndedUtc); Assert.AreEqual("started", run.Outcome);
    }
    [TestMethod] public void WeeklyRangeIsBounded()
    {
        using var d = new TestDirectory(); Assert.ThrowsExactly<ArgumentException>(() => Report(d, new(Start, Start.AddDays(32))));
    }
}
