using AgentWatch.Aggregation;
using AgentWatch.Hosting;
using AgentWatch.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.IntegrationTests;

[TestClass]
public sealed class RetentionFailureTests
{
    [TestMethod] public void SecondTierFailureRollsBackFirstTierWritesAndDeletes()
    {
        using var directory = new TestDirectory(); using var writer = new AgentWatchDatabase(directory.DatabasePath);
        var now = DateTimeOffset.UtcNow; var batch = new PersistBatch();
        var frame = new AggregateFrame(now.AddDays(-200).ToUnixTimeSeconds()); frame.Add(Metric.Coverage, 1, 60); batch.Frames.Add(frame); writer.Persist(batch);
        using (var fixture = new SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False"))
        {
            fixture.Open(); using var inject = fixture.CreateCommand();
            inject.CommandText = "CREATE TRIGGER fail_daily BEFORE INSERT ON history WHEN NEW.resolution_s=86400 BEGIN SELECT RAISE(ABORT,'controlled retention fault'); END"; inject.ExecuteNonQuery();
        }
        Assert.ThrowsExactly<SqliteException>(() => writer.ApplyRetention(now, new()));
        using var read = AgentWatchDatabase.OpenReadOnly(directory.DatabasePath);
        var rows = HistoryReader.Read(read, now.AddDays(-201), now).ToList(); Assert.HasCount(1, rows); Assert.AreEqual(60, rows[0].ResolutionSeconds);
        Assert.AreEqual(60.0, AggregateCodec.Decode(rows[0].BucketUnixSeconds, 60, rows[0].Payload)[Metric.Coverage].Sum);
    }
    [TestMethod] public void InstallerChecksOrdinaryFileMetadataAndQuotesSpaces()
    {
        using var directory = new TestDirectory(); var path = Path.Combine(directory.DirectoryPath, "fixture.txt"); File.WriteAllText(path, "own fixture");
        ServiceInstaller.EnsureSingleLink(path); ServiceInstaller.EnsureNoReparse(path);
        Assert.AreEqual("\"C:\\Program Files\\AgentWatch\\AgentWatch.exe\" service --data-dir \"C:\\ProgramData\\AgentWatch\"",
            ServiceInstaller.BinaryCommand(@"C:\Program Files\AgentWatch\AgentWatch.exe", @"C:\ProgramData\AgentWatch"));
        Assert.ThrowsExactly<ArgumentException>(() => ServiceInstaller.BinaryCommand("relative.exe", directory.DirectoryPath));
    }
}
