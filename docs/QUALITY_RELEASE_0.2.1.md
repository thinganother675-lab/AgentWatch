# AgentWatch 0.2.1 — качество данных и единый анализ

Согласованный первый этап: исправить использование disk idle >100 и заменить ручную серию отчётов воспроизводимым пакетом. История процессов вокруг давления на RAM, power-state attribution и MCP — отдельные следующие этапы, пока не реализованы. Новый сборщик, частота опросов и схема БД не добавлены.

## Решения

- PDH сохраняет uncapped чтение, чтобы не скрывать выбросы. Только idle проверяется на диапазон 0..100; throughput и очереди не ограничиваются 100. Invalid idle возвращает null и warning collector health; aggregator защищён также от некорректного входа.
- Старая история фильтруется при чтении до объединения бакетов. Восстановить валидную часть смешанного агрегата невозможно, поэтому исключается только idle целиком и публикуются потерянные metric-seconds. Read-only код не меняет источник; бинарные metric IDs и payload version не меняются.
- Новый analyze делает online backup SQLite и быстро освобождает источник, после чего тяжёлые отчёты читают только копию. Максимальный размер исходной БД по SQLite page_count*page_size — 512 МиБ. Backup API может блокировать writer на время копирования; он не вызывается из sampler или автоматически.
- Одно время snapshot, один captured WeeklyReport, дневные WatchReport по реальным локальным границам. Полная БД хранится в пакете. SHA-256 позволяет проверить исходный snapshot, JSON, настройки и Markdown. Повторный запуск против изменившегося источника — новый пакет; сохранённый пакет воспроизводит уже зафиксированные данные.
- Marks/usage/runs читаются один раз отдельно; их проекции закреплены в weekly.json, но это не общий атомарный снимок четырёх хранилищ. Запрошенный конец периода и время snapshot показаны раздельно.
- Дни с coarse buckets на границах не делятся пропорционально: такое деление выдумало бы данные. Оговорка о возможном повторном включении бакетов сохраняется.

## Проверки

Regression suite: диапазон idle и сохранность throughput; фильтрация старого выброса, потеря покрытия и неизменность источника; backup committed WAL и стабильность после дальнейших записей; границы UTC+3 и 23/25-часовые DST дни; hashes, согласование минутных дней, запрет перезаписи; ошибки до создания каталога и отсутствие complete manifest при сбое.

Полный Build.ps1 выполняет locked restore, warnings-as-errors, unit/integration tests и single-file publish. Live-history acceptance и runtime evidence записываются отдельно в artifacts; не подменяют длительный overhead/leak test. Установленная служба обновляется отдельным Update-Service.ps1.

## Источники

- https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcountervalue — NOCAP100 и чтение rate counters.
- https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup — online backup, ограничения блокировки writer.

Повторное чтение того же снимка стандартной командой:

```powershell
& $aw report --data-dir .\artifacts\week-analysis\history --from '2026-09-07T00:00:00+03:00' --to '2026-09-12T00:00:00+03:00' --json
```

Используйте точные границы из manifest и ту же версию CLI. Settings для метрик включены в history/config.json; schema остаётся 1. Для session/marker/run context используйте сохранённый weekly.json: повторный weekly против каталога history сам по себе не воспроизводит внешние источники.

## Проверено 2026-09-11

- Итоговая сборка: artifacts/releases/0.2.1-20260911-135600-a4f8553d; SHA-256 A2E14F50C292F560A878CCADA0819292B1FB5FA1B16DAA0CC25B65A864360BE6. 32 unit + 53 integration tests passed; warnings/errors 0. Новых regression tests: 6.
- Штатный Verify-Runtime.ps1: isolated crash/restart passed, повреждённая БД сохранена при отказе старта, восемь CLI checks passed. SCM lifecycle этим тестом не проверяется.
- Реальная недельная история: artifacts/quality-release-acceptance-20260911-135516/final-week. Online backup занял 0,147 с. JSON report из snapshot воспроизводится точно; все SHA-256 manifest совпали.
- Старый/новый CLI на одном snapshot: idle max 160,7029 → 99,99996%; исключено 61746,348 metric-seconds старых смешанных idle-бакетов. CPU/memory/agents/self/coverage/daily/correlations и физическая запись не изменились. Источник не переписывался.
- Установленная служба осталась Running с PID 1424, версия 0.2.0 не обновлялась. Новый CLI 0.2.1 уже читает её историю с фильтрацией; фильтрация новых замеров самим сервисом начнёт работать после отдельного обновления службы.
- Фиксированный конец календарного периода не означает тот же snapshot: новый пакет включил позднее сохранённые данные граничного бакета. Сохранённый snapshot даёт воспроизводимый срез; manifest различает requested period и capture time.

## Windows PowerShell 5.1 updater compatibility — 2026-09-11

User's elevated update failed before binary replacement because .NET Framework lacks File.Move(source,destination,overwrite). Old 0.2.0 restarted successfully. Updated both forward replacement and rollback to File.Replace(source,destination,[NullString]::Value). Plain PowerShell $null binds as an empty string here and fails path validation; NullString supplies a true .NET null. Isolated replacement/rollback passed on Windows PowerShell 5.1.26100.9444 and PowerShell 7.6.5; updater -Plan passed in 5.1. Actual service upgrade still requires rerunning the corrected script from elevated PowerShell. Regression script: scripts/Test-UpdateFileCompatibility.ps1.
