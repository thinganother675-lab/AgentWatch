# AgentWatch 0.1.0

Локальная служба наблюдения за Windows 11 x64 во время работы с Codex и Claude. Показывает нагрузку CPU, доступную RAM и commit, paging, логический I/O деревьев агентов, размеры четырёх известных файлов Codex и здоровье NVMe. История остаётся на компьютере. Это первая версия для накопления эксплуатационной статистики; длительный burn-in ещё предстоит.

## Быстрый старт

Готовый автономный файл после сборки: `artifacts/publish/AgentWatch.exe`. Установленный .NET, SDK и NuGet нужны только разработчику. Runtime, managed dependencies и native SQLite входят в EXE. При первом запуске .NET извлекает native-компоненты в свой кэш; размер EXE не равен потреблению RAM.

Из корня репозитория:

```powershell
.\artifacts\publish\AgentWatch.exe snapshot
.\artifacts\publish\AgentWatch.exe doctor
```

Для постоянного сбора выполните **в PowerShell, запущенном от администратора**:

```powershell
& 'C:\sarychev\Codex\AgentWatch\artifacts\publish\AgentWatch.exe' install
```

Установщик копирует EXE в `%ProgramFiles%\AgentWatch`, создаёт `%ProgramData%\AgentWatch`, сохраняет профиль и SID пользователя, регистрирует LocalSystem service с отложенным автозапуском и проверяет появление истории. При установке из другого административного аккаунта укажите `--profile` и `--reader-sid`; пример приведён в [OPERATIONS.md](docs/OPERATIONS.md).

Без повышения прав можно выполнить ограниченный прогон в собственном каталоге:

```powershell
.\artifacts\publish\AgentWatch.exe run --duration 6m --data-dir .\artifacts\manual
.\artifacts\publish\AgentWatch.exe report --since 24h --data-dir .\artifacts\manual
```

## Запросы

```powershell
.\artifacts\publish\AgentWatch.exe status
.\artifacts\publish\AgentWatch.exe doctor --json
.\artifacts\publish\AgentWatch.exe snapshot --json
.\artifacts\publish\AgentWatch.exe report --since 24h
.\artifacts\publish\AgentWatch.exe report --since 7d --json
.\artifacts\publish\AgentWatch.exe report --since 30d
.\artifacts\publish\AgentWatch.exe agents --since 7d
.\artifacts\publish\AgentWatch.exe disk --since 1h
.\artifacts\publish\AgentWatch.exe disk --since 7d
.\artifacts\publish\AgentWatch.exe anomalies --since 7d
.\artifacts\publish\AgentWatch.exe self --since 7d
.\artifacts\publish\AgentWatch.exe report --from 2026-09-01T00:00:00Z --to 2026-09-06T00:00:00Z
```

`report`, `agents`, `disk`, `anomalies`, `self` используют один query layer. В текстовом режиме выбирается нужный раздел; в JSON возвращается общий DTO `schemaVersion: 1` с контекстом периода и ограничениями измерений. При отсутствии истории запрос сообщает ошибку; `snapshot` работает независимо от SQLite. Все команды сбора и запросов принимают `--data-dir`.

## Что именно измеряется

| Слой | Источник | Значение |
|---|---|---|
| CPU и RAM | GetSystemTimes, GlobalMemoryStatusEx, GetPerformanceInfo | Нагрузка всей машины, физическая память, commit |
| Paging | Четыре английских PDH counters | Pages Input/Output — страницы; Page Reads/Writes — операции |
| Windows disk | PhysicalDisk(_Total) | Суммарный поток всех физических дисков Windows |
| Codex / Claude | GetProcessTimes, GetProcessIoCounters, process memory | Наблюдаемый логический I/O и ресурсы проверенного дерева процессов |
| NVMe | IOCTL_STORAGE_QUERY_PROPERTY, SMART log 02h | Lifetime host counters, дельты между снимками, температура и health |
| Файлы Codex | Только metadata | Размер и время изменения logs_2.sqlite, state_5.sqlite и их WAL |

**Логические записи процесса, поток физических дисков Windows и NVMe host writes — разные величины. NVMe host writes не являются NAND writes.** Сумма working sets может дважды учитывать общую память. Время присутствия процессов не доказывает активную работу человека. Короткие процессы между опросами и последняя запись завершившегося процесса могут быть пропущены.

## Режим хранения

- Быстрый опрос — 10 секунд; metadata файлов — 60 секунд; NVMe — 15 минут.
- Raw samples обрабатываются в RAM. Минутные агрегаты записываются одной транзакцией раз в 5 минут и при нормальной остановке.
- Последние 14 дней — минуты; до 180 дней — 15 минут; затем — сутки без автоматического удаления.
- Downsampling выполняется раз в сутки: сначала запись укрупнённых данных, затем удаление детальных в той же транзакции.
- SQLite WAL, synchronous NORMAL, один writer, read-only reports, без регулярного VACUUM. Пропуски наблюдения не превращаются в нулевую нагрузку.
- При недоступности диска буфер ограничен примерно шестью часами. После этого старые незаписанные партии отбрасываются с событием о потере покрытия.

Отчёты видят последнюю сохранённую партию, поэтому обычная задержка — до пяти минут. При аварийном завершении возможна потеря последней партии; при сбое питания — также ещё не синхронизированных данных WAL. Файлы повреждённой БД сохраняются для явного восстановления оператором.

## Настройка и удаление

Конфигурация: `%ProgramData%\AgentWatch\config.json`. Полный пример с начальными значениями — [config.example.json](config.example.json). Изменения применяются после перезапуска службы. Невалидные диапазоны заменяются безопасными начальными значениями с предупреждением; повреждённый JSON требует исправления.

```powershell
# Повышенный PowerShell; история сохраняется.
.\artifacts\publish\AgentWatch.exe uninstall
# Явное удаление собственной истории и конфигурации:
.\artifacts\publish\AgentWatch.exe uninstall --purge-data --confirm-purge
```

## Сборка и проверка

```powershell
# PowerShell 7, Windows 11 x64; SDK и caches только в .tools.
.\scripts\Bootstrap-Dotnet.ps1
.\scripts\Build.ps1
.\scripts\Verify-Runtime.ps1
.\scripts\Measure-Overhead.ps1 -DurationSeconds 720 -OutputDirectory .\artifacts\new-measurement
```

SDK закреплён в `global.json`; NuGet-граф и content hashes — в `packages.lock.json` каждого проекта. `Build.ps1` выполняет clean, locked restore, Release build, tests и self-contained single-file publish. Тестовый helper не входит в поставку. Сведения о native runtime и лицензиях — [DEPENDENCIES.md](docs/DEPENDENCIES.md).

## Документация

- [ARCHITECTURE.md](docs/ARCHITECTURE.md) — архитектура, решения и первичные источники.
- [DATA_MODEL.md](docs/DATA_MODEL.md) — единицы, схема, retention, точность и границы периодов.
- [SECURITY.md](docs/SECURITY.md) — данные, ACL и границы привилегий.
- [OPERATIONS.md](docs/OPERATIONS.md) — установка, эксплуатация, backup, recovery и обновление.
- [PERFORMANCE.md](docs/PERFORMANCE.md) — реальные измерения и границы выводов.
- [VALIDATION.md](docs/VALIDATION.md) — выполненные проверки и ещё не проверенные условия.
- [MCP_FUTURE.md](docs/MCP_FUTURE.md) — будущий adapter и token analytics; в v1 они не реализованы.

AgentWatch не оптимизирует Windows, не меняет Defender, pagefile, power plan или настройки агентов, не читает разговоры, не отправляет телеметрию и не открывает сетевой listener.
