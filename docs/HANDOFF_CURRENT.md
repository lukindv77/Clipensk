# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-20 (добавлены single-instance на пользователя и защита каталога данных; принят большой блок продуктовых решений — см. `docs/OPEN_QUESTIONS.md`; остаток — реализация этих решений, перенос данных при смене пути и ручной smoke).

Mutable GitHub state is authoritative and supersedes this file. Перед любой durable repository write обязательна fresh TOCTOU-проверка relevant refs/files; перед promotion — fresh `main`, feature ref, compare и canonical workflows.

## A. Project identity

Clipensk — resident Windows clipboard-history manager.

- repository / source of truth: `lukindv77/Clipensk`;
- canonical branch: `main`;
- Windows x64/AMD64 only; ARM64 вне scope;
- C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected SQLite uses SQLCipher; один MasterKey на storage;
- Current schema v10, Storage Catalog schema v3, Archive schema v1;
- обязательные правила: `AGENTS.md`, `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`, `docs/CI_LOG_ACCESS.md`, `docs/ARCHIVE_SPLIT_PROTOCOL.md`, `docs/ARCHIVE_ROTATION_PROTOCOL.md`, `docs/LOCAL_BUILD_AND_TEST.md`.

## B. User intent

Автономное продолжение разработки без повторения завершённых этапов и без лишних уточняющих вопросов.

Постоянные требования:

- promotion в `main` только fast-forward, `force=false`, без merge commit;
- PASS/accepted только по exact-SHA CI evidence;
- при Actions failure сначала connector logs/artifacts, затем `docs/CI_LOG_ACCESS.md`; причину не угадывать;
- временные feature triggers перед promotion восстанавливать byte-for-byte;
- manual production WinUI UX/smoke не считать проверенным без отдельного durable evidence;
- язык взаимодействия — **русский**;
- после каждой задачи сообщать рекомендуемую модель и сложность (`AGENTS.md`).

## C. Current authoritative state

Последний промотированный main: `bbef1326070f126642417835762a7929112e2289`.

Exact-main CI для `bbef132…`:

- Build #505, run `35518612583` — **SUCCESS**;
- Native SQLCipher #119, run `35518613993` — **SUCCESS** (запущен вручную через `workflow_dispatch`, т.к. слайс не менял `src/Clipensk.Storage/**`/`src/Clipensk.Core/Storage/**`).

Важно: слайс tray/автозапуска сначала упал с `CS0051` (публичный конструктор `WindowsTrayIconService` принимал `internal ResidentMessageWindow`); исправлено (конструктор стал `internal`, как у `GlobalHotKeyService`) — см. §F.

Baseline **ACCEPTED**: Archive Rotation, возврат записи в clipboard, автоудаление Trash по сроку, период/поиск/фильтр журнала, ручная блокировка + автоблокировка по простою, загрузка внешних переводов, tray-иконка/автозапуск/страница «О программе», а также single-instance на пользователя Windows и защита каталога данных реализованы.

**Важно про продуктовые решения:** 2026-09-20 пользователь принял большой блок решений (лицензия, версии Windows, схема распространения, дефолты форматов, период журнала, пороги ротации, формат файлов локализации, focus restoration, single-instance/изоляция). Все они записаны в `docs/OPEN_QUESTIONS.md` с явной пометкой, что именно уже реализовано, а что ещё нет. **Большая часть из них ещё не реализована в коде** — см. §I.

Важно про Native: он не запускается на push в `main`, если изменения не попадают под path-фильтры
`sqlcipher-native.yml` (`src/Clipensk.Storage/**`, `src/Clipensk.Core/Storage/**`, `Clipensk.App.csproj`
и другие). Для чисто App/Core.Settings изменений Native нужно запускать вручную на том же SHA — это
единственная проверка publish-пути `Clipensk.App`.

Canonical Build workflow blob: `6bb0e8b8eda657c082738a64a4ba80acd857daf4`

Полная таблица приёмки по каждому слайсу Archive Rotation — в `docs/ARCHIVE_ROTATION_PROTOCOL.md` §15.

## D. Current owner / active task

Archive Rotation: **завершена end to end** — storage-слой, runtime-интеграция, экран настроек и ручной триггер. Осталось только продуктовое решение по дефолтам и ручной smoke.

Стартовая последовательность живёт в `ProtectedStorageStartupRecoveryCoordinator`:

- `RecoverAsync(currentLocalDate, ct)` — Archive Split → Archive Rotation → policy maintenance, в этом порядке;
- `RunAsync(currentLocalDate, rotationSettings, ct)` — то же плюс новая ротация после того, как всё прерванное досведено; без настроенных порогов ротация не запускается.

Вызов — из `App.PolicyMaintenance.cs` внутри suspension-блока; при любой ошибке capture не возобновляется.

Ручной запуск: `App.TryRunArchiveRotationAsync` (quiesce → `StartAndCompleteAsync` → resume только если маркер снят), вызывается со страницы Maintenance.

Пороги правятся на странице настроек через `ArchiveRotationSettingsDraft` (`Clipensk.Core/Settings/`).

Возврат записи в clipboard реализован:

- `ProtectedClipboardHistoryRestoreService` — перепроверка внешних файлов при восстановлении (containment в `Files/`, reparse-point, размер, SHA-256);
- `ClipboardRestorePlanFactory` — состав восстанавливаемых форматов; файловый drop отдаётся текстом по одному пути на строку и никогда не затирает захваченный plain text;
- `ClipboardSelfWriteSuppressor` + `ClipboardUpdateMonitor` — собственная запись не захватывается повторно, опознаётся по clipboard sequence number;
- `WindowsClipboardRestoreWriter` — публикация `DataPackage`; файлы разрешаются до публикации, между `SetContent` и чтением sequence number нет `await`, пакет не flush-ится;
- `App.TryRestoreToClipboardAsync` + кнопка «Скопировать в буфер обмена» в журнале.

Фактическая работа возврата в буфер **не подтверждена**: весь Windows/WinUI слой автотестами не покрыт.

Журнал получил период по умолчанию, поиск и фильтр по приложению:

- `DefaultJournalPeriod.ForDays` (`Clipensk.Core/Settings/`) — чистое правило периода; opt-in, конкретное значение не выбрано (`OPEN_QUESTIONS.md` §7);
- `ClipboardHistorySearchMatcher` + `SqliteClipboardHistorySearchFunction` — поиск через кастомную SQL-функцию на `.NET`-сравнении (`OrdinalIgnoreCase`), а не встроенный `LIKE`/`LOWER` SQLite, который фолдит регистр только для ASCII. Функция регистрируется на соединении **безусловно**: SQL-текст ссылается на неё даже когда term = NULL, и SQLite резолвит имя функции при подготовке запроса, а не в рантайме;
- `sourceApplicationId`-фильтр — точное сравнение на `ClipboardHistoryEvent.SourceApplicationId`, тем же путём, что и поиск;
- оба фильтра применяются **внутри** уже выбранного по периоду набора БД и не меняют, какие файлы открываются;
- «приложение вызова журнала» — `InvocationApplication` резолвится в `ApplicationId` через **read-only** `SqliteApplicationIdentityRepository.FindAliasesAsync` (не `ResolveOrCreateAsync`): открытие журнала не создаёт identity как побочный эффект. Пункт выпадающего списка появляется, но не выбирается автоматически.

Ручная блокировка и автоблокировка по простою реализованы:

- `JournalWindow.TryLockNow()` — единственная реализация блокировки «прямо сейчас», переиспользует существующий `ProtectedApplicationLifecycle.TryBeginLock`/`CompleteLock`, который уже отзывает MasterKey через `ProtectedDataAccessLease`/`ProtectedStorageSessionLease`; новой инфраструктуры отзыва не потребовалось;
- `IdleAutoLockTrigger` (`Clipensk.Core/Security/`) — чистое edge-triggered правило: срабатывает один раз за непрерывный период простоя, достигший порога, и повторно вооружается только когда простой падает ниже порога;
- `WindowsIdleTimeReader` (`Clipensk.Windows/Security/`) — системный (не только приложения) простой через `GetLastInputInfo`, с корректной арифметикой 32-битного переполнения tick count;
- `App.AutoLock.cs` — фоновый `System.Threading.Timer` на 15 секунд; чтение простоя и решение триггера — на thread-pool потоке, сама блокировка маршалится на dispatcher окна;
- `AutoLockAfterMinutes` (`ApplicationSettings`) — opt-in, без произвольного дефолта, как и `DefaultJournalPeriodDays`/пороги ротации.

Загрузка внешних переводов (`REQUIREMENTS.md` §20) реализована:

- `ExternalOverlayLocalizationService` (`Clipensk.Core/Localization/`) — оверлей поверх `BuiltInRussianLocalizationService`; заменяемый на лету через `SetOverlay`, поэтому загрузка сразу видна во всех последующих `GetString`;
- `JsonExternalLocalizationLoader` (`Clipensk.Infrastructure/Localization/`) — чтение `*.json` файла перевода (плоский объект строк с теми же ключами);
- `ActiveLocalizationFileName` (`ApplicationSettings`) — голое имя файла внутри `<DataRoot>\Languages\`, никогда не абсолютный путь; `JsonApplicationSettingsStore` отклоняет любое значение с `/`, `\`, `.` или `..` **явными проверками обоих слэшей**, а не через `Path.GetFileName` (на Linux, где идёт локальный прогон тестов, `\` не является разделителем);
- три действия в настройках: «Загрузить файл перевода…» (валидация до копирования в Languages), «Открыть папку Languages», «Перечитать переводы» (никогда не пишет в settings как побочный эффект).

Tray-иконка, автозапуск и страница «О программе» реализованы (продуктовые решения пользователя от 2026-09-20, зафиксированы в §I):

- `WindowsTrayIconService` (`Clipensk.Windows/Interop/`) — переиспользует тот же `ResidentMessageWindow`, на котором уже работают hotkey и clipboard listener, вместо создания второго скрытого окна: `Shell_NotifyIcon` просто постит сообщения на переданный HWND. Левый клик открывает журнал; правый — меню (Журнал / Настройки / Завершить работу) через `TrackPopupMenuEx` с `TPM_RETURNCMD`, с обязательными `SetForegroundWindow`/`PostMessage(WM_NULL)` вокруг вызова (иначе меню не закрывается корректно). Иконка берётся из самого exe (`ExtractIconEx`), с фолбэком на системную `IDI_APPLICATION`, если embedded-иконки нет;
- закрытие крестиком уже сворачивало в трей (`JournalWindow.OnAppWindowClosing`), просто не было иконки, чтобы вернуться; `ExitApplication()` тоже уже существовал, но был не вызван ниоткуда — теперь это пункт «Завершить работу»;
- `WindowsAutostartService` (`Clipensk.Windows/Autostart/`) — реестровый `HKCU\...\Run`, никогда `HKLM`; запись в реестр выполняется **до** сохранения настройки, иначе Clipensk не должен утверждать состояние, которого не добился;
- страница «О программе» — реальный контент вместо общей заглушки: описание, ссылка на GitHub, «Сборка: {git SHA}» — SHA зашивается в `AssemblyMetadata` из `$(GITHUB_SHA)` в `Clipensk.App.csproj`, доступен только у CI-собранных бинарников (ручная сборка показывает «неизвестна»), сознательно не изобретает semver.

Single-instance и защита каталога данных реализованы (продуктовые решения — `OPEN_QUESTIONS.md` §12):

- `WindowsSingleInstanceGuard` (`Clipensk.Windows/Interop/`) — один экземпляр на пользователя Windows через эксклюзивно удерживаемый `%LocalAppData%\Clipensk\instance.lock`. Именно файл, а не named mutex: `Global\`-имя — единственное видимое между сессиями терминальных служб, но его создание требует привилегии, которой у обычного пользователя может не быть; файловый замок беспривилегированный, машинный и самовосстанавливается после kill;
- второй запуск не поднимает конкурирующий runtime: находит message-only окно первого (`FindWindowEx(HWND_MESSAGE, …)`), выдаёт ему право на передний план (`AllowSetForegroundWindow`) и постит `WM_APP+2` → `ResidentWindowsHost.ActivationRequested` → показ журнала. Имя класса окна потеряло PID-суффикс, чтобы его можно было найти; классы окон per-process, поэтому коллизий нет;
- между сессиями это невозможно (оконные хэндлы не пересекают сессии) — там показывается сообщение, а не молчаливый выход;
- `SettingsPathProvider` теперь владеет тремя путями: `settings.json`, каталогом данных по умолчанию (`%LocalAppData%\Clipensk\Data`) и lock-файлом. Замок намеренно лежит рядом с настройками, а не в каталоге данных: он должен работать до того, как каталог данных вообще настроен, и не должен переезжать при смене пути;
- `WindowsDataRootProtectionService` (`Clipensk.Windows/Security/`) ограничивает ACL каталога текущим пользователем + `SYSTEM` + `Administrators`, снимая наследование, **до** записи первого durable-байта. Возвращает три исхода (`Protected`/`Unsupported`/`SkippedNonEmptyDirectory`), каждый со своим сообщением пользователю.

## E. What has been completed

Archive Split завершён и не подлежит повторной реализации (детали — `ARCHIVE_SPLIT_PROTOCOL.md`).

Archive Rotation, принято в `main` (evidence — §15 протокола):

1. `ArchiveRotationSettings`, thresholds, explicit `Any`/`All`, JSON persistence;
2. чистый `ArchiveRotationPlanner` — ready ranges + open tail, post-day `>=`;
3. Current schema **v10** + `SqlitePendingArchiveRotationRepository`, миграция, взаимная блокировка с pending Split;
4. `ProtectedArchiveRotationSourceScanner` — eligible-окно закрытых дней, детерминированное выделение base-номеров;
5. `ProtectedArchiveRotationShadowBuilder` — hidden staging, оба режима планирования, замер физического размера на закрытом `.db`, cross-check; общий `ArchiveShadowWriter` со Split;
6. `ProtectedArchiveRotationPublisher` — copy-first публикация, атомарный move без overwrite, retained staging backup;
7. `ProtectedArchiveRotationSourcePurgeService` — verified purge через lease-aware ядро `ProtectedCurrentToArchiveTransferService`;
8. `ProtectedArchiveRotationCatalogPublisher` — перестроение проекции, очистка staging, снятие маркера;
9. `ProtectedArchiveRotationRecoveryService` — roll-forward из любой durable фазы;
10. `ProtectedArchiveRotationStartService` — атомарный старт под одним mutation lease;
11. `ProtectedStorageStartupRecoveryCoordinator` — единый упорядоченный roll-forward до возобновления capture (`cabc962a…`);
12. `StartAndCompleteAsync` + `RunAsync` — автоматический запуск подошедшей ротации после recovery, opt-in по настройкам (`3e0d7bfb…`);
13. `ArchiveRotationSettingsDraft` + экран настроек порогов и Any/All (`a2791aa1…`);
14. `App.TryRunArchiveRotationAsync` + кнопка «Выполнить ротацию сейчас» в Maintenance (`5cc4d341…`).

Возврат записи в clipboard, принято в `main`:

15. `ProtectedClipboardHistoryRestoreService` — read-путь внешних payload с проверками (`3b8098e2…`);
16. `ClipboardSelfWriteSuppressor` + состав восстановления (`53c953c6…`);
17. файловый drop возвращается текстом, `ReadFullPaths` рядом с writer (`303e798d…`);
18. `WindowsClipboardRestoreWriter`, подавление в мониторе, кнопка в журнале (`0d444c19…`).

Trash, принято в `main`:

19. `ProtectedExternalPayloadTrashRetentionService` — удаление истёкших payload и подключение последним шагом стартовой последовательности (`1b01d53b…`).

Журнал, принято в `main`:

20. `DefaultJournalPeriod` + экран настроек периода (`5452895…`);
21. `ClipboardHistorySearchMatcher`/`SqliteClipboardHistorySearchFunction`, поиск в Current+Archive, строка поиска в журнале (`9330867…`);
22. `sourceApplicationId`-фильтр, read-only резолв `InvocationApplication`, выпадающий список приложений в журнале (`f28dd691…`).

Блокировка, принято в `main`:

23. `IdleAutoLockTrigger`, `WindowsIdleTimeReader`, `TryLockNow()`, кнопка «Заблокировать сейчас» и автоблокировка по простою в настройках (`d6856c6…`).

Локализация, принято в `main`:

24. `ExternalOverlayLocalizationService`, `JsonExternalLocalizationLoader`, `ActiveLocalizationFileName`, загрузка/открытие папки/перечитывание в настройках (`ceddf7a…`).

Оболочка, принято в `main`:

25. `WindowsTrayIconService`, `WindowsAutostartService`, реальная страница «О программе» (`0bb0be0…`).

Оболочка и изоляция, принято в `main`:

26. `WindowsSingleInstanceGuard`, `WindowsDataRootProtectionService`, per-user пути в `SettingsPathProvider`, кнопка расположения по умолчанию на первом запуске (`bbef132…`).

Storage 595, Core 260, Infrastructure 69 — все зелёные локально и в CI.

## F. Load-bearing design decisions

- Пороговое правило Any/All определено **один раз** в `ArchiveRotationSettings.HasReachedThresholds`; конфигурация с `MaxBytes` без измеренного размера — fail-closed.
- Построение Archive v1 shadow, exact source→shadow сверка и полная валидация Archive живут **один раз** в `ArchiveShadowWriter`, общем со Split.
- Purge не имеет собственной реализации переноса.
- **Всякий держатель mutation lease обязан использовать `…InTransaction`-помощники репозитория**, а не его lease-берущие точки входа: повторный вход в lease даёт дедлок. На этом уже один раз наступили.
- Recovery из `Planned` пересобирает shadow и **обязан воспроизвести плановый физический размер** — это evidence, с которым publication сверяет опубликованные копии.
- `currentLocalDate` везде передаётся параметром, а не берётся из системных часов.

- Containment-проверка пути **не защищает** от симлинка внутри управляемого каталога:
  `Path.GetFullPath` не разыменовывает ссылки, поэтому путь через подложенную ссылку остаётся
  формально внутри корня. Защищает только проверка reparse point на каждом уровне. Проверено
  экспериментально на Trash retention: без неё удаляется реальный файл за пределами `Trash`.

- SQLite резолвит имена кастомных SQL-функций **при подготовке запроса**, а не в рантайме: ветка
  `$param IS NULL OR customFunc(...)` не спасает, если функция не зарегистрирована на соединении —
  `no such function` возникает даже когда параметр NULL. Регистрировать функцию нужно безусловно,
  не только когда параметр реально задан. Поймано полным прогоном тестов, не предположением.

- Встроенный `LIKE`/`LOWER` SQLite фолдит регистр только для ASCII. При основном языке продукта —
  русском — это делает текстовый поиск по факту нерабочим для кириллицы без явной регистрации
  `.NET`-функции сравнения через `SqliteConnection.CreateFunction`.

- Read-only lookup identity (`FindAliasesAsync`) и create-or-resolve (`ResolveOrCreateAsync`) — два
  разных метода не просто из соображений API-чистоты: открытие UI-фильтра не должно создавать
  identity как побочный эффект простого просмотра.

- `Path.GetFileName`/`Path.DirectorySeparatorChar` **платформозависимы**: на Linux, где идёт
  локальный прогон тестов, `\` не разделитель пути, поэтому `Path.GetFileName("..\\x") == "..\\x"`
  на Linux (проверка НЕ ловит traversal), но `!= "..\\x"` на Windows. Для проверок вида «это голое
  имя файла, а не путь» нужны явные проверки обоих слэшей (`Contains('/')`/`Contains('\\')`), а не
  платформенные `Path`-хелперы. Поймано до пуша через продумывание, не через живой прогон на Windows.

- `Clipensk.Windows`-типы, которые оборачивают `internal ResidentMessageWindow` (например
  `GlobalHotKeyService`, теперь и `WindowsTrayIconService`), обязаны иметь **`internal`-конструктор**:
  публичный конструктор с параметром менее доступного типа — это `CS0051`, а не предупреждение.
  Поймано реальным `CS0051` из Build CI (Build #500 → `internal` fix → Build #501 SUCCESS), не
  предположением — `Clipensk.App`/`Clipensk.Windows` не собираются локально на Linux, поэтому такие
  ошибки видны только через фактический CI-лог.

- ACL каталога данных переписываются **только** для каталога, который Clipensk создал сам, либо
  для пустого. Безусловное применение прав к произвольной выбранной пользователем папке отобрало бы
  у других пользователей доступ к посторонним данным, лежащим в ней рядом, причём рекурсивно по
  всему поддереву. Поймано самопроверкой до промоушена, уже после первого зелёного Build.

- `SYSTEM` и `Administrators` в ACL каталога данных оставлены намеренно. Их удаление не добавляет
  приватности там, где администратор может забрать владение, но ломает резервное копирование,
  антивирус и обслуживание.

## G. Important invariants

- Перед durable write: fresh target ref, `AGENTS.md`, relevant files.
- Перед promotion: fresh `main`, feature ref, compare; `behind_by=0`; merge-base = текущий main; review полного net diff; `.github/` в net diff отсутствует.
- `main` только fast-forward, `force=false`.
- Изменения в `src/Clipensk.Storage/**` или `src/Clipensk.Core/Storage/**` требуют exact-main Build **и** Native SQLCipher.
- **Concurrency-группа Native задана по ref**, поэтому push в `main` отменяет выполняющийся Native предыдущего SHA. Промотировать строго по одному слайсу за гейт.
- Ротация не делит `CalendarDate`; purge только после подтверждённой durable публикации; Catalog — rebuildable projection; коллизия канонического имени — fail-closed; pending rotation и pending Split взаимно исключаются.

## H. Known risks / unresolved questions

- Manual production WinUI smoke — **UNVERIFIED** для Split, Rotation, возврата в clipboard и журнальных фильтров: весь WinUI/WinRT-слой не покрыт автотестами, CI подтверждает только компиляцию и publish.
- Локальные тесты идут на `e_sqlite3`, а не SQLCipher: шифрование, native provenance и published-runtime loading локально не проверяются.
- SDK не переживает пересоздание сессии; процедура — `docs/LOCAL_BUILD_AND_TEST.md`.

## I. Remaining work

**Продуктовые решения приняты 2026-09-20 и записаны в `docs/OPEN_QUESTIONS.md`** (там же для каждого
явно указано, реализовано оно или нет). Их больше не нужно запрашивать у пользователя — нужно
реализовать. Не реализовано:

1. ~~Период журнала по умолчанию = 30 дней~~ — **реализовано в коде** (`OPEN_QUESTIONS.md` §7,
   `ApplicationSettings.DefaultJournalPeriodDays = 30`), но ещё **не в `main`**: лежит на ветке
   `feat/journal-rotation-defaults`, ожидает build/test/CI/promotion (см. §J).
2. ~~Дефолты ротации~~ — **реализовано в коде** (`OPEN_QUESTIONS.md` §8): `ArchiveRotation` по
   умолчанию `{ MaxCalendarDays = 30 }`, record count/размер БД по-прежнему выключены;
   `ArchiveRotationSettingsDraft.ToSettings()` теперь бросает при выбранном режиме без порога. Та же
   ветка `feat/journal-rotation-defaults`, тот же статус — ждёт CI/promotion.
3. **Дефолты форматов при новой установке** (`OPEN_QUESTIONS.md` §6): Plain/Unicode Text, HTML, RTF,
   изображения, custom binary, `CF_HDROP`. **Заблокировано**: конкретные числовые лимиты для каждого
   формата пользователем ещё не названы.
4. ~~Focus restoration~~ — **частично реализовано в коде** (`OPEN_QUESTIONS.md` §10):
   `WindowsForegroundFocusTracker` захватывает HWND в памяти при первом показе скрытого окна
   (`ShowJournal()`, по `AppWindow.IsVisible`) и восстанавливает фокус в единственном сегодня
   существующем триггере скрытия — `OnAppWindowClosing` (крестик → трей). Escape и «клик
   мимо»/авто-скрытие как отдельные UX-триггеры скрытия окна **не существуют в коде вообще** — это
   отдельная незапланированная задача с открытым вопросом поведения (например, не скрывать окно,
   пока открыт системный File Picker). Лежит на ветке `feat/journal-focus-restoration`, ожидает
   build/test/CI/promotion (см. §J); тестов нет — `WindowsForegroundFocusTracker` чистый P/Invoke,
   как `WindowsIdleTimeReader`.
5. **Режим «вставить как plain text»** (`OPEN_QUESTIONS.md` §10).
6. ~~Только Windows 11~~ — **реализовано в коде** (`OPEN_QUESTIONS.md` §2):
   `SupportedOSPlatformVersion` поднят до `10.0.22000.0` в `Clipensk.App`/`Clipensk.Windows`. Та же
   ветка `feat/journal-rotation-defaults`, тот же статус — ждёт CI/promotion. Отдельно остаётся
   нерешённым (не блокирует эту ветку): нужна ли runtime-проверка версии ОС при запуске на Windows 10.
7. **MSIX-упаковка** в дополнение к unpackaged (`OPEN_QUESTIONS.md` §3).
8. ~~Комментарий на русском в файлах перевода + экспорт шаблона~~ — **реализовано в коде**
   (`OPEN_QUESTIONS.md` §9): кнопка «Экспортировать шаблон перевода» и `LocalizationTemplateWriter`
   создают `translation-template.json` со всеми ключами, значением-заготовкой и русским `//`
   комментарием над каждым ключом; `JsonExternalLocalizationLoader` пропускает такие комментарии и
   завершающую запятую. Лежит на отдельной ветке `feat/localization-template-export`, ожидает
   build/test/CI/promotion (см. §J).
9. **Перенос настроек и базы при смене пути** (`OPEN_QUESTIONS.md` §12) — отдельный заход по прямому
   решению пользователя; по цене ошибки это аналог Archive Rotation/Split.

Остаётся открытым (решение НЕ принято): `OPEN_QUESTIONS.md` §11, merge/rebind identity приложений.

Отдельно, не код:

10. Manual production WinUI/storage smoke evidence (остаётся `UNVERIFIED`). Покрывает возврат в буфер
    (`DataPackage`, подавление собственной записи, конверсия файлового drop), журнальные фильтры,
    **tray-иконку/автозапуск** и **single-instance/ACL каталога данных** — весь этот Win32-interop код
    проверен CI только на уровне компиляции, реальное поведение на Windows не подтверждено.

## J. Exact resume point

1. Fresh-read `AGENTS.md`, `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`, этот файл, `docs/ARCHIVE_ROTATION_PROTOCOL.md`, `docs/ARCHIVE_SPLIT_PROTOCOL.md`, `docs/LOCAL_BUILD_AND_TEST.md`.
2. Fresh-check `origin/main`; ожидаемое значение на момент checkpoint — потомок `bbef1326070f126642417835762a7929112e2289`.
3. Для настроек/локализации/оболочки прочитать `ApplicationSettings`, `BuiltInRussianLocalizationService`, `JournalWindow.xaml(.cs)` и `ResidentWindowsHost` перед изменениями.
4. Создать fresh ветку от exact accepted main.
5. Не переделывать заново принятые слайсы ротации, Archive Split, возврата в clipboard, Trash retention, журнала (период/поиск/фильтр), блокировки (ручная + автоблокировка по простою), внешней локализации, оболочки (tray/автозапуск/«О программе») и single-instance/защиты каталога данных.
6. Продуктовые решения из `OPEN_QUESTIONS.md` **уже приняты пользователем** — их надо реализовывать, а не переспрашивать. Исключение: §11 (merge/rebind identity) и числовые лимиты форматов в §6 — там решения нет.
7. `Clipensk.App` и `Clipensk.Windows` не собираются на Linux: перед push отдельно проверять usings нового кода этих проектов, иначе цикл CI тратится на `CS0246`.
8. `Clipensk.Windows` зависит только от `Clipensk.Core`. Типы, которые нужны и платформенному адаптеру, и слою хранения, живут в `Clipensk.Core`; зависимость Windows → Storage не добавлять.

## K. Bootstrap rules

1. Repository state beats this handoff if refs/CI moved.
2. Не повторять принятые Archive Split и Archive Rotation slices.
3. Не трактовать `MaxBytes` как сумму payload bytes.
4. На CI failure сначала получить evidence; «flake» причиной не считать.
5. Локальный PASS не является acceptance evidence.
6. Manual desktop UX остаётся `UNVERIFIED` до фактического manual evidence.

## L. Development environment note

Сетевая политика cloud-окружения переведена на **Custom** с доменами .NET SDK, поэтому локально доступны сборка и прогон `Clipensk.Core.Tests`, `Clipensk.Storage.Tests`, `Clipensk.Infrastructure.Tests` — около 10 секунд против полного CI-цикла. Процедура, ограничения и диагностика — `docs/LOCAL_BUILD_AND_TEST.md`.

Рекомендация по модели для следующего шага:

После 2026-09-20 узкое место сместилось: продуктовые решения приняты, и оставшаяся работа — снова
код. Разбивка по §I:

- пункты §I.1, §I.2, §I.6, §I.8 (дефолты журнала и ротации с валидацией, таргет Windows 11,
  комментарий/экспорт шаблона перевода) — **Sonnet 5, высокая сложность**: работа по уже
  существующим паттернам, без конкурентных инвариантов;
- пункты §I.4, §I.5 (focus restoration, «вставить как plain text») — **Sonnet 5, высокая сложность**:
  Win32/WinRT-интеграция того же класса, что уже сделанные tray и автозапуск;
- пункт §I.7 (MSIX) — **Sonnet 5, высокая сложность**, но затрагивает `.github/**` и схему доставки
  `sqlcipher.dll`, поэтому требует отдельного согласования с пользователем до начала;
- пункт §I.9 (перенос настроек и базы при смене пути) — **Opus, высокая сложность**. Это
  crash-safe перемещение живого зашифрованного хранилища с координацией через
  protected-session/mutation-lease, по цене ошибки прямой аналог Archive Rotation/Split, которые
  делались на Opus. Пользователь явно решил делать это отдельным заходом;
- **ручная проверка на Windows** (§I.10) — не задача для модели: нужен реальный прогон резидентного
  приложения и фиксация evidence пользователем. Особенно важно для tray/автозапуска и для
  single-instance/ACL — это raw Win32 interop (`Shell_NotifyIcon`, `TrackPopupMenuEx`, реестр,
  `FindWindowEx`, `DirectorySecurity`), проверенный CI только на уровне компиляции;
- §I.3 (дефолты форматов) заблокирован отсутствием числовых лимитов — их должен назвать пользователь.
