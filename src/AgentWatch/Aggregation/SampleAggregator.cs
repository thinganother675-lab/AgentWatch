using AgentWatch.Models;

namespace AgentWatch.Aggregation;

public sealed class SampleAggregator
{
    private readonly SortedDictionary<long, AggregateFrame> frames = new();
    public void Add(FastSample sample)
    {
        if (sample.Gap)
        {
            if (sample.Self is { } self)
            {
                var bucket = Floor(sample.TimestampUtc.ToUnixTimeSeconds(), 60);
                if (!frames.TryGetValue(bucket, out var gapFrame)) frames[bucket] = gapFrame = new(bucket);
                gapFrame.Add(Metric.MissedSamples, self.MissedSamples, 1);
                gapFrame.Add(Metric.CollectorErrors, self.CollectorErrors, 1);
            }
            return;
        }
        if (sample.DurationSeconds <= 0 || !double.IsFinite(sample.DurationSeconds)) return;
        var end = sample.TimestampUtc.UtcTicks;
        var cursor = end - (long)Math.Round(sample.DurationSeconds * TimeSpan.TicksPerSecond);
        while (cursor < end)
        {
            var time = new DateTimeOffset(cursor, TimeSpan.Zero);
            var bucket = Floor(time.ToUnixTimeSeconds(), 60);
            var next = Math.Min(end, DateTimeOffset.FromUnixTimeSeconds(bucket + 60).UtcTicks);
            var weight = (next - cursor) / (double)TimeSpan.TicksPerSecond;
            if (!frames.TryGetValue(bucket, out var frame)) frames[bucket] = frame = new(bucket);
            Accumulate(frame, sample, weight);
            cursor = next;
        }
    }
    public static long Floor(long timestamp, int seconds) => (long)Math.Floor(timestamp / (double)seconds) * seconds;
    public List<AggregateFrame> Drain(DateTimeOffset throughUtc, bool includePartial = false)
    {
        var list = new List<AggregateFrame>();
        var cutoff = throughUtc.ToUnixTimeSeconds();
        foreach (var (key, frame) in frames)
            if (includePartial || key + 60 <= cutoff) list.Add(frame);
        foreach (var frame in list) frames.Remove(frame.BucketUnixSeconds);
        return list;
    }
    private static void Accumulate(AggregateFrame frame, FastSample s, double weight)
    {
        void Add(Metric m, double? v) => frame.Add(m, v, weight);
        void Total(Metric m, double? v) => Add(m, v / s.DurationSeconds);
        Add(Metric.Coverage, 1); Total(Metric.SampleCount, 1);
        if (s.Cpu is { } cpu)
        {
            Add(Metric.CpuTotal, cpu.TotalPercent); Add(Metric.CpuUser, cpu.UserPercent); Add(Metric.CpuKernel, cpu.KernelPercent);
            frame.CpuHistogram[Math.Clamp((int)Math.Ceiling(cpu.TotalPercent / 5), 0, 20)] += weight;
        }
        if (s.Memory is { } mem)
        {
            Add(Metric.PhysicalTotal, mem.PhysicalTotalBytes); Add(Metric.AvailableMemory, mem.AvailableBytes);
            Add(Metric.MemoryLoad, mem.MemoryLoadPercent); Add(Metric.CommitBytes, mem.CommitBytes);
            Add(Metric.CommitLimit, mem.CommitLimitBytes); Add(Metric.CommitPercent, mem.CommitPercent);
            Add(Metric.MemoryBelow512, mem.AvailableBytes < 512UL * 1024 * 1024 ? 1 : 0);
            Add(Metric.MemoryBelow256, mem.AvailableBytes < 256UL * 1024 * 1024 ? 1 : 0);
        }
        if (s.Pdh is { } pdh)
        {
            Add(Metric.PagesInput, pdh.PagesInputPerSecond); Add(Metric.PagesOutput, pdh.PagesOutputPerSecond);
            Add(Metric.PageReads, pdh.PageReadsPerSecond); Add(Metric.PageWrites, pdh.PageWritesPerSecond);
            Add(Metric.PhysicalDiskRead, pdh.PhysicalDiskReadBytesPerSecond); Add(Metric.PhysicalDiskWrite, pdh.PhysicalDiskWriteBytesPerSecond);
            Add(Metric.DiskQueue, pdh.PhysicalDiskQueueLength); Add(Metric.DiskIdle, pdh.PhysicalDiskIdlePercent);
            if (s.Memory is { } memory)
            {
                Add(Metric.EstimatedPageIn, pdh.PagesInputPerSecond * memory.PageSize);
                Add(Metric.EstimatedPageOut, pdh.PagesOutputPerSecond * memory.PageSize);
                if (pdh.PagesOutputPerSecond is { } pages)
                {
                    var pressure = memory.AvailableBytes < 512UL * 1024 * 1024 && pages > 0;
                    Add(Metric.PressureWithPageOut, pressure ? 1 : 0);
                    if (pdh.PhysicalDiskWriteBytesPerSecond is { } writes)
                        Add(Metric.PressurePageOutDiskOverlap, pressure && writes > 1024 * 1024 ? 1 : 0);
                }
            }
        }
        foreach (var agent in s.Agents)
        {
            if (agent.Kind == AgentKind.Other) continue;
            var offset = agent.Kind == AgentKind.Codex ? (int)Metric.CodexActive : (int)Metric.ClaudeActive;
            void A(int i, double? v) => Add((Metric)(offset + i), v);
            void T(int i, double? v) => Total((Metric)(offset + i), v);
            A(0, agent.ProcessCount > 0 ? 1 : 0); A(1, agent.ProcessCount); A(2, agent.CpuPercent);
            A(3, agent.WorkingSetBytes); A(4, agent.PrivateBytes);
            if (agent.LogicalIo is { } io)
            {
                T(5, io.LogicalReadBytes); T(6, io.LogicalWriteBytes); T(7, io.LogicalOtherBytes);
                T(8, io.ReadOperations); T(9, io.WriteOperations); T(10, io.OtherOperations);
            }
            T(11, agent.ObservedStarts); T(12, agent.ObservedExits); A(13, agent.InaccessibleProcesses);
        }
        if (s.Self is { } self)
        {
            Add(Metric.SelfCpu, self.CpuPercent); Add(Metric.SelfWorkingSet, self.WorkingSetBytes); Add(Metric.SelfPrivate, self.PrivateBytes);
            Total(Metric.SelfLogicalRead, self.LogicalIo?.LogicalReadBytes); Total(Metric.SelfLogicalWrite, self.LogicalIo?.LogicalWriteBytes);
            Add(Metric.DatabaseBytes, self.DatabaseBytes); Add(Metric.WalBytes, self.WalBytes);
            if (self.FlushMilliseconds > 0) Add(Metric.FlushMs, self.FlushMilliseconds);
            Total(Metric.CollectorErrors, self.CollectorErrors); Total(Metric.MissedSamples, self.MissedSamples);
        }
        Add(Metric.CollectionMs, s.CollectionMilliseconds);
        var bin = Array.FindIndex(AggregateFrame.LatencyBounds, value => s.CollectionMilliseconds <= value);
        frame.LatencyHistogram[bin < 0 ? frame.LatencyHistogram.Length - 1 : bin] += weight / s.DurationSeconds;
    }
}
