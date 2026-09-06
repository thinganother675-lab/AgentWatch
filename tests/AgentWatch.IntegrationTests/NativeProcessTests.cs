using System.Diagnostics;
using AgentWatch.Attribution;
using AgentWatch.Collectors;
using AgentWatch.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgentWatch.IntegrationTests;

[TestClass]
public sealed class NativeProcessTests
{
    private static readonly string Helper = Path.Combine(AppContext.BaseDirectory, "helper", "AgentWatch.TestHelper.exe");
    private static Process Launch(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private static void Finish(Process process)
    {
        if (!process.HasExited) { process.StandardInput.WriteLine("exit"); if (!process.WaitForExit(10000)) process.Kill(true); }
        process.Dispose();
    }
    [TestMethod] public async Task LogicalIoSeesControlledSixteenMiBWriter()
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.DirectoryPath, "controlled-16MiB.bin");
        var process = Launch(Helper, "writer", file);
        try
        {
            Assert.AreEqual("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            var node = ProcessCollector.Snapshot().Single(n => n.Pid == (uint)process.Id);
            var before = ProcessCollector.Read(node)!; var clock = Stopwatch.StartNew();
            process.StandardInput.WriteLine("go");
            Assert.AreEqual("written", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            var after = ProcessCollector.Read(node)!;
            var delta = ProcessTracker.CalculateDelta(before, after, clock.Elapsed.TotalSeconds, Environment.ProcessorCount);
            Assert.AreEqual(16777216L, new FileInfo(file).Length);
            Assert.IsTrue(delta.HasBaseline); Assert.IsNotNull(delta.Io);
            Assert.IsTrue(delta.Io.Value.LogicalWriteBytes >= 16777216UL);
            Assert.IsTrue(delta.Io.Value.LogicalWriteBytes < 16777216UL + 65536UL, "Only tiny stdout/runtime writes may supplement the controlled file write.");
            Assert.IsTrue(delta.Io.Value.WriteOperations > 0);
            Assert.AreEqual(before.Identity, after.Identity);
        }
        finally { Finish(process); }
    }
    [TestMethod] public async Task NativeRootChildGrandchildAreAttributedAndUnrelatedHelperIsExcluded()
    {
        using var directory = new TestDirectory();
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(Helper)!)) File.Copy(file, Path.Combine(directory.DirectoryPath, Path.GetFileName(file)));
        var rootExe = Path.Combine(directory.DirectoryPath, "codex.exe"); File.Copy(Helper, rootExe);
        var root = Launch(rootExe, "tree", "2", Helper); var unrelated = Launch(Helper, "tree", "0", Helper);
        try
        {
            var line = await root.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var ids = line!.Split(',').Select(uint.Parse).ToArray(); Assert.HasCount(3, ids);
            Assert.IsNotNull(await unrelated.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            var nodes = ProcessCollector.Snapshot(); var reads = nodes.Where(n => ids.Contains(n.Pid) || n.Pid == (uint)unrelated.Id).Select(ProcessCollector.Read).OfType<ProcessReading>().ToList();
            Assert.HasCount(4, reads);
            Assert.AreEqual(ids[0], reads.Single(p => p.Identity.Pid == ids[1]).ParentPid);
            Assert.AreEqual(ids[1], reads.Single(p => p.Identity.Pid == ids[2]).ParentPid);
            var tracker = new ProcessTracker(); var groups = tracker.Update(reads, 1, Environment.ProcessorCount);
            Assert.AreEqual(3, groups.Single(g => g.Kind == AgentKind.Codex).ProcessCount);
            Assert.IsFalse(tracker.KnownIdentities.Any(i => i.Pid == (uint)unrelated.Id));
        }
        finally { Finish(root); Finish(unrelated); }
    }
    [TestMethod] public void ForegroundMonitorIsExcludedFromAgentGroups()
    {
        var collector = new ProcessCollector(); collector.Collect(1);
        Assert.IsFalse(collector.LastReadings.Any(p => p.Identity.Pid == (uint)Environment.ProcessId));
    }
}
