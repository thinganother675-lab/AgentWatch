using System.Diagnostics;
using AgentWatch.Models;
using Microsoft.Extensions.Logging;

namespace AgentWatch.Hosting;

public sealed class LocalLog(string directory)
{
    private readonly object sync = new();
    public long Failures { get; private set; }
    public void Write(string level, string message)
    {
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "agentwatch.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
                    File.Move(path, Path.Combine(directory, "agentwatch.previous.log"), true);
                var text = message.Replace('\r', ' ').Replace('\n', ' ');
                if (text.Length > 1800) text = text[..1800];
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {level} {text}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failures++; }
        }
    }
}

public sealed class LocalLogProvider(LocalLog log) : ILoggerProvider
{
    private sealed class Adapter(LocalLog target) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) target.Write(level.ToString(), formatter(state, exception)); }
    }
    public ILogger CreateLogger(string categoryName) => new Adapter(log);
    public void Dispose() { }
}

public sealed class CollectorHealthBook(LocalLog? log = null)
{
    private readonly Dictionary<string, CollectorHealth> states = new();
    private readonly Dictionary<string, long> lastLog = new();
    public IReadOnlyCollection<CollectorHealth> States => states.Values;
    public long ErrorCount => states.Values.Sum(s => s.Errors);
    public T? Read<T>(string name, Func<T> collect, Func<T, string?>? degraded = null) where T : class
    {
        try
        {
            var result = collect(); var detail = degraded?.Invoke(result);
            Record(name, detail is null ? "OK" : "WARN", detail, true, detail is not null);
            return result;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { Record(name, "ERROR", $"{e.GetType().Name}: {e.Message}", false, true); return null; }
    }
    public void Record(string name, string state, string? detail, bool success, bool failure)
    {
        states.TryGetValue(name, out var old); var now = DateTimeOffset.UtcNow;
        var updated = new CollectorHealth(name, state, detail, success ? now : old?.LastSuccessUtc,
            failure ? now : old?.LastFailureUtc, (old?.Errors ?? 0) + (failure ? 1 : 0));
        states[name] = updated;
        var ticks = Stopwatch.GetTimestamp();
        if (old?.State != state || old?.Detail != detail)
        {
            if (!lastLog.TryGetValue(name, out var last) || Stopwatch.GetElapsedTime(last, ticks).TotalSeconds >= 60)
            { log?.Write(state, $"{name}: {detail ?? "recovered/available"}"); lastLog[name] = ticks; }
        }
    }
}
