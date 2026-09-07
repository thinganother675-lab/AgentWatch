using System.Text;
using AgentWatch.Analysis;

namespace AgentWatch.Cli;

public static partial class WeeklyText
{
    private static void AppendSessionsAndContexts(StringBuilder s, WeeklyReport w)
    {
        s.AppendLine("\n## FACTS — контекст отметок ±15 минут\n");
        foreach (var c in w.Contexts.Where(c => c.SubjectType == "marker"))
        {
            s.AppendLine($"- **Отметка `{c.SubjectId}`: {c.FromUtc:yyyy-MM-dd HH:mm}–{c.ToUtc:HH:mm} UTC.** Наблюдалось {H(c.ObservedSeconds)}; CPU средний / пик {N(c.CpuPercent.Average)} / {N(c.CpuPercent.Maximum)}%; RAM min {B(c.AvailableMemoryBytes.Minimum)}; page-out {B(c.EstimatedPageOutBytes)}; disk writes {B(c.PhysicalDiskWriteBytes)}.");
            foreach (var (agent, a) in c.Agents) s.AppendLine($"  {agent}: CPU {N(a.AverageCpuPercent)}%, working set {B(a.AverageWorkingSetBytes)}, logical writes {B(a.LogicalWriteBytes)}.");
            s.AppendLine($"  Сессии с пересечением во времени: {Safe(string.Join(", ", c.OverlappingSessions.Take(10)))}. Детализация {c.MaximumResolutionSeconds} с. Ресурсы относятся ко всем процессам вида агента, не к отдельной сессии.");
        }
        s.AppendLine("\n## FACTS — сессии и usage\n");
        s.AppendLine($"Последний импорт: {w.SessionUsage.LastImportUtc:O}. Сессий в результате: {w.SessionUsage.Sessions.Count}; список ограничен: {w.SessionUsage.Truncated}.");
        s.AppendLine("\nТокены ниже относятся к запрошенному периоду. Input Codex включает cache; input Claude исключает cache. Final output и reasoning остаются неизвестными, если источник не подтверждает окончательный счётчик. Стоимость — только оценка по явно заданному тарифу, не счёт за подписку.");
        foreach (var session in w.SessionUsage.Sessions.Take(100))
        {
            s.AppendLine($"\n- **{session.Agent} `{Safe(session.SessionId)}`**: start {session.StartedUtc:O}; first observed {session.FirstObservedUtc:O}; last activity {session.LastActivityUtc:O}; end {session.EndedUtc:O}. Пустой end означает неизвестный конец, а не работающий процесс.");
            s.AppendLine($"  Модели: {Safe(string.Join(", ", session.Models))}; effort: {Safe(string.Join(", ", session.ReasoningEfforts))}.");
            var t = session.TokensInRequestedPeriod;
            s.AppendLine($"  Tokens: input {N(t.Input, "F0")}, output final {N(t.Output, "F0")}, cache read {N(t.CacheRead, "F0")}, cache write {N(t.CacheWrite, "F0")}, reasoning final {N(t.ReasoningOutput, "F0")}; output / reasoning reported-only {N(t.ReportedOutput, "F0")} / {N(t.ReportedReasoningOutput, "F0")}. Cost estimate USD: {(session.CostEstimateUsd.HasValue ? session.CostEstimateUsd.Value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) : "неизвестно")}; {Safe(session.CostBasis)}.");
            var context = w.Contexts.FirstOrDefault(c => c.SubjectType == "session" && c.SubjectId == session.Agent + ":" + session.SessionId);
            if (context is not null)
            {
                var a = context.Agents[session.Agent];
                s.AppendLine($"  Во временном окне сессии: наблюдалось {H(context.ObservedSeconds)}, system RAM min {B(context.AvailableMemoryBytes.Minimum)}, {session.Agent} CPU {N(a.AverageCpuPercent)}%, logical writes {B(a.LogicalWriteBytes)}. Это общий временной контекст вида агента, не атрибуция этой сессии.");
            }
        }
        if (w.SessionUsage.Sessions.Count > 100) s.AppendLine("\nВ тексте показаны первые 100 сессий; дополнительные доступны в JSON, без детального ресурсного контекста.");
        foreach (var note in w.SessionUsage.Notes) s.AppendLine("\n- " + Safe(note));
        s.AppendLine("\n## FACTS — события\n");
        s.AppendLine($"В JSON {w.Events.Count} событий, ограничение достигнуто: {w.EventsTruncated}. Ниже до 100 событий, кроме отдельных usage/activity записей.");
        foreach (var e in w.Events.Where(e => e.Type is not ("session.usage" or "session.activity")).Take(100))
            s.AppendLine($"- {e.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC — `{Safe(e.Type)}`, {Safe(e.Agent ?? "monitor/user")}, {Safe(e.SessionId ?? e.RunId ?? "")}: {Safe(e.Summary)}. ID `{e.Id}`.");
    }
}
