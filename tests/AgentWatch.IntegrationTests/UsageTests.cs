using System.Text;
using System.Text.Json;
using AgentWatch.Analysis;
using AgentWatch.Analysis.Usage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.IntegrationTests;

[TestClass]
public sealed class UsageTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
    private const string Meta = """{"timestamp":"2026-09-01T12:00:00Z","type":"session_meta","payload":{"id":"session-one","base_instructions":"SECRET-MUST-NOT-PERSIST"}}""";
    private const string Context = """{"timestamp":"2026-09-01T12:00:01Z","type":"turn_context","payload":{"model":"test-model","effort":"high","turn_id":"turn-one"}}""";
    private const string Legacy = """{"timestamp":"2026-09-01T12:00:02Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":120},"last_token_usage":{"input_tokens":100,"output_tokens":20,"cached_input_tokens":40,"cache_write_input_tokens":0,"reasoning_output_tokens":10}}}}""";
    private const string Modern = """{"timestamp":"2026-09-01T12:00:02Z","type":"token_usage_record","payload":{"thread_id":"session-one","turn_id":"turn-one","response_id":"response-one","usage":{"input_tokens":100,"output_tokens":20,"cached_input_tokens":40,"cache_write_input_tokens":0,"reasoning_output_tokens":10}}}""";
    private const string Claude = """{"timestamp":"2026-09-01T12:00:02Z","type":"assistant","sessionId":"claude-one","effort":"medium","message":{"id":"message-one","model":"claude-test","content":[{"text":"SECRET-MUST-NOT-PERSIST"}],"usage":{"input_tokens":3,"output_tokens":55,"cache_read_input_tokens":80,"cache_creation_input_tokens":20,"output_tokens_details":{"thinking_tokens":8}}}}""";
    private static string Write(TestDirectory d, params string[] lines)
    { var file = Path.Combine(d.DirectoryPath, Guid.NewGuid().ToString("N") + ".jsonl"); File.WriteAllText(file, string.Join('\n', lines) + "\n", new UTF8Encoding(false)); return file; }
    private static UsageInventory Read(string directory) => UsageStore.Read(directory, new(Start.AddMinutes(-1), Start.AddHours(1)));

    [TestMethod] public void CodexResponseAndLegacyRecordsAreNotDoubleCountedAndImportIsIdempotent()
    {
        using var d = new TestDirectory(); var source = Write(d, Meta, Context, Legacy, Modern, Legacy, Modern); var store = Path.Combine(d.DirectoryPath, "usage");
        UsageImporter.Import(store, [new("codex", source)]); var first = Read(store);
        Assert.AreEqual(1, first.Usage.Count); Assert.AreEqual(100L, first.Sessions.Single().TokensInRequestedPeriod.Input);
        Assert.AreEqual("high", first.Usage.Single().ReasoningEffort); Assert.AreEqual("test-model", first.Usage.Single().Model);
        Assert.AreEqual(10L, first.Usage.Single().Tokens.ReasoningOutput); Assert.IsNull(first.Sessions.Single().EndedUtc);
        var again = UsageImporter.Import(store, [new("codex", source)]); Assert.AreEqual(0, again.FilesRead); Assert.AreEqual(1, Read(store).Usage.Count);
        var json = JsonSerializer.Serialize(first); Assert.IsFalse(json.Contains("SECRET-MUST-NOT-PERSIST", StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(store)) Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("SECRET-MUST-NOT-PERSIST", StringComparison.Ordinal));
    }
    [TestMethod] public void ClaudeDeduplicatesMessageIdsAndDoesNotCallPlaceholderOutputFinal()
    {
        using var d = new TestDirectory(); var source = Write(d, Claude, Claude); var store = Path.Combine(d.DirectoryPath, "usage");
        UsageImporter.Import(store, [new("claude", source)]); var r = Read(store); Assert.AreEqual(1, r.Usage.Count);
        var s = r.Sessions.Single(); Assert.AreEqual(3L, s.TokensInRequestedPeriod.Input); Assert.AreEqual(80L, s.TokensInRequestedPeriod.CacheRead);
        Assert.AreEqual(55L, s.TokensInRequestedPeriod.ReportedOutput); Assert.IsNull(s.TokensInRequestedPeriod.Output);
        Assert.IsNull(s.TokensInRequestedPeriod.ReasoningOutput); Assert.AreEqual(8L, s.TokensInRequestedPeriod.ReportedReasoningOutput); Assert.IsNull(s.StartedUtc); Assert.IsNull(s.CostEstimateUsd);
        Assert.AreEqual("medium", s.ReasoningEfforts.Single());
    }
    [TestMethod] public void IncompleteLastLineIsResumedAndTurnContextSurvivesIncrementalImport()
    {
        using var d = new TestDirectory(); var source = Write(d, Meta, Context); var store = Path.Combine(d.DirectoryPath, "usage");
        var half = Modern.Length / 2; File.AppendAllText(source, Modern[..half]); UsageImporter.Import(store, [new("codex", source)]);
        Assert.AreEqual(0, Read(store).Usage.Count); File.AppendAllText(source, Modern[half..] + "\n");
        UsageImporter.Import(store, [new("codex", source)]); var entry = Read(store).Usage.Single(); Assert.AreEqual("test-model", entry.Model); Assert.AreEqual("high", entry.ReasoningEffort);
    }
    [TestMethod] public void MissingModelAndCountersStayUnknown()
    {
        using var d = new TestDirectory(); var source = Write(d, Meta, """{"timestamp":"2026-09-01T12:00:02Z","type":"token_usage_record","payload":{"thread_id":"session-one","response_id":"r","usage":{"input_tokens":10}}}""");
        var store = Path.Combine(d.DirectoryPath, "usage"); UsageImporter.Import(store, [new("codex", source)]);
        var entry = Read(store).Usage.Single(); Assert.IsNull(entry.Model); Assert.IsNull(entry.ReasoningEffort); Assert.IsNull(entry.Tokens.Output); Assert.IsNull(entry.Tokens.CacheRead);
    }
    [TestMethod] public void TruncationRetainsPreviouslyObservedAccountingAndWarns()
    {
        using var d = new TestDirectory(); var source = Write(d, Meta, Context, Modern); var store = Path.Combine(d.DirectoryPath, "usage");
        UsageImporter.Import(store, [new("codex", source)]); File.WriteAllText(source, Meta + "\n");
        var result = UsageImporter.Import(store, [new("codex", source)]); Assert.IsTrue(result.Warnings.Any(w => w.Contains("truncated/replaced")));
        Assert.AreEqual(1, Read(store).Usage.Count);
    }
    [TestMethod] public void LargeAndMalformedLinesDoNotPersistContentOrLoseLaterMetadata()
    {
        using var d = new TestDirectory(); var source = Write(d, "{broken", new string('x', 1024 * 1024 + 10), Meta, Context, Modern); var store = Path.Combine(d.DirectoryPath, "usage");
        var result = UsageImporter.Import(store, [new("codex", source)]); Assert.AreEqual(1, result.InvalidRecords); Assert.AreEqual(1, result.OversizedLines);
        Assert.AreEqual(1, Read(store).Usage.Count);
    }
    [TestMethod] public void ExplicitPriceUsesCacheCorrectlyAndDoesNotDoubleChargeReasoning()
    {
        var tokens = new TokenAmounts(100, 20, 40, 0, 10, "includes-cache");
        var entry = new UsageEntry("r", "codex", "s", null, Start, "m", "high", tokens, "test", "file", 1);
        var rule = new PriceRule("codex", "m", Start.AddDays(-1), Start.AddDays(1), 2, 1, 2, 10, "explicit test tariff");
        Assert.AreEqual(0.00036m, UsagePricing.Estimate(entry, [rule])); Assert.IsNull(UsagePricing.Estimate(entry, []));
        Assert.IsNull(UsagePricing.Estimate(entry with { Model = "unknown" }, [rule]));
        Assert.IsNull(UsagePricing.Estimate(entry with { Tokens = tokens with { Output = null } }, [rule]));
        Assert.IsNull(UsagePricing.Estimate(entry, [rule, rule]));
    }
    [TestMethod] public void EndRequiresExplicitSessionEndNotTurnCompletionOrIdle()
    {
        using var d = new TestDirectory(); var source = Write(d, Meta, Context, Modern,
            """{"timestamp":"2026-09-01T12:01:00Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"turn-one"}}""");
        var store = Path.Combine(d.DirectoryPath, "usage"); UsageImporter.Import(store, [new("codex", source)]); Assert.IsNull(Read(store).Sessions.Single().EndedUtc);
        File.AppendAllText(source, """{"timestamp":"2026-09-01T12:02:00Z","type":"event_msg","payload":{"type":"session_end"}}""" + "\n");
        UsageImporter.Import(store, [new("codex", source)]); Assert.AreEqual(Start.AddMinutes(2), Read(store).Sessions.Single().EndedUtc);
    }
    [TestMethod] public void ResourceContextIsTemporalAndKeepsSessionEventIdsStable()
    {
        using var d = new TestDirectory(); var source = Write(d, Meta, Context, Modern); var store = Path.Combine(d.DirectoryPath, "usage");
        UsageImporter.Import(store, [new("codex", source)]);
        using var writer = new AgentWatch.Storage.AgentWatchDatabase(d.DatabasePath); var frame = new AgentWatch.Aggregation.AggregateFrame(Start.ToUnixTimeSeconds());
        frame.Add(AgentWatch.Aggregation.Metric.Coverage, 1, 60); frame.Add(AgentWatch.Aggregation.Metric.CodexLogicalWrite, 1024, 60);
        var batch = new AgentWatch.Storage.PersistBatch(); batch.Frames.Add(frame); writer.Persist(batch);
        var report = new ReportService(d.DatabasePath).GetWeeklyReport(new(Start, Start.AddHours(1)), Path.Combine(d.DirectoryPath, "marks"), usageDirectory: store);
        var context = report.Contexts.Single(c => c.SubjectType == "session"); Assert.AreEqual(61440, context.Agents["codex"].LogicalWriteBytes);
        StringAssert.Contains(context.Attribution, "not resource attribution"); Assert.IsTrue(report.Events.Any(e => e.Type == "session.start"));
        Assert.IsTrue(report.Events.Any(e => e.Type == "session.usage"));
    }
}
