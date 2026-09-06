using System.Buffers.Binary;
using AgentWatch.Analysis;
using AgentWatch.Collectors;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.Tests;

[TestClass]
public sealed class NvmeAnomalyTests
{
    [TestMethod] public void HealthChangesProduceDistinctEventsWithAppropriateSeverity()
    {
        var bytes = new byte[512]; bytes[3] = 100; bytes[4] = 10;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), 322);
        var before = NvmeParser.Parse(bytes, 0, DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var detector = new AnomalyDetector(new()); detector.EvaluateNvme(before); Assert.HasCount(0, detector.Drain());
        var after = before with { TimestampUtc = before.TimestampUtc.AddMinutes(15), TemperatureC = 71, CriticalWarning = 1,
            PercentageUsed = 1, AvailableSparePercent = 99, UnsafeShutdowns = "1", MediaErrors = "1", ErrorInfoLogEntries = "2" };
        detector.EvaluateNvme(after); var events = detector.Drain().ToDictionary(e => e.Type);
        Assert.HasCount(7, events); Assert.AreEqual("critical", events["ssd-temperature"].Severity);
        Assert.AreEqual("critical", events["nvme-media-errors"].Severity); Assert.AreEqual("info", events["nvme-unsafe-shutdowns"].Severity);
        Assert.IsTrue(events.ContainsKey("nvme-critical-warning")); Assert.IsTrue(events.ContainsKey("nvme-spare-decrease"));
        Assert.IsTrue(events.ContainsKey("nvme-percentage-used")); Assert.IsTrue(events.ContainsKey("nvme-error-log"));
    }
    [TestMethod] public void MissingTemperatureAndIndependentSensorOffsetsAreHandled()
    {
        var bytes = new byte[512]; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(200), 300);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(202), ushort.MaxValue);
        var point = NvmeParser.Parse(bytes, 0, DateTimeOffset.UtcNow);
        Assert.IsNull(point.TemperatureC); Assert.AreEqual(26.85, point.TemperatureSensorsC[0]!.Value, .00001);
        Assert.IsNull(point.TemperatureSensorsC[1]); Assert.IsNull(point.TemperatureSensorsC[7]);
        var detector = new AnomalyDetector(new()); detector.EvaluateNvme(point); Assert.HasCount(0, detector.Drain());
    }
}
