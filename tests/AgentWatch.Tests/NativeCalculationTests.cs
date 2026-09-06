using System.Buffers.Binary;
using System.Numerics;
using AgentWatch.Attribution;
using AgentWatch.Collectors;
using AgentWatch.Models;
using AgentWatch.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.Tests;

[TestClass]
public sealed class NativeCalculationTests
{
    private static ProcessReading Reading(uint pid = 1, ulong created = 100, uint parent = 0, string name = "codex.exe", ulong cpu = 100,
        ulong writes = 1000) => new(new(pid, created), parent, name, cpu, new IoCounters { LogicalWriteBytes = writes }, 100, 200);

    [TestMethod] public void CpuSubtractsIdleFromKernel()
    {
        var cpu = SystemCollector.Calculate(new(100, 150, 50), new(140, 250, 110));
        Assert.IsNotNull(cpu); Assert.AreEqual(75, cpu.TotalPercent, 0.001);
        Assert.AreEqual(37.5, cpu.UserPercent, 0.001); Assert.AreEqual(37.5, cpu.KernelPercent, 0.001);
    }
    [TestMethod] public void CpuRejectsCounterRegression() => Assert.IsNull(SystemCollector.Calculate(new(100, 200, 100), new(90, 201, 200)));
    [TestMethod] public void CpuRejectsZeroInterval() => Assert.IsNull(SystemCollector.Calculate(new(100, 200, 100), new(100, 200, 100)));
    [TestMethod] public void CpuRejectsImpossibleIdle() => Assert.IsNull(SystemCollector.Calculate(new(0, 0, 0), new(100, 50, 1)));
    [TestMethod] public void ProcessDeltaUsesLogicalIoAndMachineNormalizedCpu()
    {
        var d = ProcessTracker.CalculateDelta(Reading(cpu: 0), Reading(cpu: 10_000_000, writes: 5000), 10, 8);
        Assert.IsTrue(d.HasBaseline); Assert.AreEqual(1.25, d.CpuPercent, 0.001); Assert.AreEqual(4000UL, d.Io!.Value.LogicalWriteBytes);
    }
    [TestMethod] public void NewProcessIsOnlyBaseline() => Assert.IsFalse(ProcessTracker.CalculateDelta(null, Reading(), 10, 8).HasBaseline);
    [TestMethod] public void PidReuseIsNotSameProcess() => Assert.IsFalse(ProcessTracker.CalculateDelta(Reading(), Reading(created: 200), 10, 8).HasBaseline);
    [TestMethod] public void CpuResetInvalidatesBaseline() => Assert.IsFalse(ProcessTracker.CalculateDelta(Reading(cpu: 200), Reading(cpu: 100), 10, 8).HasBaseline);
    [TestMethod] public void IoRegressionDoesNotUnderflow() => Assert.IsNull(ProcessTracker.CalculateDelta(Reading(writes: 5000), Reading(writes: 2), 10, 8).Io);
    [TestMethod] public void DisappearanceCountsExitAndDropsBaseline()
    {
        var tracker = new ProcessTracker(); tracker.Update([Reading()], 10, 8);
        var gone = tracker.Update([], 10, 8); Assert.AreEqual(1, gone[0].ObservedExits); Assert.AreEqual(0, gone[0].ProcessCount);
        var again = tracker.Update([Reading(writes: 9999)], 10, 8); Assert.IsNull(again[0].LogicalIo);
    }
    [TestMethod] public void DescendantsButNotUnrelatedNodeAreAttributed()
    {
        var readings = new[] { Reading(), Reading(2, 200, 1, "node.exe"), Reading(3, 300, 2, "test.exe"), Reading(4, 400, 99, "node.exe") };
        var result = new ProcessTracker().Classify(readings);
        Assert.AreEqual(AgentKind.Codex, result[readings[2].Identity]); Assert.AreEqual(AgentKind.Other, result[readings[3].Identity]);
    }
    [TestMethod] public void ReusedParentPidDoesNotStealOldChild()
    {
        var child = Reading(2, 100, 1, "node.exe"); var root = Reading(1, 200);
        Assert.AreEqual(AgentKind.Other, new ProcessTracker().Classify([root, child])[child.Identity]);
    }
    [TestMethod] public void VerifiedOrphanKeepsIdentityAttribution()
    {
        var tracker = new ProcessTracker(); var child = Reading(2, 200, 1, "node.exe");
        tracker.Update([Reading(), child], 10, 8);
        Assert.AreEqual(1, tracker.Update([child], 10, 8)[0].ProcessCount);
    }
    [TestMethod] public void CyclicParentsTerminateSafely()
    {
        var a = Reading(1, 100, 2, "node.exe"); var b = Reading(2, 100, 1, "node.exe");
        Assert.AreEqual(AgentKind.Other, new ProcessTracker().Classify([a, b])[a.Identity]);
    }
    [TestMethod] public void ServiceResetStartsNewBaselines()
    {
        var tracker = new ProcessTracker(); tracker.Update([Reading()], 10, 8);
        Assert.IsNull(tracker.Update([Reading(writes: 50000)], 10, 8, true)[0].LogicalIo);
    }
    [TestMethod] public void NvmeGoldenHostWritesAndHealth()
    {
        var log = new byte[512]; BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(48), 0xAAB764);
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), 322); log[3] = 100; log[4] = 10;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(144), 50);
        var result = NvmeParser.Parse(log, 0, DateTimeOffset.UnixEpoch);
        Assert.AreEqual("11188068", result.DataUnitsWritten); Assert.AreEqual("5728290816000", result.NvmeHostWriteBytesLifetime);
        Assert.AreEqual(48.85, result.TemperatureC!.Value, 0.001);
        Assert.AreEqual((byte)0, result.CriticalWarning); Assert.AreEqual((byte)100, result.AvailableSparePercent);
        Assert.AreEqual((byte)10, result.AvailableSpareThresholdPercent); Assert.AreEqual((byte)0, result.PercentageUsed);
        Assert.AreEqual("50", result.UnsafeShutdowns); Assert.AreEqual("0", result.MediaErrors);
    }
    [TestMethod] public void NvmeParsesAll128BitsAndMultipliesWithoutOverflow()
    {
        var bytes = Enumerable.Repeat((byte)255, 16).ToArray();
        Assert.AreEqual(UInt128.MaxValue, NvmeParser.ReadUInt128(bytes));
        Assert.AreEqual(BigInteger.Parse("340282366920938463463374607431768211455") * 512000, NvmeParser.DataUnitsToBytes(NvmeParser.ReadUInt128(bytes)));
    }
    [TestMethod] public void NvmeRejectsTruncatedLog() => Assert.ThrowsExactly<InvalidDataException>(() => NvmeParser.Parse(new byte[511], 0, DateTimeOffset.UtcNow));
    [TestMethod] public void NvmeTemperatureUnavailableIsNull()
    { Assert.IsNull(NvmeParser.KelvinToCelsius(0)); Assert.IsNull(NvmeParser.KelvinToCelsius(ushort.MaxValue)); }
    [TestMethod] public void NvmeRejectsOverflowingDescriptorOffset()
    {
        var bytes = new byte[560]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 48); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 3); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), uint.MaxValue); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 512);
        Assert.ThrowsExactly<InvalidDataException>(() => { _ = NvmeParser.ValidateDescriptor(bytes); });
    }
}
