# AgentWatch 0.2.1

Локальная служба наблюдения за Windows 11 x64 и CLI для разбора нагрузки Codex/Claude. Служба собирает CPU, RAM/commit, paging/disk, logical I/O деревьев агентов, metadata четырёх файлов Codex и NVMe health. Недельный отчёт связывает эти измерения по времени с сессиями, usage и явными отметками пользователя.

Отчёт разделяет **FACTS** и **INTERPRETATION**: выводы имеют confidence, evidence и альтернативные объяснения. Неизвестные поля остаются null, временное совпадение не объявляется причинной связью, а CPU/I/O не распределяется между параллельными сессиями без доказательств.

## Быстрый старт

Сборка создаёт уникальный каталог с автономным EXE, тестовыми результатами и SHA-256 manifest. Установленный .NET нужен только разработчику; runtime и native SQLite входят в EXE. Предыдущие сборки и логи не перезаписываются.

```powershell
.\scripts\Build.ps1
$build = Get-Content -LiteralPath .\artifacts\latest-build.json -Raw | ConvertFrom-Json
$aw = $build.File
& $aw version
& $aw status
```

Полный сценарий — [WEEKLY_TRIAL.md](docs/WEEKLY_TRIAL.md).

Для первой установки, если службы ещё нет, выполните в PowerShell **от имени администратора**:

```powershell
& $aw install
```

Если служба уже работает, её можно оставить собирать данные и читать старую историю новым CLI. Обновление выполняется отдельно, только когда готовы к перезапуску:

```powershell
.\scripts\Update-Service.ps1 -Plan  # Только чтение, без остановки.
# Позже, в повышенном PowerShell:
.\scripts\Update-Service.ps1
```

Скрипт обновления архивирует прежний EXE и согласованный комплект истории/config/logs, заменяет EXE, проверяет новый запуск и при ошибке пытается вернуть старый EXE. Во время разработки обновление установленной службы автоматически не выполняется.

## Единый анализ (0.2.1)

```powershell
$build = Get-Content -LiteralPath .\artifacts\latest-build.json -Raw | ConvertFrom-Json
$aw = $build.File
& $aw usage-sync --since 7d --json  # Повторить, если budgetReached=true.
& $aw analyze --from '2026-09-07T00:00:00+03:00' --to '2026-09-12T00:00:00+03:00' --timezone 'Arab Standard Time' --output .\artifacts\week-analysis
```

`analyze` создаёт новый каталог с `summary.md`, `weekly.md`, JSON общего отчёта и локальных дней, `history/agentwatch.db`, параметрами анализа и SHA-256 manifest. Без `--timezone` используется локальный пояс ОС, без границ — последние 7 дней. Максимальный период — 31 день. Для сегодняшнего отчёта задайте `--from` с локальной полуночи; без `--to` конец фиксируется при запуске.

Все ресурсные отчёты пакета читают один согласованный online backup SQLite, включающий committed WAL. Копия сохраняет **всю историю**, а не только запрошенный период; исходная БД не меняется. Snapshot ограничен 512 МиБ и выполняется только по запросу; операция backup может кратко задержать writer. При превышении бюджета используйте обычные `report`/`weekly`. Сохраняйте пакет в своём локальном каталоге, архивируйте или удаляйте его вручную.

Отметки, журналы запусков и уже импортированные usage metadata читаются отдельно и сохраняются в `weekly.json`; общей транзакции между этими источниками нет. `analyze` не запускает импорт и не сканирует transcripts. `manifest.json` со state=complete записывается последним; при ошибке остаётся `failed.json`. Существующий каталог не перезаписывается.

Локальные дни учитывают DST (23/25 часов); редкий пояс с несуществующей локальной полуночью отклоняется с предложением использовать UTC. Минутные/крупные бакеты включаются целиком: при пересечении полуночи они могут попасть в соседние дни, поэтому их сумма не всегда равна общему итогу. Отчёт явно сохраняет effective bounds и resolution; отсутствие наблюдений остаётся неизвестным.

Disk idle вне 0..100 теперь исключается без подмены значением 100. Для старой истории, где агрегат уже смешал хорошие и плохие значения, исключается весь idle-показатель этого бакета; остальные метрики сохраняются. `disk.idle` valid coverage и предупреждение показывают ограничение. Миграция или перезапись истории не требуются. Это защита качества данных, не доказательство причины выбросов PDH.

Подробнее: [QUALITY_RELEASE_0.2.1.md](docs/QUALITY_RELEASE_0.2.1.md).
## Полезные команды

```powershell
& $aw mark 'ноут начал тормозить'
& $aw mark 'задержка после запуска сборки' --at '2026-09-07T16:20:00+03:00'
& $aw usage-sync --since 7d --json
& $aw sessions --since 7d --json
& $aw weekly --since 7d
& $aw weekly --since 7d --json
& $aw weekly --since 7d --output .\artifacts\my-week.md
& $aw events --since 24h --json
& $aw runs --since 7d --json
& $aw report --since 24h
& $aw agents --since 7d
& $aw disk --since 7d
& $aw anomalies --since 7d
& $aw self --since 7d
& $aw snapshot --json
& $aw doctor --json
```

`weekly` включает все доступные метрики ±15 минут вокруг каждой включённой отметки, независимо от попадания в список выбранных эпизодов. Сессии показывают start/first observed/last activity/end, модель и effort из источника, input/cache/output/reasoning tokens с явной семантикой, неизвестную либо рассчитанную по заданному тарифу cost estimate. Claude reported-only output/reasoning отделены от final. Детали форматов и оценки стоимости — [SESSION_USAGE.md](docs/SESSION_USAGE.md).

`usage-sync` выполняется по запросу в отдельном CLI-процессе; служба не читает transcripts. По умолчанию импорт ограничен 64 МиБ и 100 изменённых файлов. При ограничении повторный вызов продолжает чтение с сохранённого offset. В пользовательское хранилище попадают только разрешённые поля metadata, без текстов сообщений, команд и tool results. Перед недельным отчётом повторите импорт.

Отметки находятся в `%LocalAppData%\AgentWatch\marks`, usage — в `%LocalAppData%\AgentWatch\usage`. Для них не нужны административные права. `--marks-dir` и `--usage-dir` позволяют выбрать отдельные каталоги. Основная история службы остаётся в `%ProgramData%\AgentWatch`; команды принимают `--data-dir` для другой истории. История, отсутствующая или недоступная в источниках, не считается нулевым использованием.

## Нагрузка и хранение

- Быстрый опрос — 10 секунд; metadata файлов — 60 секунд; NVMe — 15 минут. Частота существующих native collectors не изменена.
- Минутные агрегаты накапливаются в RAM и сохраняются одной транзакцией раз в 5 минут и при нормальной остановке. Отдельно хранится последний диагностический snapshot.
- Последние 14 дней — минуты; до 180 дней — 15 минут; затем — сутки без автоматического удаления. Downsampling выполняется атомарно раз в сутки.
- SQLite WAL/NORMAL, один writer, read-only reports, без регулярного VACUUM. Пропуски не превращаются в нулевую нагрузку. При недоступности диска буфер ограничен примерно шестью часами, затем потери покрытия фиксируются явно.
- Каждый запуск сборщика сохраняет `logs/runs/<run-id>/run.json` и последовательные сегменты diagnostic-*.log. Ротация не удаляет старые сегменты; архивирование журналов ручное. Сборки и проверочные запуски также используют новые каталоги.

Нормальная задержка отчёта — до пяти минут. При аварийном завершении возможна потеря незаписанной партии, при сбое питания — также ещё не синхронизированных данных WAL. Повреждённая история не заменяется пустой БД. Недельная стабильность и полезность выводов требуют реальной эксплуатации; короткий тест не заменяет сутки измерений.

## Проверки и документация

```powershell
.\scripts\Bootstrap-Dotnet.ps1   # При отсутствии SDK в .tools.
.\scripts\Build.ps1
.\scripts\Verify-Runtime.ps1
.\scripts\Measure-Overhead.ps1 -DurationSeconds 360
```

SDK и NuGet dependency graph закреплены, дополнительных зависимостей для usage/events не добавлено. `Build.ps1` выполняет locked restore, Release build с warnings-as-errors, тесты и self-contained single-file publish. Обновлённые validation scripts берут EXE из latest-build.json либо явного `-Executable` и управляют только созданными ими тестовыми процессами.

- [ARCHITECTURE.md](docs/ARCHITECTURE.md), [DATA_MODEL.md](docs/DATA_MODEL.md), [WEEKLY_TRIAL_DECISIONS.md](docs/WEEKLY_TRIAL_DECISIONS.md) — устройство, семантика и решения.
- [WEEKLY_TRIAL.md](docs/WEEKLY_TRIAL.md), [SESSION_USAGE.md](docs/SESSION_USAGE.md) — недельное испытание и новые контракты.
- [OPERATIONS.md](docs/OPERATIONS.md), [SECURITY.md](docs/SECURITY.md), [DEPENDENCIES.md](docs/DEPENDENCIES.md) — эксплуатация, границы доступа и зависимости.
- [VALIDATION_0.2.md](docs/VALIDATION_0.2.md), [PERFORMANCE_0.2.md](docs/PERFORMANCE_0.2.md) — проверки новой версии. Исходные измерения 0.1.0 сохранены отдельно.
- [MCP_FUTURE.md](docs/MCP_FUTURE.md) — будущий adapter; MCP сейчас не реализован.

AgentWatch не оптимизирует Windows, не меняет Defender/pagefile/power plan, не открывает сетевой listener и не отправляет телеметрию. Полная история команд, dashboard, GUI и автоматическая диагностика причин не реализованы.
