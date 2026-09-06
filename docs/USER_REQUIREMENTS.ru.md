# РОЛЬ

Ты — principal-level Windows systems engineer, .NET engineer, performance/observability engineer и pragmatic software architect.

Твоя задача — самостоятельно спроектировать, реализовать, протестировать и подготовить к постоянной эксплуатации локальный Windows-сервис **AgentWatch**.

Это не demo и не учебный проект. Это небольшой production-quality инструмент, который будет месяцами работать на основном ноутбуке разработчика.

Главные свойства продукта:

1. Очень маленький overhead.
2. Полностью локальная работа.
3. Никакой облачной телеметрии.
4. Никакого GUI в v1.
5. Один основной executable.
6. Автоматический запуск вместе с Windows.
7. Долговременное накопление истории.
8. Возможность через неделю/месяц получить нормальный аналитический отчёт.
9. Архитектура должна позволить затем добавить MCP без переделки ядра.
10. Сам монитор НЕ должен становиться причиной лишних записей на SSD.

Работай автономно. Не спрашивай подтверждение по мелким техническим решениям. Если существует несколько разумных реализаций — исследуй, выбери наиболее надёжную и лёгкую, зафиксируй решение в документации и продолжай.

---

# РАБОЧАЯ ДИРЕКТОРИЯ

Вся разработка происходит здесь:

```text
C:\sarychev\Codex\AgentWatch
```

В начале обязательно:

```powershell
Set-Location 'C:\sarychev\Codex\AgentWatch'
```

Если директория пустая — создай проект здесь.

Не разбрасывай исходники по другим директориям.

Runtime-файлы после установки допускается хранить в стандартных Windows locations:

```text
C:\Program Files\AgentWatch\
C:\ProgramData\AgentWatch\
```

но исходный код, solution, tests, docs и build artifacts должны находиться под:

```text
C:\sarychev\Codex\AgentWatch
```

---

# ДОСТУП К ИНТЕРНЕТУ И ЗАВИСИМОСТЯМ

Ты МОЖЕШЬ:

* пользоваться интернетом;
* читать официальную Microsoft/.NET документацию;
* читать документацию Windows Win32 API;
* исследовать GitHub;
* скачивать NuGet-пакеты;
* устанавливать project-local development dependencies;
* скачивать инструменты, необходимые именно для тестирования;
* устанавливать .NET SDK 10 LTS, если подходящего SDK нет;
* использовать PowerShell;
* писать временные тестовые программы;
* запускать unit/integration tests;
* использовать Git локально.

Предпочитай:

1. официальную документацию Microsoft;
2. официальную документацию .NET;
3. Windows Win32 APIs;
4. официальные Microsoft NuGet packages;
5. хорошо поддерживаемые open-source packages только там, где они действительно сокращают риск или объём собственного кода.

Не добавляй dependency просто ради удобства.

Для каждого внешнего runtime dependency оцени:

* нужен ли он вообще;
* maintenance status;
* license;
* runtime overhead;
* allocations;
* disk/network activity;
* возможность убрать его.

Финальный AgentWatch не должен требовать:

* Node.js;
* Python;
* WSL;
* Docker;
* CrystalDiskInfo;
* HWiNFO;
* Process Explorer;
* Process Monitor;
* Process Lasso;
* smartmontools;
* ccusage;
* внешнюю БД;
* браузер;
* Electron;
* отдельный daemon.

Дополнительные инструменты разрешены **только в процессе разработки/валидации**.

---

# ЦЕЛЕВАЯ ПЛАТФОРМА

Работай только с:

```text
Windows 11 Pro x64
native Windows
```

WSL2 в архитектуре не использовать.

Программа не должна зависеть от WSL.

Основной target:

```text
.NET 10 LTS
win-x64
```

На момент проектирования .NET 10 является актуальным LTS. Используй актуальный стабильный patch .NET 10, доступный через официальный SDK.

Предпочтительная модель:

```text
.NET Worker Service
+
Windows Service
+
Win32 APIs
+
Microsoft.Data.Sqlite
```

Финальный publish желательно сделать:

```text
self-contained
single-file
win-x64
```

Но сначала добейся корректности.

Не включай aggressive trimming/AOT только ради красивого размера бинарника, если это повышает риск runtime-проблем. После измерения можно оценить необходимость.

---

# КОНТЕКСТ ПОЛЬЗОВАТЕЛЯ

Пользователь — AI-разработчик.

Он работает много часов ежедневно.

Типичный режим:

```text
3–4 параллельных Codex sessions
+
Claude Code
+
много Node процессов
+
браузер / dev tooling
```

По субъективному распределению использования:

```text
Codex  ≈ 90%
Claude ≈ 10%
```

Объём AI-работы очень высокий.

Недавний `ccusage` показывал примерно:

```text
Codex total tokens       ≈ 3.127B
Codex cache read         ≈ 3.014B
Codex ordinary input     ≈ 99M
Codex output             ≈ 14M
```

Это контекст для понимания интенсивности использования.

AgentWatch v1 НЕ обязан постоянно считать токены.

Не запускай `npx ccusage` в фоне.

Token analytics будет отдельным более поздним collector или on-demand интеграцией.

---

# ИЗВЕСТНЫЙ HARDWARE BASELINE

Машина:

```text
Laptop: HONOR GDG-X

Windows:
Microsoft Windows 11 Pro 25H2 x64
Build 26200.9168

CPU:
Intel Core i3-1315U
6 physical cores
8 logical processors

RAM:
8 GiB LPDDR4
4266 MT/s

GPU:
Intel UHD Graphics
integrated

Storage:
YMTC PC411-512GB-B
512.1 GB
NVMe 1.4
PCIe 4.0 x4
Firmware YM3603BX

System volume:
C:
NTFS
~475.8 GiB usable
~335 GiB free at baseline
```

Это машина с небольшим объёмом RAM, поэтому overhead программы особенно важен.

---

# NVME / SSD BASELINE

Baseline был снят CrystalDiskInfo 9.9.2:

```text
Date:
2026-09-04 ~16:50

Model:
YMTC PC411-512GB-B

Temperature:
49 °C

Health:
100%

Critical Warning:
0

Available Spare:
100%

Available Spare Threshold:
10%

Percentage Used:
0%

Power On Hours:
281

Power Cycles:
175

Unsafe Shutdowns:
50

Media/Data Integrity Errors:
0

Error Information Log Entries:
0
```

NVMe raw Data Units Written:

```text
0xAAB764
```

Decimal:

```text
11,188,068 data units
```

По NVMe semantics:

```text
1 data unit = 1000 × 512 bytes
```

Следовательно baseline lifetime host writes:

```text
11,188,068 × 512,000
=
5,728,290,816,000 bytes
≈
5.728 TB decimal
```

CrystalDiskInfo отображал:

```text
Host Writes: 5334 GB
```

Используй raw NVMe value как первичный источник для собственных вычислений AgentWatch.

NVMe Data Units Read baseline:

```text
0x1878E3F
```

Decimal:

```text
25,660,991
```

Это примерно:

```text
13.138 TB
```

Не путай:

```text
per-process logical writes
```

и:

```text
physical/lifetime NVMe host writes
```

Это принципиально разные метрики.

AgentWatch ОБЯЗАН явно сохранять это различие во всех моделях данных, названиях и отчётах.

---

# ИЗВЕСТНЫЙ MEMORY BASELINE

Во время предыдущего snapshot:

```text
RAM total:
~8 GiB

RAM available:
примерно 147–290 MiB

Committed:
~18.98 GB / 26.61 GB

pagefile:
~17.5 GB allocated

pagefile current usage:
~3 GB

pagefile observed peak:
~6.8 GB

Pages/sec snapshot:
~4445
```

Следовательно одна из главных задач AgentWatch — понять:

```text
действительно ли недостаток 8 GB RAM
создаёт постоянный paging
и дополнительную нагрузку на SSD.
```

Не делай вывод только по `Pages/sec`.

Особенно интересуют:

```text
Memory\Pages Input/sec
Memory\Pages Output/sec
Memory\Available MBytes
commit usage
```

Pages Output/sec особенно важен для понимания реального вытеснения памяти в pagefile.

---

# ИЗВЕСТНЫЙ PROCESS BASELINE

Один предыдущий snapshot показывал:

```text
Codex:
4 processes

Claude:
13 processes

Node:
69 processes
```

Пример cumulative process I/O основного Codex процесса:

```text
codex.exe
Read  ≈ 4940 MB
Write ≈ 1584 MB
```

Это cumulative Windows process I/O с момента старта процесса.

Это НЕ означает, что SSD физически получил ровно 1584 MB NAND writes.

AgentWatch должен использовать process I/O как attribution metric:

```text
"кто логически инициировал много I/O"
```

а NVMe Data Units Written — как:

```text
"сколько host writes фактически получил накопитель".
```

---

# CODEX LOCAL STORAGE BASELINE

Предыдущий metadata-only audit:

```text
%USERPROFILE%\.codex
≈ 5.30 GB total

logs_2.sqlite
≈ 800.9 MB

logs_2.sqlite-wal
≈ 5.45 MB

state_5.sqlite
≈ 26.3 MB

state_5.sqlite-wal
≈ 4.34 MB
```

`.claude`:

```text
≈ 98 MB
```

AgentWatch НЕ должен читать содержимое пользовательских разговоров.

Для этих файлов нас интересуют преимущественно:

```text
exists
file size
size delta
last write timestamp
```

Никакого чтения SQLite/JSONL разговоров в v1.

---

# ОСНОВНАЯ ПРОБЛЕМА

Пользователь хочет понять:

```text
Почему ноутбук начинает сильно шуметь,
когда несколько AI coding agents работают часами?
```

Возможные причины:

```text
CPU
RAM pressure
paging/pagefile
Codex logging
Claude
Node/build/test processes
disk I/O
Defender/indexing
комбинация факторов
```

AgentWatch не должен заранее утверждать, какая из причин основная.

Он должен собрать данные, позволяющие доказать причину.

---

# USER EXPERIENCE

Желаемый UX:

Пользователь один раз устанавливает AgentWatch.

После этого забывает о нём.

AgentWatch автоматически работает в фоне при запуске Windows.

Нет постоянно открытого окна.

Нет tray icon в v1.

Нет браузерного dashboard.

Нет localhost HTTP server.

Нет облака.

Нет push telemetry.

Нет рекламы/analytics/crash-reporting наружу.

Через неделю пользователь должен иметь возможность выполнить что-то вроде:

```powershell
AgentWatch.exe report --since 7d
```

и получить полезный ответ.

В будущем Codex будет получать те же данные через MCP.

---

# ГЛАВНАЯ АРХИТЕКТУРНАЯ ИДЕЯ

Один executable должен иметь несколько режимов:

```text
AgentWatch.exe service
AgentWatch.exe install
AgentWatch.exe uninstall
AgentWatch.exe status
AgentWatch.exe doctor
AgentWatch.exe snapshot
AgentWatch.exe report --since 24h
AgentWatch.exe report --since 7d
AgentWatch.exe report --since 7d --json
AgentWatch.exe anomalies --since 7d
AgentWatch.exe disk --since 7d
AgentWatch.exe agents --since 7d
AgentWatch.exe self --since 7d
```

В будущем:

```text
AgentWatch.exe mcp
```

Но MCP сейчас НЕ реализовывать.

Сейчас только подготовь внутренние interfaces/query layer таким образом, чтобы `mcp` затем был тонкой оболочкой вокруг уже существующего query API.

---

# V1 SCOPE

v1 должен включать:

```text
Windows Service
process monitoring
Codex/Claude attribution
system CPU
memory
paging
disk activity
NVMe SMART
hot Codex DB file sizes
local SQLite history
retention/downsampling
anomaly detection
CLI reports
self-monitoring
installation/uninstallation
tests
documentation
```

---

# НЕ ВХОДИТ В V1

Не реализовывать сейчас:

```text
MCP
GUI
tray application
web UI
HTTP API
remote access
cloud sync
token billing analytics
prometheus server
Grafana
OpenTelemetry exporter
email notifications
Telegram notifications
automatic process throttling
automatic CPU affinity
automatic Efficiency Mode
automatic Defender exclusions
automatic power-plan changes
automatic cleanup of .codex
automatic deletion of logs
automatic SQLite VACUUM чужих баз
```

Сначала только measurement.

Никакой автоматической «оптимизации» Codex/Claude.

---

# SAFETY / НЕ ТРОГАТЬ WINDOWS

AgentWatch и процесс разработки НЕ должны автоматически:

* менять Defender exclusions;
* отключать Defender;
* менять Windows Search;
* менять VBS;
* менять Memory Integrity;
* менять Power Plan;
* менять process priority Codex/Claude;
* менять CPU affinity;
* удалять `.codex`;
* удалять `.claude`;
* удалять Codex SQLite;
* делать VACUUM Codex databases;
* менять Codex configuration;
* менять Claude configuration;
* менять pagefile;
* отключать службы Windows;
* отключать обновления Windows;
* менять BIOS;
* делать disk benchmark;
* делать SSD stress test;
* делать длительный filesystem benchmark.

Если для диагностики хочется что-то подобное — НЕ делай.

---

# PRIVACY

AgentWatch предназначен для локальной observability.

По умолчанию НЕ сохранять:

```text
command line arguments
environment variables
prompt text
conversation content
JSONL content
SQLite content Codex/Claude
file content
API keys
tokens/secrets
network payloads
browser history
clipboard
source code
git diffs
```

Можно хранить:

```text
process name
PID
parent PID
process creation time
agent classification
CPU counters
memory counters
I/O counters
known monitoring file names
known file sizes
timestamps
system metrics
NVMe health values
```

Не сохраняй серийный номер SSD.

Не сохраняй Windows product key.

Не добавляй никакой runtime network activity.

После установки программа должна нормально работать вообще без интернета.

---

# СТРУКТУРА ПРОЕКТА

Не переусложняй solution.

Предпочтительно начать примерно так:

```text
C:\sarychev\Codex\AgentWatch
│
├── AgentWatch.sln
│
├── src\
│   └── AgentWatch\
│       ├── AgentWatch.csproj
│       └── ...
│
├── tests\
│   ├── AgentWatch.Tests\
│   └── AgentWatch.IntegrationTests\
│
├── docs\
│   ├── ARCHITECTURE.md
│   ├── DATA_MODEL.md
│   ├── OPERATIONS.md
│   ├── PERFORMANCE.md
│   ├── SECURITY.md
│   └── MCP_FUTURE.md
│
├── scripts\
│
├── artifacts\
│
├── README.md
├── .gitignore
└── ...
```

Если несколько production class-library projects реально улучшают separation — можно использовать.

Но не делай 12 проектов ради «clean architecture».

Цель — простой поддерживаемый инструмент.

---

# RECOMMENDED INTERNAL MODULES

Логически отдели:

```text
Hosting
Configuration

Collectors
    SystemCpuCollector
    MemoryCollector
    PagingCollector
    DiskActivityCollector
    ProcessCollector
    NvmeHealthCollector
    HotFileCollector
    SelfCollector

Attribution
    ProcessTreeTracker
    AgentClassifier

Aggregation
    SampleAggregator

Storage
    AgentWatchDatabase
    RetentionManager

Analysis
    AnomalyDetector
    ReportService

CLI
    Commands

Windows
    NativeMethods
    SafeHandles
```

Не обязательно именно такие filenames/classes, но separation должна быть примерно такой.

---

# WINDOWS SERVICE

Используй современный .NET Worker Service / `BackgroundService`.

Service name:

```text
AgentWatch
```

Display name:

```text
AgentWatch AI Development Monitor
```

Description:

```text
Low-overhead local observability service for AI development workloads.
```

Настрой:

```text
Automatic (Delayed Start)
```

Предпочтительно delayed start, чтобы AgentWatch не конкурировал с загрузкой Windows.

Recovery:

```text
first failure  -> restart
second failure -> restart
```

с разумной задержкой, например 60 секунд.

Не делай бесконечный crash loop.

Если collector ломается, предпочтительно отключить конкретный collector и продолжить работу, а не падать всем service.

---

# SERVICE ACCOUNT

Исследуй наиболее практичный вариант.

Для NVMe SMART может понадобиться привилегированный доступ.

Предпочтительный кандидат:

```text
LocalSystem
```

если это действительно необходимо для `\\.\PhysicalDrive0` / NVMe query.

Но учитывай безопасность.

Service не должен:

```text
открывать network listener
исполнять произвольные команды
принимать команды от сети
```

CLI report должен уметь читать историю без administrative privileges.

Runtime data:

```text
C:\ProgramData\AgentWatch\
```

Например:

```text
C:\ProgramData\AgentWatch\agentwatch.db
C:\ProgramData\AgentWatch\config.json
```

Настрой ACL так, чтобы:

```text
SYSTEM       full control
Administrators full control
installing/current user read
```

Избегай `Everyone: Full Control`.

---

# INSTALL / UNINSTALL

Реализуй:

```powershell
AgentWatch.exe install
```

и:

```powershell
AgentWatch.exe uninstall
```

`install` должен:

1. проверить elevation;
2. создать Program Files / ProgramData directories;
3. скопировать published binary в:

```text
C:\Program Files\AgentWatch\AgentWatch.exe
```

4. создать Windows Service;
5. настроить delayed automatic startup;
6. настроить recovery;
7. создать data directory;
8. применить нормальные ACL;
9. запустить service;
10. выполнить health check.

Если elevation отсутствует:

* не пытайся обходить UAC;
* выдай понятную инструкцию запустить elevated PowerShell.

`uninstall`:

* остановить service;
* удалить service;
* удалить installed executable.

По умолчанию НЕ удалять историю.

Для истории отдельный explicit вариант:

```text
AgentWatch.exe uninstall --purge-data
```

с дополнительным явным действием пользователя.

---

# CONFIGURATION

Используй конфигурацию с разумными defaults.

Пример смысловой модели:

```json
{
  "sampling": {
    "fastSampleSeconds": 10,
    "persistBatchMinutes": 5,
    "hotFilesSeconds": 60,
    "nvmeHealthMinutes": 15,
    "staticSystemHours": 24
  },
  "retention": {
    "minuteDays": 14,
    "fifteenMinuteDays": 180,
    "dailyDays": 0
  }
}
```

`dailyDays = 0` можно трактовать как indefinite.

Конкретный JSON можешь улучшить.

Если config отсутствует — использовать defaults.

Не требовать config для нормального запуска.

Ошибочное поле config не должно убивать весь service — используй validation + safe fallback или понятную startup error в случае фундаментальной ошибки.

---

# FAST SAMPLING

Основной интервал:

```text
10 seconds
```

Это default, а не жестко прошитая константа.

Каждые ~10 секунд собирай максимально дешёвые показатели.

Не используй для этого рекурсивный WMI/CIM scan всей системы.

---

# SYSTEM CPU

Предпочитай direct Windows API.

Например исследуй:

```text
GetSystemTimes
```

CPU рассчитывай по delta между samples.

Нужны как минимум:

```text
total CPU %
user CPU %
kernel/privileged CPU %
```

Корректно учитывай, что kernel time semantics Windows включают idle в соответствующем counter.

Используй monotonic duration.

Не основывай вычисления на wall-clock, который пользователь может изменить.

---

# SYSTEM MEMORY

Получай:

```text
physical total
physical available
physical used
memory load %
```

Исследуй direct APIs:

```text
GlobalMemoryStatusEx
GetPerformanceInfo
```

Нужно также:

```text
commit total
commit limit
commit %
```

Если значения требуют преобразования из pages:

```text
bytes = pages × system page size
```

Page size получай из системы, не hard-code 4096 для всех расчётов.

---

# PAGING

Нужно обязательно различать:

```text
Pages Input/sec
Pages Output/sec
Page Reads/sec
Page Writes/sec
```

Для Windows Performance Counters предпочтительно исследуй PDH API.

Поскольку Windows пользователя русская, не используй локализованные display names напрямую.

Предпочитай:

```text
PdhAddEnglishCounterW
```

чтобы counter paths были независимы от языка Windows.

Пример интересующих counter paths:

```text
\Memory\Pages Input/sec
\Memory\Pages Output/sec
\Memory\Page Reads/sec
\Memory\Page Writes/sec
```

Можно также использовать:

```text
\Memory\Available MBytes
\Memory\% Committed Bytes In Use
```

но если эти значения уже надёжнее/дешевле получены direct Win32 APIs — не дублируй без причины.

PDH query должен быть создан один раз и переиспользоваться.

Не открывай/закрывай PDH query на каждый sample.

Если PDH counter недоступен — service продолжает работу с degraded capability.

---

# DISK ACTIVITY

Нужно видеть текущую активность системного диска:

```text
read bytes/sec
write bytes/sec
queue/activity metric
```

На машине сейчас один основной physical SSD, поэтому `_Total` приемлем как fallback.

Можно использовать PDH physical disk counters.

Если instance naming нестабилен:

* реализуй discovery;
* либо надежно используй `_Total`.

Не превращай это в сложный disk enumerator, если пользы нет.

Важно:

```text
Windows disk throughput
!=
NVMe Data Units Written delta
!=
process WriteTransferCount
```

В коде и документации эти три уровня должны называться различно.

---

# PROCESS DISCOVERY

Нам нужны AI-related process trees, а не каждый процесс Windows в базе.

Нужны roots:

```text
codex.exe
claude.exe
```

Известный дополнительный Codex process:

```text
codex-code-mode-host.exe
```

Поддержи extensible enum:

```text
AgentKind.Codex
AgentKind.Claude
AgentKind.Other
```

но `Other` сейчас можно не собирать детально.

Не классифицируй все `node.exe` как Codex или Claude.

Node должен быть attributed агенту только если является descendant соответствующего agent process tree.

---

# PROCESS TREE

Для определения parent-child relationships предпочитай лёгкий native snapshot.

Исследуй:

```text
CreateToolhelp32Snapshot
PROCESSENTRY32
```

или другой более подходящий Win32 API.

Не делай каждые 10 секунд:

```text
Get-CimInstance Win32_Process
```

для всей системы, если native process snapshot дешевле.

Отслеживай process identity как минимум через:

```text
PID
+
creation time
```

Потому что PID переиспользуются.

Никогда не считай один и тот же PID тем же процессом только потому, что число PID совпало.

---

# PROCESS METRICS

Для интересующих processes собирай:

```text
CPU time
working set
private memory
read operation count
write operation count
other operation count
read transfer bytes
write transfer bytes
other transfer bytes
```

Используй direct Win32 process APIs там, где это разумно.

Особенно:

```text
GetProcessIoCounters
```

`WriteTransferCount` должен называться в модели примерно:

```text
LogicalProcessWriteBytes
```

а не:

```text
DiskWrites
```

чтобы будущий пользователь/MCP не перепутал это с физической записью SSD.

---

# PROCESS DELTA

Counters cumulative.

Для каждого process identity:

```text
delta =
current cumulative value
-
previous cumulative value
```

Учитывай:

```text
новый процесс
завершившийся процесс
PID reuse
counter reset
AccessDenied
race: process exited during sample
```

При:

```text
current < previous
```

не создавай огромный отрицательный/overflow delta.

Считай это reset/invalid transition.

---

# PROCESS ATTRIBUTION

Желаемый результат:

```text
Codex
  root A
    codex.exe
    child node.exe
    child test runner
    child ...
  root B
    codex.exe
    ...

Claude
  root A
    claude.exe
    child node.exe
    ...
```

Minute aggregates можно хранить на уровне:

```text
AgentKind.Codex
AgentKind.Claude
```

Дополнительно можно хранить per-session/root aggregates, если это не раздувает БД и существенно помогает.

Не нужно сохранять 69 отдельных node rows каждую минуту, если они могут быть безопасно агрегированы.

---

# IMPORTANT LIMITATION OF POLLING

Polling может не увидеть очень короткоживущий process между двумя samples.

Не пытайся ради v1 решить это постоянным kernel ETW trace.

Документируй limitation.

В будущем можно добавить optional burst diagnostics.

v1 должен быть low-overhead first.

---

# NVME SMART

Это важная часть.

Не используй CrystalDiskInfo как runtime dependency.

На Windows 11 исследуй прямой native путь:

```text
CreateFile("\\.\PhysicalDrive0")
DeviceIoControl
IOCTL_STORAGE_QUERY_PROPERTY
StorageDeviceProtocolSpecificProperty
ProtocolTypeNVMe
NVMeDataTypeLogPage
SMART / Health Information Log
```

Используй Windows documented:

```text
NVME_HEALTH_INFO_LOG
```

Нас интересуют:

```text
CriticalWarning
Temperature
AvailableSpare
AvailableSpareThreshold
PercentageUsed
DataUnitRead
DataUnitWritten
HostReadCommands
HostWrittenCommands
ControllerBusyTime
PowerCycle
PowerOnHours
UnsafeShutdowns
MediaErrors
ErrorInfoLogEntryCount
WarningCompositeTemperatureTime
CriticalCompositeTemperatureTime
TemperatureSensor1...
```

Не обязательно отображать всё пользователю, но parser должен корректно понимать основные поля.

---

# NVME UINT128

Поля вроде:

```text
DataUnitWritten[16]
```

являются 128-bit little-endian values.

.NET 10 имеет подходящие numeric primitives.

Корректно парси весь 128-bit диапазон.

Не обрезай его до UInt64 до проверки.

Для текущего диска значение помещается в UInt64, но parser должен быть правильным по формату.

Unit test ОБЯЗАТЕЛЕН.

Golden test:

```text
raw DataUnitWritten:
0x00000000000000000000000000AAB764
```

должен дать:

```text
11,188,068
```

и:

```text
5,728,290,816,000 bytes
```

при:

```text
dataUnits × 1000 × 512
```

---

# NVME TEMPERATURE

SMART composite temperature хранится в Kelvin.

Baseline:

```text
0x0142
=
322 K
=
~49 °C
```

Напиши unit test.

Не допускай типичную ошибку:

```text
322 °C
```

---

# NVME POLLING RATE

Default:

```text
15 minutes
```

SMART не нужно читать каждые 10 секунд.

Если выяснится, что один native SMART query чрезвычайно дешёвый и нужен более быстрый thermal alert, архитектура должна позволять снизить interval.

Но default v1:

```text
15 min
```

достаточен для health/endurance history.

Опционально можно сделать adaptive mode:

```text
если SSD temperature >= 60°C
temporarily poll every 60 sec
until temperature < 58°C
```

Но только если реализация остаётся простой.

Не обязательно для v1.

---

# NVME STORED METRICS

Каждый SMART sample сохраняет:

```text
timestamp
physical drive number
temperatureC
criticalWarning
availableSparePct
availableSpareThresholdPct
percentageUsed
dataUnitsRead
dataUnitsWritten
hostReadBytesLifetime
hostWriteBytesLifetime
powerOnHours
powerCycles
unsafeShutdowns
mediaErrors
errorInfoLogEntries
```

При возможности:

```text
controllerBusyMinutes
```

Lifetime values нужны прежде всего для delta.

Например:

```text
physical host writes during 7 days =
latest hostWriteBytesLifetime
-
first hostWriteBytesLifetime
```

---

# HOT CODEX FILES

Следи metadata-only за:

```text
%USERPROFILE%\.codex\logs_2.sqlite
%USERPROFILE%\.codex\logs_2.sqlite-wal
%USERPROFILE%\.codex\state_5.sqlite
%USERPROFILE%\.codex\state_5.sqlite-wal
```

Default interval:

```text
60 seconds
```

Для каждого:

```text
exists
size bytes
last write timestamp if cheap
delta size
```

Никакого открытия SQLite.

Никакого чтения contents.

Никакого lock.

Никакого VACUUM.

Никакого checkpoint чужой БД.

---

# USER PROFILE DISCOVERY

Service LocalSystem имеет другой `%USERPROFILE%`.

Поэтому нельзя использовать:

```text
Environment.GetFolderPath(...)
```

и автоматически получить systemprofile вместо пользователя.

Сделай нормальный mechanism.

На install можно сохранить monitored user profile:

```text
C:\Users\User
```

в config.

Лучше получить SID/profile пользователя, который выполняет installation.

Нужна возможность config override:

```text
monitoredUserProfile
```

Не hard-code `User` навсегда.

---

# RECURSIVE .CODEX SIZE

НЕ считай размер всей `.codex` каждые 10 секунд или каждую минуту.

Допустимо:

```text
раз в 24 часа
```

или вообще только on-demand.

Если реализуешь daily metadata scan:

* cancellable;
* low priority;
* не follow symlinks/reparse points;
* не открывать содержимое файлов;
* иметь timeout;
* не мешать основной sampling loop.

Это secondary feature.

Hot four files важнее.

---

# SELF-MONITORING

Это критически важно.

AgentWatch должен следить за САМИМ СОБОЙ.

Нужно измерять:

```text
AgentWatch CPU
AgentWatch working set
AgentWatch private bytes
AgentWatch logical read bytes
AgentWatch logical write bytes
sampling duration
DB flush duration
collector errors
missed samples
```

Мы должны иметь возможность доказать:

```text
монитор не является причиной проблемы.
```

Self metrics не нужно писать каждые 10 секунд отдельной строкой.

Агрегируй.

---

# IN-MEMORY SAMPLING

Главный принцип:

```text
10-second raw samples
          ↓
       RAM only
          ↓
1-minute aggregates
          ↓
       RAM queue
          ↓
one SQLite batch every 5 minutes
```

То есть НЕ делать SQLite transaction каждые 10 секунд.

Default persist interval:

```text
5 minutes
```

При graceful service shutdown:

```text
flush pending aggregates
```

При crash допустима потеря нескольких минут мониторинга.

Это лучше, чем лишняя постоянная write amplification.

---

# MINUTE AGGREGATES

Для system minute aggregate желательно хранить:

```text
timestamp minute UTC

sample count

CPU:
avg
max

RAM:
available MB avg
available MB min
commit % avg
commit % max

Paging:
pages input/sec avg/max
pages output/sec avg/max
page reads/sec avg/max
page writes/sec avg/max

estimated page-in bytes
estimated page-out bytes

Disk:
read bytes
write bytes
average queue/activity if available
```

---

# AGENT MINUTE AGGREGATES

Для каждого:

```text
Codex
Claude
```

хранить:

```text
timestamp
agent kind

active seconds
process count avg
process count max

CPU avg
CPU max

working set avg
working set max

private memory avg
private memory max

logical read bytes during minute
logical write bytes during minute
logical other bytes during minute

read operations
write operations

process starts
process exits
```

Не нужно сохранять каждый 10-second process row.

---

# SQLITE

Используй:

```text
Microsoft.Data.Sqlite
```

Не используй EF Core без веской причины.

Для такого проекта raw SQL проще и легче.

Database:

```text
C:\ProgramData\AgentWatch\agentwatch.db
```

Используй schema migrations/versioning.

Например:

```text
schema_version
```

или migrations table.

---

# SQLITE PRAGMAS

Исследуй и выбери безопасные значения.

Кандидат:

```text
journal_mode=WAL
synchronous=NORMAL
busy_timeout
```

Не используй:

```text
synchronous=OFF
```

Не делай регулярный:

```text
VACUUM
```

Потому что цель проекта — снижать write amplification.

Не запускай database optimize operations без измеренной причины.

Prepared commands/statements и batch transaction предпочтительнее множества одиночных insert.

---

# DATABASE TABLES

Конкретную schema можешь улучшить, но логически нужны:

```text
meta

system_minute
agent_minute
hot_file_sample
nvme_health
anomaly
self_minute

system_15m
agent_15m

system_daily
agent_daily
disk_daily
```

Не дублируй данные бесконтрольно.

Indexes только там, где реально нужны queries:

```text
timestamp
agent + timestamp
anomaly timestamp
```

Не индексируй каждую колонку.

---

# RETENTION

Default:

```text
1-minute data:
14 days

15-minute rollups:
180 days

daily:
indefinite
```

Перед удалением minute rows должен существовать 15-minute rollup.

Перед удалением 15-minute rows должен существовать daily rollup.

Cleanup:

```text
once per day
one transaction
```

Не выполнять cleanup каждую минуту.

---

# DATABASE SIZE

AgentWatch history должна оставаться маленькой.

Ожидаю порядок:

```text
десятки MB,
а не GB.
```

Добавь self-check:

```text
DB file size
WAL size
```

Если собственный WAL неожиданно начинает расти — это anomaly AgentWatch.

Установи разумный alert, например:

```text
agentwatch.db > 256 MB
```

или если рост выходит далеко за ожидаемую модель.

---

# НЕ ДОПУСТИТЬ ИРОНИЧНОГО БАГА

Особенно важно:

AgentWatch создаётся для поиска excessive SQLite writes в Codex.

Поэтому AgentWatch сам НЕ должен создать:

```text
agentwatch.db-wal = 5 GB
```

Обязательно тестируй:

```text
DB growth
WAL growth
self WriteTransferCount
```

---

# ANOMALY DETECTOR

v1 должен записывать anomalies в БД.

Не нужны toast notifications.

Главное — чтобы через неделю report мог показать их.

Каждая anomaly:

```text
timestampStart
timestampEnd
type
severity
agent
short summary
structured evidence
```

Evidence лучше хранить компактно.

---

# INITIAL ANOMALIES

## RAM PRESSURE

Например:

Warning candidate:

```text
Available RAM < 512 MB
for >= 2 minutes
```

особенно если одновременно есть ненулевой sustained Pages Output/sec.

Critical candidate:

```text
Available RAM < 256 MB
+
сильный sustained page-out
```

Не привязывайся слепо к этим thresholds.

Сделай их configurable.

Отдельно считай:

```text
minutesBelow512MB
minutesBelow256MB
pageOutBytesTotal
```

---

# CODEX LOGICAL WRITE ANOMALY

Configurable defaults:

```text
Codex logical writes > 100 MB/min
for >= 10 minutes
```

warning.

Более сильный:

```text
> 500 MB/min
for >= 5 minutes
```

high severity.

Здесь речь именно:

```text
logical process write bytes
```

Не называй это физическим SSD write.

---

# CODEX DB ANOMALIES

Интересны:

```text
logs_2.sqlite growth
logs_2.sqlite-wal growth
```

Например записывать anomaly при:

```text
WAL > 512 MB
```

или:

```text
WAL grew > 500 MB within 10 minutes
```

или:

```text
logs_2.sqlite unexpectedly grows very rapidly
```

Thresholds configurable.

---

# SSD TEMPERATURE

Для конкретного PC411:

```text
warning >= 65°C
critical >= 70°C
```

Это initial defaults.

---

# NVME HEALTH ANOMALIES

Обязательно фиксировать:

```text
CriticalWarning changed from 0
MediaErrors increased
PercentageUsed increased
AvailableSpare decreased
UnsafeShutdowns increased
ErrorInfoLogEntries increased
```

UnsafeShutdown increase не обязательно критическая ошибка.

Просто отдельная anomaly/event.

---

# HOST WRITE HISTORY

Считай:

```text
physical host write delta
per hour
per day
per week
```

из NVMe Data Units Written.

Не делай жёсткий вывод:

```text
100 GB/day = плохо
```

без baseline.

Первые 7 дней можно считать learning/baseline period.

После накопления данных показывай:

```text
avg
median
P95 daily host writes
max day
```

если данных достаточно.

Не переусложняй statistical framework.

---

# ATTRIBUTION CAVEAT

Report должен честно говорить:

```text
Codex logical process writes:
X GB

Claude logical process writes:
Y GB

Total physical NVMe host writes:
Z GB
```

Не пытайся заявлять:

```text
Codex физически износил SSD ровно на X GB
```

потому что process counters и physical host writes не имеют отношения 1:1.

Разрешена аналитическая формулировка:

```text
Codex был крупнейшим источником наблюдаемого logical process I/O.
```

Но не ложная точность.

---

# CLI

CLI должен работать быстро.

Команды минимум:

```text
AgentWatch.exe status
AgentWatch.exe doctor
AgentWatch.exe snapshot
AgentWatch.exe report --since 24h
AgentWatch.exe report --since 7d
AgentWatch.exe report --since 30d
AgentWatch.exe report --since 7d --json
AgentWatch.exe agents --since 7d
AgentWatch.exe disk --since 7d
AgentWatch.exe anomalies --since 7d
AgentWatch.exe self --since 7d
```

Допустимо добавить:

```text
--from
--to
```

если это не сильно усложняет parser.

Не тащи тяжёлый CLI framework только ради десяти команд.

---

# STATUS EXAMPLE

Желаемый смысл:

```text
AgentWatch 1.0.0
Service: Running
Uptime: 3d 18h

Database:
42.7 MB

Last sample:
8 sec ago

Collectors:
System       OK
Processes    OK
Paging       OK
NVMe         OK
HotFiles     OK

Last NVMe:
Temp             51°C
Health used       0%
Host writes       5.94 TB
Media errors      0

Current:
CPU              21%
Available RAM    612 MB
Codex processes    4
Claude processes   7
```

Не обязательно копировать формат дословно.

---

# REPORT EXAMPLE

Команда:

```text
AgentWatch.exe report --since 7d
```

должна стремиться выдавать примерно такой уровень полезности:

```text
AgentWatch — last 7 days

SYSTEM

CPU
Average:              24.1%
P95:                  68.4%
Peak:                 94.7%

MEMORY
Minimum available:    118 MB
Time below 512 MB:    16h 42m
Time below 256 MB:     5h 11m
Estimated page-out:    73.2 GB

AGENTS

Codex
Active time:           41h 18m
Logical reads:        312.4 GB
Logical writes:        87.6 GB
Peak memory:            1.9 GB
Peak process count:        7

Claude
Active time:            8h 21m
Logical reads:         28.1 GB
Logical writes:         9.4 GB
Peak memory:            1.8 GB

SSD

Start host writes:      5.728 TB
End host writes:        6.013 TB
Physical host delta:      285 GB

Average/day:             40.7 GB
Peak/day:                72.3 GB

Temperature:
Average:                 51°C
Peak:                    63°C

Media errors:
0

Percentage used:
0%

Unsafe shutdowns:
50 -> 50

CODEX DATABASES

logs_2.sqlite:
800 MB -> 1.04 GB

largest WAL observed:
620 MB

ANOMALIES

12 RAM pressure events
3 sustained Codex write events
1 large logs_2 WAL event
0 NVMe health errors

INTERPRETATION

The strongest observed bottleneck was memory pressure.
...
```

Числа выше — только пример желаемого отчёта.

Не hard-code их.

---

# JSON REPORT

Очень важно.

Сделай стабильный machine-readable JSON.

Это станет фундаментом MCP.

Пример смысловой структуры:

```json
{
  "period": {
    "fromUtc": "...",
    "toUtc": "..."
  },
  "system": {
    "cpu": {},
    "memory": {},
    "paging": {}
  },
  "agents": {
    "codex": {},
    "claude": {}
  },
  "storage": {
    "nvme": {},
    "logicalProcessIo": {}
  },
  "hotFiles": {},
  "anomalies": [],
  "agentWatch": {},
  "limitations": []
}
```

Не сериализуй внутренние database entities напрямую.

Создай query/report DTO.

Это будет public-ish internal contract.

---

# FUTURE MCP BOUNDARY

НЕ реализовывай MCP сейчас.

Но `ReportService` / query layer должен позволить потом сделать tools вроде:

```text
health_summary
system_usage
agent_usage
disk_io
ssd_health
hot_files
anomalies
monitor_health
```

без raw SQL внутри MCP tool.

В:

```text
docs\MCP_FUTURE.md
```

опиши future mapping:

```text
MCP tool
→
existing query service method
```

Позже планируется официальный C# MCP SDK и stdio transport.

Не добавляй MCP NuGet dependency в v1, если он сейчас не используется.

---

# ERROR HANDLING

Collector failure не должен останавливать весь сервис.

Пример:

```text
NVMe query AccessDenied
```

результат:

```text
NVMe collector degraded
system/process collection continues
```

Ошибки должны:

* иметь rate limiting;
* не спамить Event Log;
* сохранять состояние collector health;
* появляться в `doctor`.

---

# LOGGING

Не логируй каждый sample.

Нужны только:

```text
service startup/shutdown
collector disabled/recovered
database errors
migration
retention errors
important anomaly
unexpected exception
```

Можно использовать Windows Event Log или маленький bounded log.

Не создавай гигабайты собственных текстовых логов.

Если используешь file logs:

* rotation;
* жёсткий size cap;
* например несколько файлов суммарно <= 10–20 MB.

Но Event Log для warning/error предпочтительнее, если реализация проста.

---

# TIME HANDLING

В БД:

```text
UTC
```

Для durations:

```text
monotonic time
```

Не полагайся на разницу wall clock между двумя samples.

Учитывай:

```text
sleep
resume
clock correction
timezone change
```

Если между fast samples прошло, например:

```text
> 2.5 × configured interval
```

отметь gap.

Не рассчитывай CPU rate на гигантском sleep interval как будто процесс работал всё время.

---

# SERVICE RESTART

После restart:

* cumulative process counters не сравнивать со старым unknown state;
* первую точку каждого процесса использовать только как baseline;
* SMART lifetime counter можно сравнить с последним persisted SMART sample;
* не создавать искусственный огромный I/O spike.

---

# SELF OVERHEAD TARGETS

Это design targets, которые нужно ИЗМЕРИТЬ.

Цель:

```text
average CPU:
< 0.3%

acceptable ceiling:
< 0.5% average

working set target:
< 50–60 MB

hard investigation threshold:
> 100 MB steady-state

persistent self logical writes:
желательно < 20 MB/day

hard target:
не превращаться в десятки/сотни MB/day без причины

DB:
десятки MB, не GB
```

Если .NET runtime делает `<50 MB` нереалистичным — не применяй опасные оптимизации.

Измерь и честно задокументируй.

---

# ALLOCATION / HOT PATH

Sampling loop должен быть скучным и дешёвым.

Избегай без необходимости:

```text
LINQ over all processes every 10 sec
large temporary arrays
string formatting per sample
JSON serialization per sample
WMI object graphs
reflection
recursively scanning directories
opening/closing SQLite each sample
spawning PowerShell each sample
spawning cmd.exe
spawning npx
```

PowerShell/CIM допустимы для:

```text
install
doctor
one-time system inventory
```

но не для 10-second runtime hot path.

---

# NATIVE INTEROP

Предпочитай современный безопасный interop.

Рассмотри:

```text
LibraryImport
SafeHandle
Span<byte>
```

где это упрощает корректность.

Очень внимательно работай с:

```text
struct layout
packing
endianness
DWORD/ULONG/ULONGLONG
128-bit NVMe values
handle lifetime
```

Не копируй случайный P/Invoke snippet из StackOverflow без проверки Windows headers/docs.

---

# TESTING STRATEGY

Нужны:

```text
unit tests
integration tests
actual-machine smoke test
performance/self-overhead test
```

Не считай «build succeeded» достаточным.

---

# UNIT TESTS — ОБЯЗАТЕЛЬНЫЕ

Покрой:

## PROCESS COUNTER DELTA

```text
normal delta
new process
process reset
PID reuse
counter lower than previous
process disappearance
```

## CPU DELTA

Проверь расчёт на искусственных known values.

## AGGREGATOR

Например 6 десятисекундных samples должны корректно дать minute aggregate.

## RETENTION

Проверь:

```text
minute → 15m
15m → daily
safe deletion
```

## ANOMALIES

Проверь sustained conditions, а не только single spike.

## NVME UINT128

Golden baseline:

```text
0xAAB764
→
11,188,068
→
5,728,290,816,000 bytes
```

## NVME TEMPERATURE

```text
322 K
→
49 °C approximately
```

## NVME HEALTH

Golden values:

```text
criticalWarning = 0
availableSpare = 100
threshold = 10
percentageUsed = 0
unsafeShutdowns = 50
mediaErrors = 0
```

## PROCESS TREE

Проверь:

```text
codex root
→ node child
→ child child
```

и отдельный unrelated:

```text
node.exe
```

который НЕ должен считаться Codex.

## JSON REPORT CONTRACT

Добавь snapshot/schema-oriented tests, чтобы будущий MCP не сломался случайно.

---

# INTEGRATION TEST: PROCESS I/O

Создай controlled helper process.

Он может:

1. создать временный небольшой файл;
2. записать известный объём, например 16 MB;
3. flush/close;
4. завершиться.

AgentWatch test collector должен увидеть:

```text
WriteTransferCount delta
```

примерно соответствующего порядка.

Не требуй exact физического disk write.

Не делай гигабайтные файлы.

Удалить test file после теста.

---

# INTEGRATION TEST: PROCESS TREE

Helper:

```text
parent
  → child
      → child
```

Проверь attribution.

Не нужно создавать сотни процессов.

---

# INTEGRATION TEST: SQLITE

Сымитируй несколько дней данных ускоренно.

Проверь:

```text
batch insert
query
rollup
retention
WAL behavior
DB size
```

Проверь concurrent:

```text
service writer
+
CLI read-only report
```

---

# ACTUAL NVME TEST

Если текущий процесс elevated:

попробуй native SMART collector на реальном:

```text
PhysicalDrive0
```

Результат должен быть примерно совместим с baseline:

```text
Model family / drive identification: YMTC PC411
Temperature around current real value
Data Units Written >= 11,188,068
Percentage Used near 0
Media Errors = 0 unless reality changed
```

Не требуй, чтобы Data Units Written был ровно baseline — диск используется после 2026-09-04.

Если AccessDenied:

* тест должен быть inconclusive/skipped;
* не считать implementation failing автоматически;
* `doctor` должен объяснить причину.

---

# DOCTOR

Команда:

```text
AgentWatch.exe doctor
```

должна проверять:

```text
Windows version supported
data directory
DB readability/writability
service status
current user profile path
PDH counters
process access
NVMe access
PhysicalDrive detection
hot Codex file paths
clock/sample health
database migrations
last successful collector timestamps
```

И выдавать:

```text
OK
WARN
ERROR
```

с объяснениями.

Никаких destructive fixes.

---

# SNAPSHOT

```text
AgentWatch.exe snapshot
```

должен выполнить один safe one-shot collection без service history requirement.

Полезно для debugging.

Вывести:

```text
CPU
RAM
paging
disk
Codex
Claude
hot files
NVMe
```

Если для NVMe нужны admin privileges — указать.

---

# PERFORMANCE TEST

После working implementation сделай реальное измерение overhead.

Не использовать стресс-тест.

Сценарии:

```text
A. Service active, AI agents idle/absent
B. если Codex/Claude сейчас уже работают — естественная текущая нагрузка
```

Хотя бы несколько минут каждого доступного сценария.

Измерь сам AgentWatch:

```text
CPU time delta
working set
private memory
ReadTransferCount
WriteTransferCount
DB size delta
WAL size behavior
sample duration P50/P95/max
```

Если сервис сам заметно грузит машину — исправь.

---

# DATABASE WRITE TEST

Отдельно посмотри:

```text
AgentWatch process WriteTransferCount
```

за measured interval.

Экстраполируй:

```text
MB/day
```

явно отметив, что extrapolation approximate.

Цель — доказать, что AgentWatch не создаёт новый disk-write problem.

---

# SQLite BATCH STRATEGY

Не commit каждую minute row отдельно.

Хорошая модель:

```text
minute aggregate generated
→ kept in memory
→ every 5 minutes
→ one transaction
→ multiple prepared inserts
→ commit
```

При нормальном shutdown:

```text
final flush
```

---

# DATA LOSS PHILOSOPHY

Это observability history, не banking system.

Допустимо потерять последние несколько минут при power loss.

НЕ нужно включать самые тяжёлые durability settings ценой лишних writes.

Но database corruption тоже недопустима.

Ищи разумный баланс.

---

# CODE QUALITY

Требования:

```text
nullable enabled
warnings reasonably clean
async cancellation
CancellationToken
no async void
proper disposal
SafeHandle where appropriate
no swallowed exceptions without telemetry
no giant God class
no magic hard-coded paths
no global mutable static state without reason
```

Комментарии — только где они объясняют Windows-specific nuance.

Не комментируй очевидный C#.

---

# VERSIONING

Начни с:

```text
0.1.0
```

или:

```text
1.0.0-alpha
```

до первого подтверждённого burn-in.

Храни schema version отдельно от app version.

---

# LOCAL GIT

Если repo ещё не создан:

```text
git init
```

Добавь нормальный `.gitignore`.

Разрешены локальные commits после логических этапов.

НЕ:

```text
git push
```

Не создавай remote без запроса пользователя.

Не публикуй код наружу.

---

# DOCUMENTATION

README должен объяснять:

```text
что такое AgentWatch
почему он существует
что собирает
что НЕ собирает
как build
как test
как install
как uninstall
как report
где data
какой overhead
какие limitations
```

---

# ARCHITECTURE.md

Опиши:

```text
sampling pipeline
process attribution
NVMe query
aggregation
persistence
retention
query path
future MCP boundary
```

Добавь простую ASCII architecture diagram.

---

# DATA_MODEL.md

Документируй каждую важную metric semantic.

Особенно:

```text
LogicalProcessWriteBytes
PhysicalDiskWriteBytes
NvmeHostWriteBytesLifetime
```

И большими буквами объясни:

```text
THEY ARE NOT EQUIVALENT.
```

---

# SECURITY.md

Опиши:

```text
service account
filesystem ACL
no network listener
no cloud
no command line capture
no prompt capture
no environment capture
privileged NVMe access
attack surface
```

---

# PERFORMANCE.md

После tests запиши реальные цифры:

```text
CPU
memory
logical writes
DB growth
sampling latency
```

Не оставляй только design target.

---

# OPERATIONS.md

Нужно:

```text
service start/stop
service status
doctor
logs
DB path
backup
uninstall
reset history if user explicitly chooses
recovery from corrupted DB
```

---

# DATABASE CORRUPTION

Если DB реально не открывается:

НЕ удаляй автоматически.

Например:

```text
agentwatch.db
→
agentwatch.db.corrupt.2026...
```

после безопасного detection, затем можно создать новую БД.

Но old DB оставь для анализа.

Не делать это при обычном transient `SQLITE_BUSY`.

---

# REPORT INTERPRETATION

Report может делать простые deterministic conclusions.

Например:

```text
RAM pressure was sustained for 37% of monitored active time.
```

или:

```text
Codex accounted for 82% of observed logical AI-agent writes.
```

или:

```text
NVMe host writes increased by 285 GB during the period.
```

Но избегай недоказуемых причинных утверждений.

Например НЕ писать:

```text
Codex physically wrote exactly 87.6 GB to NAND.
```

если это только process logical counter.

---

# REPORT LIMITATIONS SECTION

Автоматически включай caveats, если relevant:

```text
Process I/O is logical Windows process I/O and does not equal NAND writes.

Short-lived processes can be missed by polling.

NVMe SMART interval is lower-frequency than system sampling.

Data gaps occurred during sleep/service stop.

Attribution includes descendants observable at polling time.
```

---

# INITIAL ANALYTICS QUESTIONS

Спроектируй storage/query layer так, чтобы он мог отвечать на вопросы:

```text
Сколько часов Codex работал за последние 7 дней?

Сколько logical bytes записали Codex processes?

Сколько logical bytes записал Claude?

Сколько host writes получил SSD?

Какой был средний physical host write/day?

Какой день был самым тяжёлым?

Какая максимальная температура SSD?

Изменился ли Percentage Used?

Появились ли Media Errors?

Увеличились ли Unsafe Shutdowns?

Сколько времени RAM была ниже 512 MB?

Сколько времени RAM была ниже 256 MB?

Сколько данных примерно ушло в pagefile?

Коррелировали ли пики Codex writes с ростом logs_2.sqlite-wal?

Какой был максимальный размер logs_2.sqlite-wal?

Какой agent занимал больше RAM?

Какое максимальное количество Codex processes было одновременно?

Какой был overhead самого AgentWatch?
```

---

# OPTIONAL CORRELATION

Если реализуется просто, report может делать временное сравнение:

```text
high Codex writes
+
high logs_2 WAL growth
```

или:

```text
low available RAM
+
high Pages Output/sec
+
high disk write throughput
```

Не нужно строить ML.

Простые overlapping time windows достаточно.

---

# DO NOT USE ETW PERMANENTLY

WPR/WPA/ETW/ProcMon могут быть полезны в будущем для deep diagnostics.

Но НЕ включай постоянный kernel trace в v1.

Если когда-нибудь нужен burst mode:

```text
AgentWatch.exe diagnose --duration 60s
```

это будущая функция.

Не реализовывать сейчас.

---

# DO NOT IMPLEMENT OPTIMIZATION YET

AgentWatch v1:

```text
observe
measure
report
```

Не:

```text
throttle
kill
renice
set affinity
set efficiency mode
delete logs
change settings
```

Решения об оптимизации будут приниматься ПОСЛЕ 1–2 недель реальных данных.

---

# BUILD OUTPUT

Создай production publish.

Желаемая команда по смыслу:

```powershell
dotnet publish `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

Конкретные flags уточни для .NET 10.

Output:

```text
artifacts\publish\AgentWatch.exe
```

Старайся получить один основной executable.

Если рядом остаются unavoidable native runtime files из-за SQLite — исследуй, можно ли корректно bundled/single-file publish сделать без хрупких hacks.

Не жертвуй reliability только ради «буквально один файл».

Но конечная цель — максимально близко к одному EXE.

---

# BUILD REPRODUCIBILITY

После clean:

```text
dotnet restore
dotnet build
dotnet test
dotnet publish
```

должны проходить.

Не оставляй проект в состоянии:

```text
works only on my current bin/obj.
```

---

# WARNINGS / STATIC ANALYSIS

Исправь значимые warnings.

Можно включить стандартные analyzers.

Не добавляй огромный third-party analyzer stack.

---

# FINAL ACCEPTANCE CRITERIA

Перед тем как сказать, что задача завершена, должны выполняться все применимые пункты:

## BUILD

```text
Release build passes
```

## TESTS

```text
unit tests pass
integration tests pass
```

## SNAPSHOT

```text
snapshot works
```

## PROCESS

```text
Codex/Claude process discovery works
```

## PROCESS IO

```text
known test writer produces observable logical write delta
```

## PROCESS TREE

```text
child attribution tested
```

## MEMORY

```text
Available RAM and commit values plausible
```

## PAGING

```text
Pages Input/Output counters work on Russian Windows
```

## NVME

```text
native NVMe SMART works if privileges permit
```

## BASELINE VALIDATION

Parser reproduces:

```text
0xAAB764
→
5,728,290,816,000 bytes
```

## HOT FILES

Known Codex database file sizes are detected metadata-only.

## SQLITE

```text
batch persistence works
concurrent read report works
retention works
```

## SELF OVERHEAD

Measured and documented.

## INSTALLATION

If elevated environment available:

```text
install service
start
verify
restart
stop
start
```

If not elevated:

```text
installation commands generated and verified as far as possible
```

Не пытайся обходить отсутствие elevation.

## REPORT

```text
human report works
JSON report works
```

## DOCS

Все ключевые docs существуют.

---

# SERVICE INSTALL VALIDATION

Если можешь установить service:

1. установи;
2. проверь Windows service state;
3. проверь delayed automatic start config;
4. проверь recovery config;
5. дай ему собрать несколько samples;
6. выполни `status`;
7. выполни `snapshot`;
8. выполни короткий report;
9. перезапусти service;
10. убедись, что DB продолжает корректно использоваться.

Если acceptance проходит — service можно оставить установленным.

Если обнаружен серьёзный bug — не оставляй broken service в autostart.

---

# НЕ ПРОВОДИТЬ ДЛИТЕЛЬНЫЙ STRESS TEST

Пользователь работает на этой машине.

Не нужно часами нагружать ноутбук ради benchmark.

Используй короткие controlled tests.

Никаких:

```text
fio
CrystalDiskMark loops
winsat disk loops
Prime95
CPU burn
memory exhaustion
```

---

# ПРОВЕРКА REAL-WORLD OVERHEAD

После service start желательно получить хотя бы короткое реальное measurement window и ответить:

```text
AgentWatch average CPU:
...

AgentWatch working set:
...

AgentWatch private memory:
...

AgentWatch logical writes during N minutes:
...

Projected logical writes/day:
...

agentwatch.db size:
...

agentwatch.db-wal max observed:
...

Sample duration:
P50 ...
P95 ...
Max ...
```

Если projected writes выглядят неожиданно высоко — расследуй ДО завершения задачи.

---

# ВАЖНЫЙ PERFORMANCE PRINCIPLE

Не оптимизируй заранее микроскопические вещи.

Приоритет:

```text
архитектурно не делать дорогие операции
```

то есть:

```text
не WMI-scan каждую секунду
не filesystem recursive scan
не subprocess spawning
не SQLite commit каждый sample
не JSON serialization hot loop
не ETW permanent trace
```

Это даст гораздо больший эффект, чем бессмысленные micro-optimizations.

---

# ПОВЕДЕНИЕ ПРИ ОТСУТСТВИИ CODEX/CLAUDE

AgentWatch всё равно работает.

Process collectors возвращают:

```text
Codex process count = 0
Claude process count = 0
```

и не генерируют ошибки.

Это нормальное состояние.

---

# ПОВЕДЕНИЕ ПРИ SLEEP

После resume:

* не создавать огромный fake CPU spike;
* отметить monitoring gap;
* продолжить sampling;
* NVMe lifetime delta остаётся допустимым;
* process lifetime state перепроверить.

---

# ПОВЕДЕНИЕ ПРИ UPDATE/REBOOT

Daily data должна переживать:

```text
AgentWatch restart
Windows reboot
Codex restart
Claude restart
```

---

# FUTURE TOKEN ANALYTICS — ТОЛЬКО DESIGN NOTE

В будущем можно будет добавить:

```text
TokenUsageCollector
```

для Codex/Claude.

Но в v1 не запускай:

```text
npx ccusage
```

постоянно.

Если хочешь подготовить интерфейс:

```text
ITokenUsageProvider
```

достаточно.

Не реализовывай provider без необходимости.

---

# FUTURE MCP — DESIGN

После успешного v1 появится:

```text
AgentWatch.exe mcp
```

Он должен быть:

```text
read-only
stdio
on-demand
```

MCP не должен становиться вторым постоянным daemon.

То есть:

```text
Windows boot
   ↓
AgentWatch service
   ↓
collects history

later:

Codex
   ↓
starts AgentWatch.exe mcp
   ↓
MCP reads existing DB
   ↓
returns report
   ↓
process exits when MCP client closes
```

Именно такую архитектуру нужно сохранить.

---

# FUTURE MCP QUERY EXAMPLES

Будущие вопросы:

```text
"Посмотри AgentWatch за неделю и скажи,
почему ноут чаще включал вентиляторы."

"Сколько Codex записал logical data за последние 24 часа?"

"Сколько physical host writes получил SSD?"

"Есть ли признаки excessive Codex logging?"

"Когда logs_2.sqlite-wal рос быстрее всего?"

"Было ли узким местом 8 GB RAM?"

"Сколько времени Windows активно выгружала память в pagefile?"

"Изменился ли SSD Percentage Used?"

"Появились ли media errors?"

"Насколько сам AgentWatch нагружает ноут?"
```

Current query API должен уже уметь получать необходимые данные.

---

# ИССЛЕДОВАНИЕ ПЕРЕД РЕАЛИЗАЦИЕЙ

Перед coding быстро проверь по официальной документации актуальные детали:

```text
.NET 10 Windows Worker Service
AddWindowsService
single-file publishing
GetSystemTimes
GlobalMemoryStatusEx
GetPerformanceInfo
GetProcessIoCounters
GetProcessTimes
process memory APIs
CreateToolhelp32Snapshot
PDH / PdhAddEnglishCounterW
IOCTL_STORAGE_QUERY_PROPERTY
STORAGE_PROTOCOL_SPECIFIC_DATA
NVME_HEALTH_INFO_LOG
Microsoft.Data.Sqlite
```

Не трать часы на research.

Цель research — подтвердить signatures/semantics, а затем реализовать.

---

# ПОРЯДОК РАБОТЫ

Работай примерно такими этапами:

```text
0. inspect environment
1. initialize solution/repo
2. write architecture/design docs
3. implement native system collectors
4. implement process tracking/attribution
5. implement NVMe collector
6. implement hot-file collector
7. implement in-memory aggregation
8. implement SQLite persistence
9. implement retention
10. implement anomaly detection
11. implement CLI query/report
12. implement Windows Service hosting
13. implement install/uninstall
14. unit tests
15. integration tests
16. actual machine validation
17. performance measurement
18. publish
19. final docs
```

Но можешь адаптировать последовательность.

Не останавливайся после skeleton.

---

# ПОСЛЕ КАЖДОГО КРУПНОГО ЭТАПА

Запускай relevant tests.

Не накапливай 40 файлов кода до первой компиляции.

---

# ЕСЛИ НАХОДИШЬ ПРОБЛЕМУ В МОИХ ТРЕБОВАНИЯХ

Если конкретный технический пункт:

```text
невозможен
опасен
имеет неожиданно высокий overhead
противоречит documented Windows semantics
```

не выполняй его слепо.

Сделай более корректно.

Зафиксируй:

```text
что было изменено
почему
какая альтернатива выбрана
```

в:

```text
docs\ARCHITECTURE.md
```

или отдельном ADR/decision note.

---

# НЕ СПРАШИВАТЬ ПРО ПРЕДПОЧТЕНИЯ, КОТОРЫЕ МОЖНО РЕШИТЬ ТЕХНИЧЕСКИ

Например не спрашивай:

```text
"xUnit или MSTest?"
"какое имя класса?"
"какой ORM?"
```

Выбирай сам.

Если dependency реально влияет на безопасность/лицензию/runtime footprint — исследуй и выбери консервативный вариант.

---

# ФИНАЛЬНЫЙ ОТЧЁТ ПОСЛЕ РЕАЛИЗАЦИИ

В конце дай пользователю компактный, но фактический отчёт:

```text
IMPLEMENTED
что создано

ARCHITECTURE
как работает

FILES
основные файлы проекта

BUILD
результат build

TESTS
сколько tests / результат

INSTALLATION
установлен ли service

CURRENT STATUS
running/stopped

REAL MACHINE VALIDATION
какие collectors реально работают

NVME
какие текущие значения прочитаны

OVERHEAD
измеренные CPU/RAM/writes

DATABASE
size / WAL / projected growth

KNOWN LIMITATIONS
что v1 пока не видит

NEXT STEP
что нужно для MCP v2
```

Не говори просто:

```text
"готово"
```

без цифр.

---

# ОСОБЫЙ ПРИОРИТЕТ ЭТОЙ МАШИНЫ

У машины:

```text
8 GB RAM
```

и baseline уже показывал сильное memory pressure.

Поэтому если придётся выбирать между:

```text
более детальной статистикой
```

и:

```text
меньшим runtime overhead
```

выбирай меньший overhead.

Историю можно сделать менее гранулярной.

Но нельзя сделать монитор тяжёлым.

---

# DEFINITION OF SUCCESS

Через неделю непрерывной работы пользователь должен суметь получить достоверный ответ примерно на такой вопрос:

```text
"За последние семь дней ноутбук был включён X часов.

Codex был активен Y часов и создал примерно Z GB
наблюдаемого logical process write I/O.

Claude создал примерно W GB.

NVMe lifetime Host Writes увеличились
с A TB до B TB, то есть физический host-write delta составил C GB.

SSD достигал максимум T °C.
Media errors не появились.
Percentage Used не изменился.

RAM находилась ниже 512 MB N часов.
Система выгрузила примерно P GB страниц через pagefile.

Наиболее сильные периоды disk activity совпадали /
не совпадали с Codex logical writes и ростом logs_2.sqlite-wal.

Сам AgentWatch использовал в среднем Q% CPU,
R MB RAM и записывал примерно S MB/day.

По данным мониторинга наиболее вероятный bottleneck:
...
"
```

Если AgentWatch позволяет честно и с caveats получить такой ответ при практически незаметной собственной нагрузке — v1 достиг цели.

---

# НАЧИНАЙ

Теперь:

1. Перейди в:

```text
C:\sarychev\Codex\AgentWatch
```

2. Проверь текущую среду и установленный .NET SDK.

3. Исследуй только необходимые official Windows/.NET APIs.

4. Создай проект.

5. Реализуй AgentWatch v1 полностью.

6. Запускай тесты по мере работы.

7. Не делай destructive changes в Windows.

8. Не используй WSL.

9. Не реализовывай MCP сейчас.

10. Не останавливайся на архитектурном описании — нужен рабочий код, tests, publish и измеренный overhead.

11. Если административных прав нет, реализуй и протестируй всё возможное без обхода UAC и оставь точную команду для финальной установки service.

12. В финале дай измеренные результаты, а не только описание.
