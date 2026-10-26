using System.Security.Cryptography;
using System.Text.Json;
using AgentWatch.Aggregation;
using AgentWatch.Analysis;
using AgentWatch.Cli;
using AgentWatch.Collectors;
using AgentWatch.Configuration;
using AgentWatch.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.IntegrationTests;

[TestClass]
public sealed class AnalysisBundleTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-07T20:59:00Z");
    private static AggregateFrame Frame(DateTimeOffset time, double idle = 75)
    {
        var f = new AggregateFrame(time.ToUnixTimeSeconds());
        f.Add(Metric.Coverage, 1, 60); f.Add(Metric.CpuTotal, 20, 60);
        f.Add(Metric.DiskIdle, idle, 60); f.Add(Metric.PhysicalDiskWrite, 10, 60);
        return f;
    }

    [TestMethod] public void IdleValidationRejectsRatherThanClampsAndDoesNotRestrictThroughput()
    {
        foreach (var value in new double?[] { null, double.NaN, double.PositiveInfinity, -1, 160.7 })
            Assert.IsNull(MetricValidity.DiskIdle(value));
        Assert.AreEqual(0.0, MetricValidity.DiskIdle(0));
        Assert.AreEqual(100.0, MetricValidity.DiskIdle(100));
        var aggregator = new SampleAggregator();
        aggregator.Add(new(Start.AddSeconds(10), 10, null, null,
            new(null, null, null, null, 5000, 6000, 2, 160.7), [], null, 1, false));
        var f = aggregator.Drain(Start.AddMinutes(1), true).Single();
        Assert.AreEqual(0.0, f[Metric.DiskIdle].Weight);
        Assert.AreEqual(6000.0, f[Metric.PhysicalDiskWrite].Average);
    }

    [TestMethod] public void LegacyInvalidBucketReducesIdleCoverageOnlyAndDoesNotRewriteHistory()
    {
        using var dir = new TestDirectory(); using var writer = new AgentWatchDatabase(dir.DatabasePath);
        var batch = new PersistBatch(); batch.Frames.Add(Frame(Start, 160.7)); batch.Frames.Add(Frame(Start.AddMinutes(1)));
        writer.Persist(batch);
        var query = new ReportService(dir.DatabasePath);
        var report = query.GetReport(new(Start, Start.AddMinutes(2)));
        Assert.AreEqual(120.0, report.Period.ObservedSeconds);
        Assert.AreEqual(60.0, report.Storage.IdlePercent.ValidSeconds);
        Assert.AreEqual(75.0, report.Storage.IdlePercent.Maximum);
        Assert.AreEqual(1200.0, report.Storage.PhysicalDiskWriteBytes);
        Assert.IsTrue(report.Limitations.Any(x => x.StartsWith("disk.idle:") && x.Contains("60.000")));
        var weekly = query.GetWeeklyReport(new(Start, Start.AddMinutes(2)), Path.Combine(dir.DirectoryPath, "marks"), usageDirectory: Path.Combine(dir.DirectoryPath, "usage"));
        Assert.AreEqual(50.0, weekly.Quality.Metrics.Single(x => x.Metric == "disk.idle").PercentOfObservedTime);
        using var source = AgentWatchDatabase.OpenReadOnly(dir.DatabasePath);
        var row = HistoryReader.Read(source, Start, Start.AddMinutes(1)).Single();
        Assert.AreEqual(160.7, AggregateCodec.Decode(row.BucketUnixSeconds, row.ResolutionSeconds, row.Payload)[Metric.DiskIdle].Maximum);
    }

    [TestMethod] public void SnapshotIncludesCommittedWalAndRemainsStableAfterFurtherWrites()
    {
        using var dir = new TestDirectory(); using var writer = new AgentWatchDatabase(dir.DatabasePath);
        var batch = new PersistBatch(); batch.Frames.Add(Frame(Start)); writer.Persist(batch);
        var snapshot = Path.Combine(dir.DirectoryPath, "snapshot.db");
        AnalysisBundle.Snapshot(dir.DatabasePath, snapshot);
        var later = new PersistBatch(); later.Frames.Add(Frame(Start.AddMinutes(1))); writer.Persist(later);
        var period = new QueryPeriod(Start, Start.AddMinutes(2));
        Assert.AreEqual(60.0, new ReportService(snapshot).GetReport(period).Period.ObservedSeconds);
        Assert.AreEqual(120.0, new ReportService(dir.DatabasePath).GetReport(period).Period.ObservedSeconds);
        Assert.ThrowsExactly<IOException>(() => AnalysisBundle.Snapshot(dir.DatabasePath, snapshot));
    }

    [TestMethod] public void LocalDaysSplitAtRiyadhMidnightAndRespectDst()
    {
        var periods = AnalysisBundle.LocalPeriods(new(Start, Start.AddMinutes(2)), TimeZoneInfo.FindSystemTimeZoneById("Arab Standard Time"));
        Assert.AreEqual(2, periods.Count); Assert.AreEqual("2026-09-07", periods[0].Date);
        Assert.AreEqual("2026-09-08", periods[1].Date); Assert.IsTrue(periods.All(x => x.Partial));
        Assert.AreEqual(Start.AddMinutes(1), periods[0].Period.ToUtc);
        Assert.AreEqual(periods[0].Period.ToUtc, periods[1].Period.FromUtc);
        var spring = AnalysisBundle.LocalPeriods(new(DateTimeOffset.Parse("2026-03-08T05:00:00Z"), DateTimeOffset.Parse("2026-03-09T04:00:00Z")), TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
        Assert.AreEqual(1, spring.Count); Assert.IsFalse(spring[0].Partial);
        Assert.AreEqual(23.0, (spring[0].Period.ToUtc - spring[0].Period.FromUtc).TotalHours);
        var fall = AnalysisBundle.LocalPeriods(new(DateTimeOffset.Parse("2026-11-01T04:00:00Z"), DateTimeOffset.Parse("2026-11-02T05:00:00Z")), TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
        Assert.AreEqual(25.0, (fall.Single().Period.ToUtc - fall.Single().Period.FromUtc).TotalHours);
    }

    [TestMethod] public void BundleProducesConsistentDaysHashesAndRefusesOverwrite()
    {
        using var dir = new TestDirectory(); using var writer = new AgentWatchDatabase(dir.DatabasePath);
        var batch = new PersistBatch(); batch.Frames.Add(Frame(Start)); batch.Frames.Add(Frame(Start.AddMinutes(1), 150)); writer.Persist(batch);
        var output = Path.Combine(dir.DirectoryPath, "analysis"); var zone = TimeZoneInfo.FindSystemTimeZoneById("Arab Standard Time");
        var period = new QueryPeriod(Start, Start.AddMinutes(2));
        var manifest = AnalysisBundle.Create(dir.DirectoryPath, output, period, zone, new(),
            Path.Combine(dir.DirectoryPath, "marks"), Path.Combine(dir.DirectoryPath, "usage"));
        Assert.AreEqual("complete", manifest.State);
        foreach (var file in manifest.Files)
        {
            using var stream = File.OpenRead(Path.Combine(output, file.Name));
            Assert.AreEqual(file.Sha256, Convert.ToHexString(SHA256.HashData(stream)));
        }
        var days = JsonSerializer.Deserialize<LocalDayReport[]>(File.ReadAllText(Path.Combine(output, "days.json")), JsonDefaults.Options)!;
        var report = JsonSerializer.Deserialize<WatchReport>(File.ReadAllText(Path.Combine(output, "report.json")), JsonDefaults.Options)!;
        Assert.AreEqual(report.Period.ObservedSeconds, days.Sum(d => d.Measurements.Period.ObservedSeconds));
        Assert.AreEqual(report.Storage.PhysicalDiskWriteBytes, days.Sum(d => d.Measurements.Storage.PhysicalDiskWriteBytes));
        Assert.AreEqual(2, days.Length); Assert.AreEqual(0.0, days[1].Measurements.Storage.IdlePercent.ValidSeconds);
        Assert.IsTrue(File.ReadAllText(Path.Combine(output, "summary.md")).Contains("2026-09-08"));
        Assert.ThrowsExactly<IOException>(() => AnalysisBundle.Create(dir.DirectoryPath, output, period, zone, new()));
    }

    [TestMethod] public void InvalidRequestFailsBeforeCreatingOutputAndFailedSnapshotHasNoCompleteManifest()
    {
        using var dir = new TestDirectory(); var output = Path.Combine(dir.DirectoryPath, "bad");
        Assert.ThrowsExactly<ArgumentException>(() => AnalysisBundle.Create(dir.DirectoryPath, output, new(Start, Start.AddDays(32)), TimeZoneInfo.Utc, new()));
        Assert.IsFalse(Directory.Exists(output));
        Assert.Throws<Exception>(() => AnalysisBundle.Create(dir.DirectoryPath, output, new(Start, Start.AddHours(1)), TimeZoneInfo.Utc, new()));
        Assert.IsTrue(File.Exists(Path.Combine(output, "failed.json")));
        Assert.IsFalse(File.Exists(Path.Combine(output, "manifest.json")));
    }
}
