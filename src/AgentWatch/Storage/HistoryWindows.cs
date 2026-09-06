using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using AgentWatch.Collectors;
using AgentWatch.Configuration;
using AgentWatch.Models;

namespace AgentWatch.Storage;

public sealed class HotFileWindow
{
    public required string Name { get; init; }
    public DateTimeOffset FirstUtc { get; set; }
    public DateTimeOffset LastUtc { get; set; }
    public long? FirstSizeBytes { get; set; }
    public long? LastSizeBytes { get; set; }
    public long? MaximumSizeBytes { get; set; }
    public long? MinimumSizeBytes { get; set; }
    public long PositiveGrowthBytes { get; set; }
    public DateTimeOffset? LastWriteUtc { get; set; }
    public int Observations { get; set; }
    public int MissingObservations { get; set; }
    public int Errors { get; set; }
    public static HotFileWindow From(HotFileSample sample) => new()
    {
        Name = sample.Name, FirstUtc = sample.TimestampUtc, LastUtc = sample.TimestampUtc,
        FirstSizeBytes = sample.SizeBytes, LastSizeBytes = sample.SizeBytes, MaximumSizeBytes = sample.SizeBytes, MinimumSizeBytes = sample.SizeBytes,
        PositiveGrowthBytes = Math.Max(0, sample.SizeDeltaBytes ?? 0), LastWriteUtc = sample.LastWriteUtc, Observations = 1,
        MissingObservations = !sample.Exists && sample.Error is null ? 1 : 0, Errors = sample.Error is null ? 0 : 1
    };
    public void Merge(HotFileWindow other)
    {
        if (Name != other.Name) throw new InvalidDataException("Cannot merge different monitored files.");
        if (other.FirstUtc < FirstUtc) { FirstUtc = other.FirstUtc; FirstSizeBytes = other.FirstSizeBytes; }
        if (other.LastUtc >= LastUtc) { LastUtc = other.LastUtc; LastSizeBytes = other.LastSizeBytes; LastWriteUtc = other.LastWriteUtc; }
        if (other.MaximumSizeBytes is { } max) MaximumSizeBytes = Math.Max(MaximumSizeBytes ?? max, max);
        if (other.MinimumSizeBytes is { } min) MinimumSizeBytes = Math.Min(MinimumSizeBytes ?? min, min);
        PositiveGrowthBytes += other.PositiveGrowthBytes; Observations += other.Observations;
        MissingObservations += other.MissingObservations; Errors += other.Errors;
    }
}

public sealed class NvmeWindow
{
    public required NvmeHealth First { get; set; }
    public required NvmeHealth Last { get; set; }
    public int Samples { get; set; }
    public double TemperatureSum { get; set; }
    public int TemperatureSamples { get; set; }
    public double? MaximumTemperatureC { get; set; }
    public string ObservedHostWriteDeltaBytes { get; set; } = "0";
    public string ObservedHostReadDeltaBytes { get; set; } = "0";
    public double CounterCoverageSeconds { get; set; }
    public int CounterRegressions { get; set; }
    public static NvmeWindow From(NvmeHealth sample) => new()
    {
        First = sample, Last = sample, Samples = 1, TemperatureSum = sample.TemperatureC ?? 0,
        TemperatureSamples = sample.TemperatureC.HasValue ? 1 : 0, MaximumTemperatureC = sample.TemperatureC
    };
    public void Merge(NvmeWindow other)
    {
        if (First.PhysicalDriveNumber != other.First.PhysicalDriveNumber) throw new InvalidDataException("Cannot merge different physical drives.");
        if (other.First.TimestampUtc < First.TimestampUtc)
        {
            var combined = JsonSerializer.Deserialize<NvmeWindow>(JsonSerializer.Serialize(other, JsonDefaults.Compact), JsonDefaults.Compact)!;
            combined.Merge(this); CopyFrom(combined); return;
        }
        var writes = BigInteger.Parse(ObservedHostWriteDeltaBytes, CultureInfo.InvariantCulture) + BigInteger.Parse(other.ObservedHostWriteDeltaBytes, CultureInfo.InvariantCulture);
        var reads = BigInteger.Parse(ObservedHostReadDeltaBytes, CultureInfo.InvariantCulture) + BigInteger.Parse(other.ObservedHostReadDeltaBytes, CultureInfo.InvariantCulture);
        if (other.First.TimestampUtc > Last.TimestampUtc)
        {
            var writeDelta = CounterDelta(Last.NvmeHostWriteBytesLifetime, other.First.NvmeHostWriteBytesLifetime);
            var readDelta = CounterDelta(Last.NvmeHostReadBytesLifetime, other.First.NvmeHostReadBytesLifetime);
            if (writeDelta is { } w && readDelta is { } r && CounterDelta(Last.PowerOnHours, other.First.PowerOnHours) is not null)
            { writes += w; reads += r; CounterCoverageSeconds += (other.First.TimestampUtc - Last.TimestampUtc).TotalSeconds; }
            else CounterRegressions++;
        }
        else if (other.First.TimestampUtc < Last.TimestampUtc) CounterRegressions++; // Overlapping or corrected clock; no invented bridge.
        if (other.Last.TimestampUtc > Last.TimestampUtc) Last = other.Last;
        Samples += other.Samples; TemperatureSum += other.TemperatureSum; TemperatureSamples += other.TemperatureSamples;
        if (other.MaximumTemperatureC is { } temperature) MaximumTemperatureC = Math.Max(MaximumTemperatureC ?? temperature, temperature);
        ObservedHostWriteDeltaBytes = writes.ToString(CultureInfo.InvariantCulture); ObservedHostReadDeltaBytes = reads.ToString(CultureInfo.InvariantCulture);
        CounterCoverageSeconds += other.CounterCoverageSeconds; CounterRegressions += other.CounterRegressions;
    }
    private void CopyFrom(NvmeWindow other)
    {
        First = other.First; Last = other.Last; Samples = other.Samples; TemperatureSum = other.TemperatureSum;
        TemperatureSamples = other.TemperatureSamples; MaximumTemperatureC = other.MaximumTemperatureC;
        ObservedHostWriteDeltaBytes = other.ObservedHostWriteDeltaBytes; ObservedHostReadDeltaBytes = other.ObservedHostReadDeltaBytes;
        CounterCoverageSeconds = other.CounterCoverageSeconds; CounterRegressions = other.CounterRegressions;
    }
    public static BigInteger? CounterDelta(string previous, string current)
    {
        var before = BigInteger.Parse(previous, CultureInfo.InvariantCulture); var after = BigInteger.Parse(current, CultureInfo.InvariantCulture);
        return before >= 0 && after >= before ? after - before : null;
    }
}

public static class WindowCodec
{
    public static byte[] Encode<T>(T window)
    {
        using var output = new MemoryStream();
        using (var compressed = new BrotliStream(output, CompressionLevel.Optimal, true))
            JsonSerializer.Serialize(compressed, window, JsonDefaults.Compact);
        return output.ToArray();
    }
    public static T Decode<T>(byte[] payload)
    {
        if (payload.Length > 65536) throw new InvalidDataException("History window too large.");
        using var input = new MemoryStream(payload, false);
        using var compressed = new BrotliStream(input, CompressionMode.Decompress);
        using var bounded = new MemoryStream();
        var buffer = new byte[4096]; int count;
        while ((count = compressed.Read(buffer)) > 0)
        {
            if (bounded.Length + count > 65536) throw new InvalidDataException("History window expands beyond size limit.");
            bounded.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(bounded.ToArray(), JsonDefaults.Compact) ?? throw new InvalidDataException("Empty history window.");
    }
}

public sealed record AnomalyEvent(string Id, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Type, string Severity,
    AgentKind? Agent, string Summary, Dictionary<string, double> Evidence, bool Open);
public sealed record HistoryRow(int ResolutionSeconds, long BucketUnixSeconds, int Kind, string Item, byte[] Payload);
