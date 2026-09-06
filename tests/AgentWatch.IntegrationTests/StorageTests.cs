using AgentWatch.Aggregation;
using AgentWatch.Configuration;
using AgentWatch.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.IntegrationTests;

public sealed class TestDirectory : IDisposable
{
    public string DirectoryPath { get; }
    public string DatabasePath => Path.Combine(DirectoryPath, "agentwatch.db");
    public TestDirectory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentWatch.sln"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Test workspace root not found.");
        DirectoryPath = Path.Combine(root.FullName, "artifacts", "tests", "data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }
    public void Dispose() => Directory.Delete(DirectoryPath, true);
}

[TestClass]
public sealed class StorageTests
{
    private static AggregateFrame Frame(DateTimeOffset time, double cpu = 25)
    {
        var frame = new AggregateFrame(time.ToUnixTimeSeconds()); frame.Add(Metric.Coverage, 1, 60); frame.Add(Metric.CpuTotal, cpu, 60);
        frame.Add(Metric.CodexLogicalWrite, 1024, 60); return frame;
    }
    [TestMethod] public void BatchIsAtomicIdempotentAndReadableWhileWriterIsOpen()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        var now = DateTimeOffset.UtcNow.AddMinutes(-5); var batch = new PersistBatch();
        for (var i = 0; i < 5; i++) batch.Frames.Add(Frame(now.AddMinutes(i)));
        writer.Persist(batch); writer.Persist(batch);
        using var reader = AgentWatchDatabase.OpenReadOnly(directory.DatabasePath);
        var rows = HistoryReader.Read(reader, now.AddMinutes(-1), now.AddMinutes(10)).ToList(); Assert.HasCount(5, rows);
        Assert.AreEqual(300, rows.Sum(row => AggregateCodec.Decode(row.BucketUnixSeconds, row.ResolutionSeconds, row.Payload)[Metric.Coverage].Sum), 0.001);
        using var command = reader.CreateCommand(); command.CommandText = "DELETE FROM history";
        Assert.ThrowsExactly<SqliteException>(() => command.ExecuteNonQuery());
    }
    [TestMethod] public void GracefullyClosedWalDatabaseRemainsReadable()
    {
        using var directory = new TestDirectory();
        using (var writer = new AgentWatchDatabase(directory.DatabasePath))
        { var batch = new PersistBatch(); batch.Frames.Add(Frame(DateTimeOffset.UtcNow.AddMinutes(-1))); writer.Persist(batch); }
        Assert.IsTrue(File.Exists(directory.DatabasePath + "-wal")); Assert.IsTrue(File.Exists(directory.DatabasePath + "-shm"));
        using var reader = AgentWatchDatabase.OpenReadOnly(directory.DatabasePath);
        Assert.HasCount(1, HistoryReader.Read(reader, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow).ToList());
    }
    [TestMethod] public void RetentionRollsMinuteToQuarterHourThenDailyWithoutLosingTotals()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        var now = DateTimeOffset.Parse("2026-09-06T00:00:00Z"); var batch = new PersistBatch();
        for (var i = 0; i < 60; i++) batch.Frames.Add(Frame(now.AddDays(-200).AddMinutes(i)));
        for (var i = 0; i < 30; i++) batch.Frames.Add(Frame(now.AddDays(-20).AddMinutes(i)));
        batch.Frames.Add(Frame(now.AddMinutes(-1))); writer.Persist(batch);
        writer.ApplyRetention(now, new()); writer.ApplyRetention(now, new());
        using var reader = AgentWatchDatabase.OpenReadOnly(directory.DatabasePath);
        var rows = HistoryReader.Read(reader, now.AddDays(-365), now).ToList();
        Assert.AreEqual(1, rows.Count(row => row.ResolutionSeconds == 86400));
        Assert.AreEqual(2, rows.Count(row => row.ResolutionSeconds == 900)); Assert.AreEqual(1, rows.Count(row => row.ResolutionSeconds == 60));
        Assert.AreEqual(91 * 60, rows.Sum(row => AggregateCodec.Decode(row.BucketUnixSeconds, row.ResolutionSeconds, row.Payload)[Metric.Coverage].Sum), 0.001);
    }
    [TestMethod] public void CancelledRetentionKeepsFineRows()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        var now = DateTimeOffset.UtcNow; var batch = new PersistBatch(); batch.Frames.Add(Frame(now.AddDays(-200))); writer.Persist(batch);
        Assert.ThrowsExactly<OperationCanceledException>(() => writer.ApplyRetention(now, new(), new CancellationToken(true)));
        using var reader = AgentWatchDatabase.OpenReadOnly(directory.DatabasePath);
        Assert.AreEqual(60, HistoryReader.Read(reader, now.AddDays(-300), now).Single().ResolutionSeconds);
    }
    [TestMethod] public void SecondWriterIsRejected()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        Assert.ThrowsExactly<IOException>(() => { using var second = new AgentWatchDatabase(directory.DatabasePath); });
    }
    [TestMethod] public void FutureSchemaIsRejectedWithoutMutation()
    {
        using var directory = new TestDirectory();
        using (var writer = new AgentWatchDatabase(directory.DatabasePath)) { }
        using (var edit = new SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False"))
        { edit.Open(); using var command = edit.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery(); }
        Assert.ThrowsExactly<InvalidDataException>(() => { using var writer = new AgentWatchDatabase(directory.DatabasePath); });
        using var verify = new SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False"); verify.Open();
        using var check = verify.CreateCommand(); check.CommandText = "PRAGMA user_version"; Assert.AreEqual(999L, check.ExecuteScalar());
    }
}
