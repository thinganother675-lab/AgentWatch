using System.Diagnostics;
using System.Text.Json;
using AgentWatch.Aggregation;
using AgentWatch.Configuration;
using AgentWatch.Collectors;
using AgentWatch.Models;
using AgentWatch.Storage;
using AgentWatch.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.IntegrationTests;

[TestClass]
public sealed class HistoryVolumeTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SyntheticWeekRetainsTotalsAndReportsStorageVolume(bool withoutRowid)
    {
        using var directory = new TestDirectory(); var watch = Stopwatch.StartNew();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z"); var rng = new Random(7321);
        long minuteDatabaseBytes, maximumWal = 0; var rowCount = 0;
        var selfNode = new ProcessNode((uint)Environment.ProcessId, 0, "storage-fixture");
        var beforeIo = ProcessCollector.Read(selfNode)!.Io!.Value;
        ulong minuteLogicalWrites;
        using (var writer = new AgentWatchDatabase(directory.DatabasePath))
        {
            if (!withoutRowid)
            {
                using var layout = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False"); layout.Open();
                using var alter = layout.CreateCommand();
                alter.CommandText = "DROP TABLE history; CREATE TABLE history (resolution_s INTEGER NOT NULL CHECK(resolution_s IN (60,900,86400)),bucket_utc INTEGER NOT NULL,kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),item TEXT NOT NULL,payload BLOB NOT NULL,PRIMARY KEY(resolution_s,bucket_utc,kind,item));";
                alter.ExecuteNonQuery();
            }
            var aggregator = new SampleAggregator(); var batch = new PersistBatch();
            for (var minute = 0; minute < 7 * 1440; minute++)
            {
                for (var sample = 1; sample <= 6; sample++)
                {
                    var stamp = start.AddMinutes(minute).AddSeconds(sample * 10); var cpu = rng.NextDouble() * 90;
                    IoCounters io = new() { LogicalReadBytes = (ulong)rng.Next(65536), LogicalWriteBytes = (ulong)rng.Next(200000), ReadOperations = 12, WriteOperations = 18, OtherOperations = 4, LogicalOtherBytes = 4096 };
                    aggregator.Add(new(stamp, 10, new(cpu, cpu * .8, cpu * .2), new(8UL << 30, (ulong)rng.Next(100, 2000) << 20, 80, 10UL << 30, 24UL << 30, 4096),
                        new(20, 2, 5, 1, 100000, 200000, .2, 90),
                        [new(AgentKind.Codex, 24, cpu / 2, 2UL << 30, 3UL << 30, io, 0, 0, 0), new(AgentKind.Claude, 0, 0, 0, 0, default(IoCounters), 0, 0, 0)],
                        new(.04, 55UL << 20, 22UL << 20, default(IoCounters), 4 << 20, 1 << 20, 0, 0, 0), 8 + rng.NextDouble() * 8, false));
                }
                batch.Frames.AddRange(aggregator.Drain(start.AddMinutes(minute + 1)));
                foreach (var name in new[] { "logs_2.sqlite", "logs_2.sqlite-wal", "state_5.sqlite", "state_5.sqlite-wal" })
                    batch.HotFiles.Add(new(start.AddMinutes(minute), name, true, 1000000 + minute * 4096L, 4096, start.AddMinutes(minute)));
                if ((minute + 1) % 5 == 0)
                { writer.Persist(batch); batch = new(); maximumWal = Math.Max(maximumWal, new FileInfo(directory.DatabasePath + "-wal").Length); }
            }
            using (var reader = AgentWatchDatabase.OpenReadOnly(directory.DatabasePath)) rowCount = HistoryReader.Read(reader, start, start.AddDays(7)).Count();
            minuteDatabaseBytes = new FileInfo(directory.DatabasePath).Length;
            minuteLogicalWrites = ProcessCollector.Read(selfNode)!.Io!.Value.LogicalWriteBytes - beforeIo.LogicalWriteBytes;
            writer.ApplyRetention(start.AddDays(210), new());
            using var verify = AgentWatchDatabase.OpenReadOnly(directory.DatabasePath);
            var rows = HistoryReader.Read(verify, start, start.AddDays(7)).ToArray(); Assert.HasCount(35, rows);
            Assert.AreEqual(7 * 86400.0, rows.Where(r => r.Kind == 0).Sum(r => AggregateCodec.Decode(r.BucketUnixSeconds, r.ResolutionSeconds, r.Payload)[Metric.Coverage].Sum), .001);
            Assert.IsTrue(rows.All(r => r.ResolutionSeconds == 86400)); Assert.AreEqual(50400, rowCount);
        }
        Assert.IsTrue(minuteDatabaseBytes < 64L * 1024 * 1024, "A synthetic week should remain well below the own DB size warning.");
        var artifact = Path.GetFullPath(Path.Combine(directory.DirectoryPath, "..", "..", withoutRowid ? "history-volume-without-rowid.json" : "history-volume-rowid.json"));
        File.WriteAllText(artifact, JsonSerializer.Serialize(new { WithoutRowid = withoutRowid, SyntheticDays = 7, FastSamples = 60480, MinuteRowsIncludingHotFiles = rowCount, MinuteDatabaseBytes = minuteDatabaseBytes,
            MaximumWalBytes = maximumWal, SyntheticWeekLogicalWriteBytesIncludingPeriodicCheckpoints = minuteLogicalWrites,
            SyntheticLogicalWriteBytesPerDay = minuteLogicalWrites / 7.0, PhysicalBytesAfterRetention = new FileInfo(directory.DatabasePath).Length, DailyRowsAfterRetention = 35,
            ElapsedSeconds = watch.Elapsed.TotalSeconds, Caveat = "Synthetic accelerated storage/retention fixture; not an overhead or daily writes benchmark. No VACUUM; pages are retained for reuse." }, JsonDefaults.Options));
    }
}
