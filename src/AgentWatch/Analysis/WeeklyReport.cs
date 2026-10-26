using System.Globalization;
using System.Text.Json;
using AgentWatch.Aggregation;
using AgentWatch.Analysis.Usage;
using AgentWatch.Configuration;
using AgentWatch.Hosting;
using AgentWatch.Models;

namespace AgentWatch.Analysis;

public sealed record MetricCoverage(string Metric, double ValidSeconds, double? PercentOfObservedTime);
public sealed record ObservationQuality(string State, double RequestedSeconds, double StoredBucketSpanSeconds,
    double ObservedSeconds, double UnobservedWithinStoredSpanSeconds, double? PercentOfStoredSpan,
    double? PercentOfRequestedPeriod, bool BoundaryBucketsExtendOutsideRequest, DateTimeOffset? LastPersistedSampleUtc,
    double? PersistenceAgeSeconds, string Freshness, string? LastRecordedRunId, string? LastRecordedState,
    IReadOnlyList<MetricCoverage> Metrics, IReadOnlyList<CollectorHealth> LatestCollectors, IReadOnlyList<string> Notes);
public sealed record EvidenceWindow(string Kind, DateTimeOffset FromUtc, DateTimeOffset ToUtc, int ResolutionSeconds,
    double ObservedSeconds, double? AverageCpuPercent, double? MinimumAvailableBytes, double? LowMemorySeconds,
    double? LowMemoryWithPageOutSeconds, double? EstimatedPageOutBytes, double? PhysicalDiskWriteBytes,
    double? CodexLogicalWriteBytes, double? ClaudeLogicalWriteBytes, double? CodexPresenceSeconds, double? ClaudePresenceSeconds);
public sealed record WeeklyFinding(string Code, string Confidence, string Summary, IReadOnlyList<string> Evidence,
    IReadOnlyList<string> AlternativeExplanations, string NextAction)
{
    public WeeklyFinding(string code, string confidence, string summary, string evidence, string nextAction)
        : this(code, confidence switch { "observed" => "high", "observed-association" => "medium", _ => "low" },
            summary, [evidence], Alternatives(code), nextAction) { }
    private static string[] Alternatives(string code) => code switch
    {
        "memory-paging" or "low-memory" => ["Другие приложения могли создавать memory pressure.", "Paging включает file-backed страницы; это не только pagefile.", "Период мог не сопровождаться заметной пользователю задержкой."],
        "high-cpu" => ["Высокая загрузка могла быть полезной ожидаемой работой.", "Часть нагрузки могла исходить от других приложений."],
        "nvme-health" => ["Возможна замена устройства или сброс счётчиков; проверьте регрессии и идентичность накопителя."],
        "monitor-budget" => ["Запуск, загрузка runtime и короткий период могут влиять на средние и пики."],
        _ => ["Короткие процессы или пики могли пройти между опросами.", "Неполные данные и отсутствие отметок ограничивают вывод."]
    };
}
public sealed record WeeklyReport(int SchemaVersion, string AppVersion, DateTimeOffset GeneratedUtc, WatchReport Measurements,
    ObservationQuality Quality, IReadOnlyList<WeeklyFinding> Findings, IReadOnlyList<EvidenceWindow> Episodes,
    int MatchingEpisodeCount, MarkerInventory UserObservations, RunInventory RunHistory, IReadOnlyList<string> Caveats)
{
    public UsageInventory SessionUsage { get; init; } = new([], [], [], null, false, []);
    public IReadOnlyList<TemporalContext> Contexts { get; init; } = [];
    public IReadOnlyList<TimelineEvent> Events { get; init; } = [];
    public bool EventsTruncated { get; init; }
}

public sealed partial class ReportService
{
    public WeeklyReport GetWeeklyReport(QueryPeriod period, string? marksDirectory = null, CancellationToken cancellationToken = default, string? usageDirectory = null, string? pricingPath = null, string? runDirectory = null, DateTimeOffset? generatedUtc = null)
    {
        if ((period.ToUtc - period.FromUtc).TotalDays > 31)
            throw new ArgumentException("Weekly analysis is limited to 31 days; request a shorter period.");
        var markers = UserMarkers.Read(marksDirectory ?? UserMarkers.DefaultDirectory, period);
        var usage = UsageStore.Read(usageDirectory ?? UsageStore.DefaultDirectory, period, UsagePricing.Load(pricingPath));
        var analysis = new WeeklyAnalysis(period, markers, usage);
        var report = ReadReport(period, cancellationToken, analysis);
        var runs = RunJournal.Read(runDirectory ?? Path.GetDirectoryName(Path.GetFullPath(databasePath))!, period.FromUtc, period.ToUtc);
        return analysis.Build(report, markers, runs, sampling ?? new(), generatedUtc);
    }
}

internal sealed class WeeklyAnalysis
{
    private readonly EpisodeSelector selector = new();
    private readonly UsageInventory usage;
    private double diskWriteValidSeconds, jointMemoryPagingValidSeconds, selfIoValidSeconds;
    private readonly List<ContextWindow> contexts = [];
    public DateTimeOffset ReadFromUtc { get; }
    public DateTimeOffset ReadToUtc { get; }
    public WeeklyAnalysis(QueryPeriod period, MarkerInventory markers, UsageInventory usage)
    {
        this.usage = usage;
        foreach (var marker in markers.Markers) contexts.Add(new(marker.Id, "marker", marker.TimestampUtc.AddMinutes(-15), marker.TimestampUtc.AddMinutes(15)));
        foreach (var session in usage.Sessions.Take(100))
        {
            var from = session.FirstObservedUtc > period.FromUtc ? session.FirstObservedUtc : period.FromUtc;
            var to = session.LastActivityUtc.AddTicks(1) < period.ToUtc ? session.LastActivityUtc.AddTicks(1) : period.ToUtc;
            if (to > from) contexts.Add(new(session.Agent + ":" + session.SessionId, "session", from, to));
        }
        ReadFromUtc = contexts.Count > 0 && contexts.Min(c => c.From) < period.FromUtc ? contexts.Min(c => c.From) : period.FromUtc;
        ReadToUtc = contexts.Count > 0 && contexts.Max(c => c.To) > period.ToUtc ? contexts.Max(c => c.To) : period.ToUtc;
    }
    public void ObserveContext(AggregateFrame frame) { foreach (var context in contexts) context.Observe(frame); }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public void Observe(AggregateFrame frame)
    {
        selector.Observe(frame);
        diskWriteValidSeconds += frame[Metric.PhysicalDiskWrite].Weight;
        jointMemoryPagingValidSeconds += frame[Metric.PressureWithPageOut].Weight;
        selfIoValidSeconds += frame[Metric.SelfLogicalWrite].Weight;
    }

    public WeeklyReport Build(WatchReport report, MarkerInventory markers, RunInventory runs, SamplingConfig sampling, DateTimeOffset? generatedUtc = null)
    {
        var now = generatedUtc ?? DateTimeOffset.UtcNow;
        var quality = Quality(report, now, sampling);
        var episodes = selector.Finish();
        var result = new WeeklyReport(1, "0.2.1", now, report, quality, Findings(report, quality), episodes, selector.MatchingCount, markers, runs,
        [
            "Выводы относятся к наблюдаемому времени. Пропуски включают сон, остановки и недоступность наблюдения; это не измеренное время работы компьютера.",
            "Эпизоды выбраны по агрегатам: RAM <512 МиБ, средний CPU >=80%, либо logical writes агента >=100 МиБ/мин. Это ориентиры для проверки, не диагноз.",
            "Начало и конец эпизода — границы сохранённых бакетов. Они не дают точного времени начала торможения. Для суточных агрегатов эпизоды не строятся.",
            "Совпадение по времени и присутствие агента не доказывают причинную связь. Истории команд и задач в этих данных нет.",
            "Показаны до 12 выбранных эпизодов, до 200 отметок и запусков. Журналы и отметки читаются отдельно от транзакционного снимка метрик.",
            "Текущее состояние сборщиков не восстанавливает их полную историческую доступность; для периода используйте valid seconds каждого показателя."
        ]) { SessionUsage = usage, Contexts = contexts.Select(c => c.Build(usage)).ToArray() };
        var events = EventTimeline.Build(result);
        return result with { Events = events.Facts, EventsTruncated = events.Truncated };
    }

    private ObservationQuality Quality(WatchReport r, DateTimeOffset now, SamplingConfig sampling)
    {
        var p = r.Period; var observed = p.ObservedSeconds;
        var span = p.FirstIncludedBucketUtc.HasValue && p.LastIncludedBucketEndUtc.HasValue
            ? (p.LastIncludedBucketEndUtc.Value - p.FirstIncludedBucketUtc.Value).TotalSeconds : 0;
        var requested = (p.ToUtc - p.FromUtc).TotalSeconds;
        var edge = p.FirstIncludedBucketUtc < p.FromUtc || p.LastIncludedBucketEndUtc > p.ToUtc;
        MetricCoverage C(string metric, double seconds) => new(metric, seconds, observed > 0 ? 100 * seconds / observed : null);
        var metrics = new[] { C("system.cpu", r.System.Cpu.TotalPercent.ValidSeconds), C("system.memory", r.System.Memory.AvailableBytes.ValidSeconds),
            C("paging.pageOut", r.System.Paging.PagesOutputPerSecond.ValidSeconds), C("disk.write", diskWriteValidSeconds), C("disk.queue", r.Storage.QueueLength.ValidSeconds), C("disk.idle", r.Storage.IdlePercent.ValidSeconds), C("memoryPaging.joint", jointMemoryPagingValidSeconds), C("monitor.logicalIo", selfIoValidSeconds),
            C("agents.codex.logicalIo", r.Agents["codex"].LogicalIoValidSeconds), C("agents.claude.logicalIo", r.Agents["claude"].LogicalIoValidSeconds),
            C("monitor.cpu", r.AgentWatch.CpuPercent.ValidSeconds), C("monitor.memory", r.AgentWatch.WorkingSetBytes.ValidSeconds) };
        DateTimeOffset? last = Metadata.TryGetValue("lastSampleUtc", out var lastText)
            && DateTimeOffset.TryParse(lastText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;
        double? age = last.HasValue ? (now - last.Value).TotalSeconds : null;
        var batchSeconds = Metadata.TryGetValue("persistBatchSeconds", out var batchText)
            && int.TryParse(batchText, out var batch) && batch is >= 60 and <= 1800 ? batch : sampling.PersistBatchMinutes * 60;
        var freshness = age is null ? "unknown" : age < -sampling.FastSampleSeconds * 3 ? "clock-inconsistent"
            : age <= batchSeconds + sampling.FastSampleSeconds * 3 ? "fresh" : "stale";
        var notes = new List<string>(); var collectors = new List<CollectorHealth>();
        if (Metadata.TryGetValue("collectors", out var health))
        {
            try { collectors = JsonSerializer.Deserialize<List<CollectorHealth>>(health, JsonDefaults.Compact) ?? []; }
            catch (JsonException) { notes.Add("Последние сведения о сборщиках повреждены; доступность оценивается по сохранённым метрикам."); }
        }
        if (Metadata.TryGetValue("logFailures", out var logFailureText) && long.TryParse(logFailureText, out var logFailures) && logFailures > 0) notes.Add($"Диагностический журнал не удалось записать {logFailures} раз; проверьте доступность его каталога.");
        if (r.Agents.Values.Any(a => a.InaccessibleCandidateCount.Maximum > 0)) notes.Add("Некоторые процессы были недоступны; agent logical I/O — наблюдаемый нижний предел.");
        if (Metadata.GetValueOrDefault("state") == "stopped") notes.Add("Последний сохранённый запуск остановлен; свежесть последней записи не означает, что сбор продолжается.");
        if (observed <= 0) notes.Add("Нет валидного времени наблюдения; выводы о нагрузке недоступны.");
        if (span > observed + 60) notes.Add("Внутри сохранённого периода есть ненаблюдавшееся время. Причина по агрегатам однозначно не определяется.");
        if (edge) notes.Add("Крайние бакеты выходят за запрошенный период; точный процент покрытия запроса неизвестен.");
        if (observed < requested * 0.8) notes.Add("Наблюдалась лишь часть запрошенного календарного периода; не переносите результаты на всю неделю.");
        if (freshness != "fresh") notes.Add("Последние сохранённые данные не подтверждают свежий сбор; проверьте status и журнал запуска.");
        if (r.Storage.Nvme is null || r.Storage.Nvme.BaselineLearning) notes.Add("Нет семи полных суточных интервалов NVMe; baseline ещё не сформирован.");
        if (r.AgentWatch.MissedSamples > 0 || r.AgentWatch.CollectorErrors > 0) notes.Add("В периоде зарегистрированы пропуски опроса или ошибки сборщиков.");
        notes.AddRange(r.Limitations.Where(x => x.StartsWith("disk.idle:", StringComparison.Ordinal)));
        var coreIncomplete = metrics.Take(4).Any(m => m.PercentOfObservedTime is null or < 90);
        var state = observed <= 0 ? "no-data" : coreIncomplete || observed < 1800 ? "limited" : "usable-for-observed-time";
        return new(state, requested, span, observed, Math.Max(0, span - observed), span > 0 ? 100 * observed / span : null,
            !edge && requested > 0 ? 100 * observed / requested : null, edge, last, age, freshness,
            Metadata.GetValueOrDefault("runId"), Metadata.GetValueOrDefault("state"), metrics, collectors, notes);
    }

    private static IReadOnlyList<WeeklyFinding> Findings(WatchReport r, ObservationQuality q)
    {
        var findings = new List<WeeklyFinding>();
        if (q.ObservedSeconds <= 0)
            return [new("insufficient-data", "insufficient", "Вывод о нагрузке пока невозможен.", "Нет валидного времени наблюдения.", "Проверьте status и дождитесь сохранённой партии данных.")];
        if (q.State == "limited") findings.Add(new("limited-coverage", "limited", "Часть выводов ограничена качеством данных.",
            "Недостаточно наблюдений или меньше 90% валидного времени у одного из основных показателей.", "Сначала проверьте таблицу покрытия и недоступные сборщики."));
        var overlap = r.System.Paging.LowRamWithPageOutSeconds;
        if (overlap >= 120)
            findings.Add(new("memory-paging", "observed-association", "Нехватка доступной памяти совпадала с выгрузкой страниц на диск.",
                $"RAM <512 МиБ и page-out наблюдались одновременно {overlap / 60:F1} мин; минимальная RAM {r.System.Memory.AvailableBytes.Minimum / 1048576:F0} МиБ.",
                "Сопоставьте эти эпизоды со своими отметками; при повторении проверьте потребление памяти процессами. Агент не объявляется причиной."));
        else if (r.System.Memory.SecondsBelow512MiB >= 120)
            findings.Add(new("low-memory", "observed", "Были периоды малого запаса доступной памяти.",
                $"RAM <512 МиБ: {r.System.Memory.SecondsBelow512MiB / 60:F1} мин. Совпадение с page-out не установлено на значимом интервале.",
                "Проверьте субъективные торможения и наличие валидных данных paging; одного значения свободной RAM недостаточно для диагноза."));
        if (r.System.Cpu.TotalPercent.Average >= 70 && r.System.Cpu.TotalPercent.ValidSeconds >= q.ObservedSeconds * 0.9)
            findings.Add(new("high-cpu", "observed", "Средняя загрузка CPU была высокой в наблюдаемом периоде.",
                $"Средний CPU {r.System.Cpu.TotalPercent.Average:F1}% при {r.System.Cpu.TotalPercent.ValidSeconds / 3600:F2} ч валидных данных.",
                "Посмотрите выбранные CPU-эпизоды и нагрузку агентов в тех же бакетах."));
        if (r.Storage.Nvme is { } n && (n.Last.CriticalWarning != 0 || n.First.MediaErrors != n.Last.MediaErrors))
            findings.Add(new("nvme-health", "observed", "В NVMe health есть изменение, требующее проверки.",
                $"Critical Warning: {n.Last.CriticalWarning}; Media Errors: {n.First.MediaErrors} → {n.Last.MediaErrors}; регрессии счётчиков: {n.CounterRegressions}.",
                "Проверьте подробный disk-отчёт и диагностические данные накопителя; связь с конкретным приложением не установлена."));
        if (r.AgentWatch.CpuPercent.Average > 0.3 || r.AgentWatch.WorkingSetBytes.Maximum > 100 * 1048576)
            findings.Add(new("monitor-budget", "observed", "Собственная нагрузка монитора требует внимания.",
                $"Средний CPU {r.AgentWatch.CpuPercent.Average:F3}%, пик working set {r.AgentWatch.WorkingSetBytes.Maximum / 1048576:F1} МиБ.",
                "Сравните длительный self-отчёт с исходными замерами перед добавлением новых сборщиков."));
        if (findings.All(f => f.Code is "limited-coverage")) findings.Add(new("no-dominant-bottleneck", "undetermined",
            "По этим данным единое основное ограничение не установлено.", "Проверьте отдельные эпизоды и отметки; отсутствие сработавшего правила не доказывает отсутствие торможений.",
            "Продолжайте обычную работу и отмечайте заметные задержки."));
        return findings;
    }
}

internal sealed class EpisodeSelector
{
    private sealed record Candidate(string Kind, long Start, long End, int Resolution, AggregateFrame Frame);
    private readonly Dictionary<string, Candidate> active = new();
    private readonly List<Candidate> best = [];
    public int MatchingCount { get; private set; }
    public void Observe(AggregateFrame frame)
    {
        var kinds = new HashSet<string>(); var coverage = frame[Metric.Coverage].Sum;
        if (frame.ResolutionSeconds <= 900 && coverage >= frame.ResolutionSeconds * 0.8)
        {
            if (frame[Metric.MemoryBelow512].Sum >= 30) kinds.Add("low-memory");
            if (frame[Metric.CpuTotal].Weight >= coverage * 0.9 && frame[Metric.CpuTotal].Average >= 80) kinds.Add("high-cpu");
            if (frame[Metric.CodexLogicalWrite].Average >= 100 * 1048576.0 / 60
                || frame[Metric.ClaudeLogicalWrite].Average >= 100 * 1048576.0 / 60) kinds.Add("agent-writes");
        }
        foreach (var key in active.Keys.ToArray())
        {
            var previous = active[key];
            if (!kinds.Contains(key) || previous.End != frame.BucketUnixSeconds || previous.Resolution != frame.ResolutionSeconds
                || frame.BucketUnixSeconds + frame.ResolutionSeconds - previous.Start > 900)
            { Keep(previous); active.Remove(key); }
        }
        foreach (var kind in kinds)
        {
            if (active.TryGetValue(kind, out var previous))
            { previous.Frame.Merge(frame); active[kind] = previous with { End = frame.BucketUnixSeconds + frame.ResolutionSeconds }; }
            else
            {
                var copy = new AggregateFrame(frame.BucketUnixSeconds, frame.ResolutionSeconds); copy.Merge(frame);
                active[kind] = new(kind, frame.BucketUnixSeconds, frame.BucketUnixSeconds + frame.ResolutionSeconds, frame.ResolutionSeconds, copy);
            }
        }
    }
    private static double Score(Candidate c) => c.Kind switch
    { "low-memory" => c.Frame[Metric.MemoryBelow512].Sum, "high-cpu" => c.Frame[Metric.CpuTotal].Sum / 100, _ => c.Frame[Metric.Coverage].Sum };
    private void Keep(Candidate candidate)
    {
        MatchingCount++; best.Add(candidate);
        if (best.Count > 12) best.Remove(best.OrderBy(Score).ThenBy(c => c.Start).First());
    }
    public IReadOnlyList<EvidenceWindow> Finish()
    {
        foreach (var candidate in active.Values) Keep(candidate); active.Clear();
        double? Sum(AggregateFrame f, Metric m) => f[m].Weight > 0 ? f[m].Sum : null;
        return best.OrderByDescending(Score).ThenBy(c => c.Start).Select(c => new EvidenceWindow(c.Kind,
            DateTimeOffset.FromUnixTimeSeconds(c.Start), DateTimeOffset.FromUnixTimeSeconds(c.End), c.Resolution,
            c.Frame[Metric.Coverage].Sum, c.Frame[Metric.CpuTotal].Average,
            c.Frame[Metric.AvailableMemory].Weight > 0 ? c.Frame[Metric.AvailableMemory].Minimum : null,
            Sum(c.Frame, Metric.MemoryBelow512), Sum(c.Frame, Metric.PressureWithPageOut), Sum(c.Frame, Metric.EstimatedPageOut),
            Sum(c.Frame, Metric.PhysicalDiskWrite), Sum(c.Frame, Metric.CodexLogicalWrite), Sum(c.Frame, Metric.ClaudeLogicalWrite),
            Sum(c.Frame, Metric.CodexActive), Sum(c.Frame, Metric.ClaudeActive))).ToArray();
    }
}
