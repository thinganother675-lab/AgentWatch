using System.ComponentModel;
using AgentWatch.Models;
using AgentWatch.Windows;

namespace AgentWatch.Collectors;

public sealed class PdhCollector : IDisposable
{
    public static readonly string[] Paths =
    [
        @"\Memory\Pages Input/sec", @"\Memory\Pages Output/sec", @"\Memory\Page Reads/sec", @"\Memory\Page Writes/sec",
        @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", @"\PhysicalDisk(_Total)\Disk Write Bytes/sec",
        @"\PhysicalDisk(_Total)\Avg. Disk Queue Length", @"\PhysicalDisk(_Total)\% Idle Time"
    ];
    private readonly SafePdhHandle query;
    private readonly nint[] counters = new nint[Paths.Length];
    private readonly Dictionary<string, string> errors = new();
    private bool primed;
    public IReadOnlyDictionary<string, string> Errors => errors;
    public PdhCollector()
    {
        var code = NativeMethods.PdhOpenQuery(null, 0, out query);
        if (code != 0) { query.Dispose(); throw new Win32Exception(unchecked((int)code), "PdhOpenQuery failed"); }
        for (var i = 0; i < counters.Length; i++)
        {
            code = NativeMethods.PdhAddEnglishCounter(query, Paths[i], 0, out counters[i]);
            if (code != 0) { counters[i] = 0; errors[Paths[i]] = $"PDH 0x{code:X8}"; }
        }
    }
    public PdhSample Read(bool reset = false)
    {
        var code = NativeMethods.PdhCollectQueryData(query);
        if (code != 0) { primed = false; throw new Win32Exception(unchecked((int)code), "PdhCollectQueryData failed"); }
        var valid = primed && !reset;
        primed = true;
        double? Value(int i)
        {
            if (!valid || counters[i] == 0) return null;
            var result = NativeMethods.PdhGetFormattedCounterValue(counters[i], 0x200 | 0x8000, out _, out var value);
            if (result != 0 || value.Status > 1 || !double.IsFinite(value.DoubleValue) || value.DoubleValue < 0)
            {
                errors[Paths[i]] = $"PDH 0x{result:X8}, status 0x{value.Status:X8}";
                return null;
            }
            errors.Remove(Paths[i]);
            return value.DoubleValue;
        }
        return new(Value(0), Value(1), Value(2), Value(3), Value(4), Value(5), Value(6), Value(7));
    }
    public void Dispose() => query.Dispose();
}
