namespace AgentWatch.Collectors;

public static class MetricValidity
{
    // Reject, never clamp: a percentage outside its domain is unavailable evidence.
    public static double? DiskIdle(double? value) => value is >= 0 and <= 100 ? value : null;
}
