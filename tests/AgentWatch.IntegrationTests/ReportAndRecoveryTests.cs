using System.Buffers.Binary;
using System.Text.Json;
using AgentWatch.Aggregation;
using AgentWatch.Analysis;
using AgentWatch.Cli;
using AgentWatch.Collectors;
using AgentWatch.Configuration;
using AgentWatch.Models;
using AgentWatch.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.IntegrationTests;

[TestClass]
public sealed class ReportAndRecoveryTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static NvmeHealth Smart(DateTimeOffset time, ulong units, int drive = 0)
    {
        var bytes = new byte[512]; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), units);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), 322); return NvmeParser.Parse(bytes, drive, time);
    }
    [TestMethod] public void JsonContractPreservesNullsUtcAndExactNvmeIntegerStrings()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        var batch = new PersistBatch(); batch.Nvme.Add(Smart(Start, 0xAAB764)); batch.Nvme.Add(Smart(Start.AddMinutes(15), 0xAAB765)); writer.Persist(batch);
        var report = new ReportService(directory.DatabasePath).GetReport(new(Start, Start.AddHours(1)));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, JsonDefaults.Options)); var root = json.RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("system").GetProperty("cpu").GetProperty("totalPercent").GetProperty("average").ValueKind);
        var nvme = root.GetProperty("storage").GetProperty("nvme");
        Assert.AreEqual("5728290816000", nvme.GetProperty("first").GetProperty("nvmeHostWriteBytesLifetime").GetString());
        Assert.AreEqual("512000", nvme.GetProperty("observedNvmeHostWriteDeltaBytes").GetString());
        Assert.AreEqual(TimeSpan.Zero, root.GetProperty("period").GetProperty("fromUtc").GetDateTimeOffset().Offset);
        Assert.IsTrue(report.Limitations.Any(x => x.Contains("not NAND writes")));
        Assert.IsNull(report.Agents["codex"].LogicalProcessWriteBytes);
    }
    [TestMethod] public void FractionalUpperBoundaryIncludesTheOverlappingBucket()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        var batch = new PersistBatch(); var frame = new AggregateFrame(Start.ToUnixTimeSeconds()); frame.Add(Metric.Coverage, 1, 10); batch.Frames.Add(frame); writer.Persist(batch);
        var report = new ReportService(directory.DatabasePath).GetReport(new(Start, Start.AddMilliseconds(100)));
        Assert.AreEqual(10.0, report.Period.ObservedSeconds); Assert.AreEqual(Start.AddMinutes(1), report.Period.LastIncludedBucketEndUtc);
    }
    [TestMethod] public void PowerHourRegressionAcrossMidnightDoesNotEnterDailyBaseline()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        var batch = new PersistBatch(); batch.Nvme.Add(Smart(Start, 100) with { PowerOnHours = "400" });
        batch.Nvme.Add(Smart(Start.AddDays(1), 120) with { PowerOnHours = "10" }); writer.Persist(batch);
        var nvme = new ReportService(directory.DatabasePath).GetReport(new(Start, Start.AddDays(2))).Storage.Nvme!;
        Assert.AreEqual(1, nvme.CounterRegressions); Assert.IsNull(nvme.MeanCompleteDayBytes);
        Assert.IsFalse(nvme.DailyIntervals.Any(x => x.ApproximatelyFullDay));
    }
    [TestMethod] public void DiskChangesDoNotCombineDistinctPhysicalDrives()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        var batch = new PersistBatch(); batch.Nvme.Add(Smart(Start, 100, 0)); batch.Nvme.Add(Smart(Start.AddMinutes(15), 9000, 1)); writer.Persist(batch);
        var report = new ReportService(directory.DatabasePath, physicalDriveNumber: 1).GetReport(new(Start, Start.AddHours(1)));
        Assert.AreEqual(1, report.Storage.Nvme!.First.PhysicalDriveNumber); Assert.AreEqual(1, report.Storage.Nvme.Samples);
        Assert.IsNull(report.Storage.Nvme.ObservedNvmeHostWriteDeltaBytes);
    }
    [TestMethod] public void NvmeRollupsPreserveEndpointsAndExcludeCounterRegressions()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        var batch = new PersistBatch(); foreach (var (minutes, units) in new[] { (0, 100UL), (15, 110UL), (30, 1UL), (45, 3UL) }) batch.Nvme.Add(Smart(Start.AddMinutes(minutes), units));
        writer.Persist(batch); writer.ApplyRetention(Start.AddDays(200), new());
        var result = new ReportService(directory.DatabasePath).GetReport(new(Start, Start.AddDays(1))).Storage.Nvme!;
        Assert.AreEqual("6144000", result.ObservedNvmeHostWriteDeltaBytes); Assert.AreEqual(1, result.CounterRegressions);
        Assert.AreEqual(1800.0, result.CounterCoverageSeconds); Assert.AreEqual(Start, result.First.TimestampUtc);
        Assert.IsFalse(result.DailyIntervals.Any(x => x.ApproximatelyFullDay));
    }
    [TestMethod] public void GapPreservesMissedCountWithoutClaimingCoverage()
    {
        var aggregator = new SampleAggregator();
        aggregator.Add(new(Start, 600, null, null, null, [], new(null, null, null, null, 0, 0, 0, 2, 59), 1, true));
        var frame = aggregator.Drain(Start, true).Single(); Assert.AreEqual(0.0, frame[Metric.Coverage].Sum);
        Assert.AreEqual(59.0, frame[Metric.MissedSamples].Sum); Assert.AreEqual(2.0, frame[Metric.CollectorErrors].Sum);
        Assert.IsNull(frame[Metric.CpuTotal].Average);
    }
    [TestMethod] public void ReadSnapshotStaysStableAndWalBackpressureRecovers()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath, 128 * 1024);
        var initial = new PersistBatch(); initial.Metadata["probe"] = "old"; writer.Persist(initial);
        using var reader = AgentWatchDatabase.OpenReadOnly(directory.DatabasePath);
        using var transaction = reader.BeginTransaction(deferred: true);
        Assert.AreEqual("old", HistoryReader.Metadata(reader, transaction)["probe"]);
        var hitBudget = false; PersistBatch? pending = null;
        for (var i = 0; i < 50; i++)
        {
            pending = new(); pending.Metadata["probe"] = i + new string('a', 16384);
            try { writer.Persist(pending); } catch (IOException) { hitBudget = true; break; }
        }
        Assert.IsTrue(hitBudget, "An intentionally pinned reader must cause bounded backpressure.");
        Assert.IsTrue(new FileInfo(directory.DatabasePath + "-wal").Length < 256 * 1024);
        Assert.AreEqual("old", HistoryReader.Metadata(reader, transaction)["probe"]);
        transaction.Commit(); writer.Persist(pending!);
        Assert.AreEqual(pending!.Metadata["probe"], HistoryReader.Metadata(reader)["probe"]);
    }
    [TestMethod] public void CorruptDatabaseIsPreservedAndRejected()
    {
        using var directory = new TestDirectory(); var original = Enumerable.Repeat((byte)0x37, 4096).ToArray(); File.WriteAllBytes(directory.DatabasePath, original);
        Assert.ThrowsExactly<Microsoft.Data.Sqlite.SqliteException>(() => { using var writer = new AgentWatchDatabase(directory.DatabasePath); });
        CollectionAssert.AreEqual(original, File.ReadAllBytes(directory.DatabasePath));
    }
    [TestMethod] public void ConfigRelationshipsAndPeriodOffsetsAreValidated()
    {
        var config = new WatchConfig { Thresholds = new() { RamWarningMiB = 64, RamCriticalMiB = 900, SsdWarningC = 100, SsdCriticalC = 70 } };
        config.Validate([]); Assert.IsTrue(config.Thresholds.RamCriticalMiB <= config.Thresholds.RamWarningMiB);
        Assert.IsTrue(config.Thresholds.SsdCriticalC >= config.Thresholds.SsdWarningC);
        Assert.ThrowsExactly<ArgumentException>(() => new CommandOptions(["report", "--from", "2026-09-01T00:00:00"]).Period());
        var period = new CommandOptions(["report", "--from", "2026-09-01T03:00:00+03:00", "--to", "2026-09-02T00:00:00Z"]).Period();
        Assert.AreEqual(Start, period.FromUtc);
        Assert.IsTrue(Version.Parse(AgentWatchDatabase.NativeVersion) >= new Version(3, 51, 3));
    }
}
