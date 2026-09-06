using System.Diagnostics;
using AgentWatch.Analysis;
using AgentWatch.Collectors;
using AgentWatch.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.Tests;

[TestClass]
public sealed class AnomalyAndClockTests
{
    [TestMethod] public void RamWarningRequiresSustainedDuration()
    {
        var detector = new AnomalyDetector(new()); var start = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 11; i++) detector.Evaluate(AggregationTests.Sample(start.AddSeconds(i * 10)));
        Assert.HasCount(0, detector.Drain());
        detector.Evaluate(AggregationTests.Sample(start.AddSeconds(120)));
        var events = detector.Drain(); Assert.HasCount(1, events); Assert.AreEqual("ram-pressure", events[0].Type);
        Assert.AreEqual(120, events[0].Evidence["observedSeconds"], 0.001);
    }
    [TestMethod] public void BriefRecoveryBreaksSustainedCondition()
    {
        var detector = new AnomalyDetector(new()); var start = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 6; i++) detector.Evaluate(AggregationTests.Sample(start.AddSeconds(i * 10)));
        detector.Evaluate(AggregationTests.Sample(start.AddSeconds(70), available: 2UL << 30));
        for (var i = 8; i <= 13; i++) detector.Evaluate(AggregationTests.Sample(start.AddSeconds(i * 10)));
        Assert.HasCount(0, detector.Drain());
    }
    [TestMethod] public void GapClosesConditionAndDoesNotCountSleep()
    {
        var detector = new AnomalyDetector(new()); var start = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 12; i++) detector.Evaluate(AggregationTests.Sample(start.AddSeconds(i * 10)));
        var original = detector.Drain().Single();
        detector.Evaluate(AggregationTests.Sample(start.AddHours(1), 3480) with { Gap = true });
        var closed = detector.Drain().Single(); Assert.AreEqual(original.Id, closed.Id); Assert.IsFalse(closed.Open);
        Assert.AreEqual(original.EndUtc, closed.EndUtc);
    }
    [TestMethod] public void ClockDetectsSleepAndBackwardCorrection()
    {
        var clock = new SampleClock(10); var utc = DateTimeOffset.UtcNow; var ticks = Stopwatch.GetTimestamp();
        Assert.AreEqual(0, clock.Advance(utc, ticks).Seconds);
        Assert.IsFalse(clock.Advance(utc.AddSeconds(10), ticks + Stopwatch.Frequency * 10).Gap);
        Assert.IsTrue(clock.Advance(utc.AddHours(1), ticks + Stopwatch.Frequency * 3600).Gap);
        Assert.IsTrue(clock.Advance(utc.AddMinutes(59), ticks + Stopwatch.Frequency * 3610).Gap);
    }
    [TestMethod] public void ConfigRejectsRelativeProfileAndFallsBackOnUnsafeFrequency()
    {
        var config = new WatchConfig(); config.Sampling.FastSampleSeconds = 0;
        var warnings = new List<string>(); config.Validate(warnings); Assert.AreEqual(10, config.Sampling.FastSampleSeconds); Assert.HasCount(1, warnings);
        config.MonitoredUserProfile = "relative"; Assert.ThrowsExactly<InvalidDataException>(() => config.Validate([]));
    }
}
