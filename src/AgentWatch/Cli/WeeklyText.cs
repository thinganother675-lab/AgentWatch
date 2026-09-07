using System.Globalization;
using System.Text;
using AgentWatch.Analysis;

namespace AgentWatch.Cli;

public static partial class WeeklyText
{
    private static string N(double? value, string format = "F1") => value?.ToString(format, CultureInfo.InvariantCulture) ?? "нет данных";
    private static string H(double? seconds) => N(seconds / 3600, "F2") + " ч";
    private static string B(double? bytes) => bytes.HasValue ? N(bytes / 1_000_000, "F2") + " МБ" : "нет данных";
    private static string Safe(string text) => System.Net.WebUtility.HtmlEncode(text).Replace("\\", "\\\\").Replace("|", "\\|")
        .Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("\r", " ").Replace("\n", " ");
    public static string Render(WeeklyReport w)
    {
        var r = w.Measurements; var q = w.Quality; var s = new StringBuilder();
        s.AppendLine($"# AgentWatch {w.AppVersion} — отчёт наблюдения");
        s.AppendLine($"\nПериод UTC: {r.Period.FromUtc:yyyy-MM-dd HH:mm:ss} — {r.Period.ToUtc:yyyy-MM-dd HH:mm:ss}. Сформирован {w.GeneratedUtc:O}.");
        s.AppendLine($"\n**Наблюдалось {H(q.ObservedSeconds)} из запрошенных {H(q.RequestedSeconds)}.** Это время валидных наблюдений, не uptime компьютера.");
        s.AppendLine("\n## INTERPRETATION — выводы и гипотезы\n");
        foreach (var f in w.Findings) s.AppendLine($"- **{Safe(f.Summary)}** Основание: {Safe(string.Join("; ", f.Evidence))} Следующий шаг: {Safe(f.NextAction)} Уверенность: `{f.Confidence}`. Альтернативы: {Safe(string.Join("; ", f.AlternativeExplanations))}.");
        s.AppendLine("\n## FACTS — качество данных\n");
        s.AppendLine($"Состояние: `{q.State}`. Свежесть последних данных: `{q.Freshness}`, возраст {N(q.PersistenceAgeSeconds)} с. Последнее сохранённое состояние процесса: `{q.LastRecordedState ?? "unknown"}`.");
        s.AppendLine($"\nОхват календарного запроса: {N(q.PercentOfRequestedPeriod)}%. Покрытие между крайними сохранёнными бакетами: {N(q.PercentOfStoredSpan)}%. Ненаблюдавшееся время внутри этих границ: {H(q.UnobservedWithinStoredSpanSeconds)}.");
        s.AppendLine($"\nОшибки сборщиков: {N(r.AgentWatch.CollectorErrors, "F0")}; пропуски опроса: {N(r.AgentWatch.MissedSamples, "F0")}. Детализация до {r.Period.MaximumResolutionSeconds} с.");
        s.AppendLine("\n| Показатель | Валидное время | Доля наблюдаемого времени |\n|---|---:|---:|");
        foreach (var m in q.Metrics) s.AppendLine($"| {m.Metric} | {H(m.ValidSeconds)} | {N(m.PercentOfObservedTime)}% |");
        foreach (var note in q.Notes) s.AppendLine("\n- " + note);
        foreach (var collector in q.LatestCollectors.Where(c => c.State != "OK")) s.AppendLine($"\n- Последнее состояние {Safe(collector.Name)}: {Safe(collector.State)} — {Safe(collector.Detail ?? "нет деталей")}");
        s.AppendLine("\n## FACTS — нагрузка\n");
        s.AppendLine($"CPU средний / пик: {N(r.System.Cpu.TotalPercent.Average)} / {N(r.System.Cpu.TotalPercent.Maximum)}%. RAM минимум: {B(r.System.Memory.AvailableBytes.Minimum)}. RAM <512 МиБ: {H(r.System.Memory.SecondsBelow512MiB)}.");
        s.AppendLine($"\nОценка page-out на диск: {B(r.System.Paging.EstimatedPageOutToDiskBytes)}; Windows physical-disk writes: {B(r.Storage.PhysicalDiskWriteBytes)}. Pagefile-only I/O не выделяется.");
        s.AppendLine("\n| Агент | Присутствие | Logical reads | Logical writes | Средний CPU |\n|---|---:|---:|---:|---:|");
        foreach (var (name, a) in r.Agents) s.AppendLine($"| {name} | {H(a.PresenceSeconds)} | {B(a.LogicalProcessReadBytes)} | {B(a.LogicalProcessWriteBytes)} | {N(a.CpuPercent.Average)}% |");
        if (r.Storage.Nvme is { } n)
        {
            s.AppendLine($"\nNVMe Host Writes delta: **{n.ObservedNvmeHostWriteDeltaBytes ?? "нет данных"} байт**, endpoints {n.First.TimestampUtc:O} — {n.Last.TimestampUtc:O}; регрессии {n.CounterRegressions}.");
            s.AppendLine($"\nТемпература max {N(n.MaximumTemperatureC)} °C; Percentage Used {n.First.PercentageUsed} → {n.Last.PercentageUsed}%; Media Errors {n.First.MediaErrors} → {n.Last.MediaErrors}. Полных суточных интервалов {n.DailyIntervals.Count(d => d.ApproximatelyFullDay)}; baseline learning: {n.BaselineLearning}.");
        }
        else s.AppendLine("\nNVMe health за период недоступен.");
        s.AppendLine($"\nAgentWatch: CPU {N(r.AgentWatch.CpuPercent.Average, "F4")}%; working set средний / пик {B(r.AgentWatch.WorkingSetBytes.Average)} / {B(r.AgentWatch.WorkingSetBytes.Maximum)}; logical writes {B(r.AgentWatch.LogicalProcessWriteBytes)}; проекция {B(r.AgentWatch.ProjectedLogicalWriteBytesPerDay)}/сутки. Это проекция наблюдаемого I/O, не измеренные NVMe-записи.");
        s.AppendLine("\n## Эпизоды для проверки\n");
        s.AppendLine($"Показано {w.Episodes.Count} из {w.MatchingEpisodeCount} подходящих окон. Соседние бакеты объединяются максимум до 15 минут. Отсутствие эпизода не доказывает отсутствие торможения.");
        foreach (var e in w.Episodes)
            s.AppendLine($"\n- **{e.FromUtc:yyyy-MM-dd HH:mm}–{e.ToUtc:HH:mm} UTC, {e.Kind}**: наблюдалось {H(e.ObservedSeconds)}, CPU {N(e.AverageCpuPercent)}%, RAM min {B(e.MinimumAvailableBytes)}, low RAM + page-out {N(e.LowMemoryWithPageOutSeconds)} с; disk writes {B(e.PhysicalDiskWriteBytes)}, Codex / Claude logical writes {B(e.CodexLogicalWriteBytes)} / {B(e.ClaudeLogicalWriteBytes)}.");
        s.AppendLine("\n## Ваши отметки\n");
        foreach (var mark in w.UserObservations.Markers)
        {
            var nearby = w.Episodes.Count(e => mark.TimestampUtc >= e.FromUtc.AddMinutes(-15) && mark.TimestampUtc <= e.ToUtc.AddMinutes(15));
            s.AppendLine($"- {mark.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC — {Safe(mark.Note)}. Выбранных эпизодов в пределах ±15 мин: {nearby}.");
        }
        if (w.UserObservations.Markers.Count == 0) s.AppendLine("Отметок нет. Для следующего заметного торможения: `AgentWatch.exe mark --note \"Что было заметно\"`.");
        s.AppendLine($"\nОшибок чтения отметок: {w.UserObservations.UnreadableEntries}; список ограничен: {w.UserObservations.Truncated}.");
        s.AppendLine("\n## Запуски и журналы\n");
        foreach (var run in w.RunHistory.Runs) s.AppendLine($"- `{Safe(run.Id)}` — версия {Safe(run.AppVersion)}, {run.StartedUtc:O} → {run.EndedUtc:O}, `{Safe(run.Outcome)}`, ошибок записи журнала {run.LogFailures}.");
        s.AppendLine($"\n{w.RunHistory.Caveat} Ошибок чтения: {w.RunHistory.UnreadableEntries}; список ограничен: {w.RunHistory.Truncated}.");
        AppendSessionsAndContexts(s, w);
        s.AppendLine("\n## Ограничения\n");
        foreach (var caveat in w.Caveats) s.AppendLine("- " + caveat);
        foreach (var caveat in r.Limitations) s.AppendLine("- " + caveat);
        return s.ToString();
    }
}
