using System.Text;
using AgentWatch.Analysis;
using AgentWatch.Analysis.Usage;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.IntegrationTests;

[TestClass]
public sealed class UsageQualityTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
    private const string Meta = """{"timestamp":"2026-09-01T12:00:00Z","type":"session_meta","payload":{"id":"s"}}""";
    private const string Context = """{"timestamp":"2026-09-01T12:00:00Z","type":"turn_context","payload":{"turn_id":"a","model":"m","effort":"high"}}""";
    private const string Entry = """{"timestamp":"2026-09-01T12:00:01Z","type":"token_usage_record","payload":{"thread_id":"s","turn_id":"b","response_id":"r","usage":{"input_tokens":100,"output_tokens":10}}}""";
    [TestMethod] public void DifferentTurnCannotInheritTheCurrentTurnsModelOrEffort()
    {
        using var d = new TestDirectory(); var file = Path.Combine(d.DirectoryPath, "source.jsonl");
        File.WriteAllText(file, string.Join('\n', Meta, Context, Entry) + "\n", new UTF8Encoding(false));
        var store = Path.Combine(d.DirectoryPath, "usage"); UsageImporter.Import(store, [new("codex", file)]);
        var entry = UsageStore.Read(store, new(Start, Start.AddDays(1))).Usage.Single();
        Assert.AreEqual("b", entry.TurnId); Assert.IsNull(entry.Model); Assert.IsNull(entry.ReasoningEffort);
    }
    [TestMethod] public void SourceQualityWarningSurvivesALaterCleanOrNoOpImport()
    {
        using var d = new TestDirectory(); var file = Path.Combine(d.DirectoryPath, "source.jsonl");
        File.WriteAllText(file, "{broken\n" + Meta + "\n" + Entry + "\n", new UTF8Encoding(false)); var store = Path.Combine(d.DirectoryPath, "usage");
        UsageImporter.Import(store, [new("codex", file)]); var second = UsageImporter.Import(store, [new("codex", file)]);
        Assert.AreEqual(0, second.InvalidRecords); Assert.AreEqual(0, second.FilesRead);
        var report = UsageStore.Read(store, new(Start, Start.AddDays(1)));
        Assert.IsTrue(report.Notes.Any(n => n.Contains("некорректных записей 1", StringComparison.Ordinal)));
    }
    [TestMethod] public void FutureUsageSchemaIsRejectedWithoutChangingTheDatabase()
    {
        using var d = new TestDirectory(); var store = Path.Combine(d.DirectoryPath, "usage");
        using (var create = new UsageStore(store)) { }
        var file = Path.Combine(store, "usage.db");
        using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
        var before = File.ReadAllBytes(file);
        Assert.ThrowsExactly<InvalidDataException>(() => { using var next = new UsageStore(store); });
        CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
    }
}
