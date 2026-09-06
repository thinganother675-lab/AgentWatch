using AgentWatch.Aggregation;
using AgentWatch.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.Tests;

[TestClass]
public sealed class AggregationTests
{
    public static FastSample Sample(DateTimeOffset end, double seconds = 10, double cpu = 50, ulong available = 200 * 1024 * 1024)
        => new(end, seconds, new(cpu, cpu, 0), new(8UL << 30, available, 90, 10UL << 30, 20UL << 30, 4096),
            new(2, 3, 1, 1, 100, 200, 0.1, 90), [], null, 5, false);
    [TestMethod] public void SixTenSecondSamplesMakeOneMinute()
    {
        var aggregator = new SampleAggregator(); var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        for (var i = 1; i <= 6; i++) aggregator.Add(Sample(start.AddSeconds(i * 10)));
        var frames = aggregator.Drain(start.AddMinutes(1)); Assert.HasCount(1, frames);
        var frame = frames[0]; Assert.AreEqual(60, frame[Metric.Coverage].Sum, 0.001);
        Assert.AreEqual(50, frame[Metric.CpuTotal].Average!.Value, 0.001);
        Assert.AreEqual(6, frame[Metric.SampleCount].Sum, 0.001);
        Assert.AreEqual(60 * 3 * 4096, frame[Metric.EstimatedPageOut].Sum, 0.001);
        Assert.AreEqual(60, frame[Metric.MemoryBelow256].Sum, 0.001);
    }
    [TestMethod] public void IntervalCrossingUtcMinuteIsSplitWithoutDoubleCounting()
    {
        var aggregator = new SampleAggregator(); var end = DateTimeOffset.Parse("2026-09-01T00:01:05Z");
        aggregator.Add(Sample(end)); var frames = aggregator.Drain(end, true); Assert.HasCount(2, frames);
        Assert.AreEqual(5, frames[0][Metric.Coverage].Sum, 0.001); Assert.AreEqual(5, frames[1][Metric.Coverage].Sum, 0.001);
        Assert.AreEqual(1, frames.Sum(f => f[Metric.SampleCount].Sum), 0.001);
    }
    [TestMethod] public void WeightedRollupPreservesMinMaxAndMissingness()
    {
        var a = new AggregateFrame(0); a.Add(Metric.CpuTotal, 10, 10);
        var b = new AggregateFrame(60); b.Add(Metric.CpuTotal, 90, 30); a.Merge(b);
        Assert.AreEqual(70, a[Metric.CpuTotal].Average!.Value, 0.001);
        Assert.AreEqual(10, a[Metric.CpuTotal].Minimum, 0.001); Assert.AreEqual(90, a[Metric.CpuTotal].Maximum, 0.001);
        Assert.IsNull(a[Metric.PagesOutput].Average);
    }
    [TestMethod] public void GapAddsNoFakeCoverageOrCpu()
    {
        var aggregator = new SampleAggregator(); aggregator.Add(Sample(DateTimeOffset.UtcNow, 3600) with { Gap = true });
        Assert.HasCount(0, aggregator.Drain(DateTimeOffset.UtcNow, true));
    }
    [TestMethod] public void BinaryCodecPreservesRollup()
    {
        var frame = new AggregateFrame(0); frame.Add(Metric.CpuTotal, 20, 10); frame.CpuHistogram[4] = 10;
        var decoded = AggregateCodec.Decode(0, 60, AggregateCodec.Encode(frame));
        Assert.AreEqual(200, decoded[Metric.CpuTotal].Sum, 0.001); Assert.AreEqual(10, decoded.CpuHistogram[4], 0.001);
        Assert.IsNull(decoded[Metric.PagesOutput].Average);
    }
}
