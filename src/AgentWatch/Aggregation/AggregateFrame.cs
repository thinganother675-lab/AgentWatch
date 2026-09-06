using System.IO.Compression;

namespace AgentWatch.Aggregation;

// Append-only IDs: the binary payload version changes if existing IDs change.
public enum Metric
{
    Coverage, CpuTotal, CpuUser, CpuKernel, PhysicalTotal, AvailableMemory, MemoryLoad, CommitBytes, CommitLimit, CommitPercent,
    MemoryBelow512, MemoryBelow256, PagesInput, PagesOutput, PageReads, PageWrites, EstimatedPageIn, EstimatedPageOut,
    PhysicalDiskRead, PhysicalDiskWrite, DiskQueue, DiskIdle, PressureWithPageOut, PressurePageOutDiskOverlap,
    SelfCpu, SelfWorkingSet, SelfPrivate, SelfLogicalRead, SelfLogicalWrite, CollectionMs, DatabaseBytes, WalBytes, FlushMs,
    CollectorErrors, MissedSamples, SampleCount,
    CodexActive, CodexProcessCount, CodexCpu, CodexWorkingSet, CodexPrivate, CodexLogicalRead, CodexLogicalWrite, CodexLogicalOther,
    CodexReadOperations, CodexWriteOperations, CodexOtherOperations, CodexStarts, CodexExits, CodexInaccessible,
    ClaudeActive, ClaudeProcessCount, ClaudeCpu, ClaudeWorkingSet, ClaudePrivate, ClaudeLogicalRead, ClaudeLogicalWrite, ClaudeLogicalOther,
    ClaudeReadOperations, ClaudeWriteOperations, ClaudeOtherOperations, ClaudeStarts, ClaudeExits, ClaudeInaccessible,
    Count
}

public struct MetricAccumulator
{
    public double Sum, Weight, Minimum, Maximum;
    public readonly double? Average => Weight > 0 ? Sum / Weight : null;
    public void Add(double? value, double seconds)
    {
        if (value is not { } v || !double.IsFinite(v) || !double.IsFinite(seconds) || seconds <= 0) return;
        if (Weight == 0) { Minimum = v; Maximum = v; }
        else { Minimum = Math.Min(Minimum, v); Maximum = Math.Max(Maximum, v); }
        Sum += v * seconds; Weight += seconds;
    }
    public void Merge(MetricAccumulator other)
    {
        if (other.Weight <= 0) return;
        if (Weight == 0) { this = other; return; }
        Sum += other.Sum; Weight += other.Weight;
        Minimum = Math.Min(Minimum, other.Minimum); Maximum = Math.Max(Maximum, other.Maximum);
    }
}

public sealed class AggregateFrame(long bucketUnixSeconds, int resolutionSeconds = 60)
{
    public long BucketUnixSeconds { get; } = bucketUnixSeconds;
    public int ResolutionSeconds { get; } = resolutionSeconds;
    public MetricAccumulator[] Metrics { get; } = new MetricAccumulator[(int)Metric.Count];
    public double[] CpuHistogram { get; } = new double[21];
    public static readonly double[] LatencyBounds = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, double.MaxValue];
    public double[] LatencyHistogram { get; } = new double[LatencyBounds.Length];
    public MetricAccumulator this[Metric metric] => Metrics[(int)metric];
    public void Add(Metric metric, double? value, double seconds) => Metrics[(int)metric].Add(value, seconds);
    public void Merge(AggregateFrame other)
    {
        for (var i = 0; i < Metrics.Length; i++) Metrics[i].Merge(other.Metrics[i]);
        for (var i = 0; i < CpuHistogram.Length; i++) CpuHistogram[i] += other.CpuHistogram[i];
        for (var i = 0; i < LatencyHistogram.Length; i++) LatencyHistogram[i] += other.LatencyHistogram[i];
    }
    public static double? PercentileUpperBound(double[] histogram, double[] bounds, double percentile)
    {
        var total = histogram.Sum(); if (total <= 0) return null;
        var target = total * percentile; var sum = 0.0;
        for (var i = 0; i < histogram.Length; i++) { sum += histogram[i]; if (sum >= target) return bounds[i]; }
        return bounds[^1];
    }
}

public static class AggregateCodec
{
    public static byte[] Encode(AggregateFrame frame)
    {
        using var output = new MemoryStream();
        using (var compression = new BrotliStream(output, CompressionLevel.Optimal, true))
        using (var writer = new BinaryWriter(compression))
        {
            writer.Write((byte)1); writer.Write((ushort)frame.Metrics.Length);
            foreach (var m in frame.Metrics)
            {
                writer.Write(m.Weight > 0);
                if (m.Weight > 0) { writer.Write(m.Sum); writer.Write(m.Weight); writer.Write(m.Minimum); writer.Write(m.Maximum); }
            }
            foreach (var value in frame.CpuHistogram) writer.Write(value);
            foreach (var value in frame.LatencyHistogram) writer.Write(value);
        }
        return output.ToArray();
    }
    public static AggregateFrame Decode(long bucket, int resolution, byte[] payload)
    {
        if (payload.Length > 65536) throw new InvalidDataException("Aggregate payload too large.");
        var frame = new AggregateFrame(bucket, resolution);
        using var input = new MemoryStream(payload, false);
        using var compression = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(compression);
        if (reader.ReadByte() != 1 || reader.ReadUInt16() != frame.Metrics.Length)
            throw new InvalidDataException("Unsupported aggregate payload version.");
        double Value() { var v = reader.ReadDouble(); return double.IsFinite(v) ? v : throw new InvalidDataException("Non-finite aggregate metric."); }
        for (var i = 0; i < frame.Metrics.Length; i++)
            if (reader.ReadBoolean()) frame.Metrics[i] = new() { Sum = Value(), Weight = Value(), Minimum = Value(), Maximum = Value() };
        for (var i = 0; i < frame.CpuHistogram.Length; i++) frame.CpuHistogram[i] = Value();
        for (var i = 0; i < frame.LatencyHistogram.Length; i++) frame.LatencyHistogram[i] = Value();
        if (compression.ReadByte() != -1) throw new InvalidDataException("Unexpected aggregate payload trailer.");
        return frame;
    }
}
