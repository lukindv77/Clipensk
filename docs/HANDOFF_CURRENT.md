# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-20 (журнал получил период по умолчанию, поиск и фильтр по приложению; остаток — прочие настройки, локализация, оболочка и продуктовые решения).

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

Последний промотированный main: `f28dd6915864a11a2209199acc35df15c6626721`.

Exact-main CI для `f28dd691…`:

- Build #492, run `35492278210` — **SUCCESS**;
- Native SQLCipher #112, run `35492278242` — **SUCCESS**, включая pinned SQLCipher x64 build/provenance, `Verify encrypted storage x64` и `Verify published Clipensk x64 runtime SQLCipher loading`.

Baseline **ACCEPTED**: Archive Rotation, возврат записи в clipboard, автоудаление Trash по сроку и период/поиск/фильтр журнала реализованы.

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

Storage 595, Core 246, Infrastructure 47 — все зелёные локально и в CI.

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

Продуктовые решения — не код, решение пользователя:

1. Product defaults ротации (`OPEN_QUESTIONS.md` §8) — до него ротация opt-in.
2. Период журнала по умолчанию (`OPEN_QUESTIONS.md` §7) — механизм готов, конкретное число не выбрано.
3. Лицензия не выбрана, файла `LICENSE` нет; схема распространения не выбрана.

По продукту в целом (подробности — в оценке готовности к релизу):

4. Настройки: автоблокировка (`AutoLockEnabled` объявлено, не используется), загрузка внешних переводов.
5. Локализация: внешние файлы и папка `Languages` (`REQUIREMENTS.md` §20) — есть только `BuiltInRussianLocalizationService`.
6. Страница «О программе» — заглушка.
7. Tray-иконка и автозапуск отсутствуют.
8. Manual production WinUI/storage smoke evidence (отдельно, остаётся `UNVERIFIED`). Покрывает возврат в буфер (`DataPackage`, подавление собственной записи, конверсия файлового drop) и журнальные фильтры.

## J. Exact resume point

1. Fresh-read `AGENTS.md`, `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`, этот файл, `docs/ARCHIVE_ROTATION_PROTOCOL.md`, `docs/ARCHIVE_SPLIT_PROTOCOL.md`, `docs/LOCAL_BUILD_AND_TEST.md`.
2. Fresh-check `origin/main`; ожидаемое значение на момент checkpoint — потомок `f28dd6915864a11a2209199acc35df15c6626721`.
3. Для настроек/локализации/оболочки прочитать `ApplicationSettings`, `BuiltInRussianLocalizationService`, `JournalWindow.xaml(.cs)` и `ResidentWindowsHost` перед изменениями.
4. Создать fresh ветку от exact accepted main.
5. Не переделывать заново принятые слайсы ротации, Archive Split, возврата в clipboard, Trash retention и журнала (период/поиск/фильтр).
6. Продуктовые решения (§I пункты 1–3) не принимать самостоятельно — это выбор пользователя.
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

Пунктов уровня Opus/High в оставшейся работе больше нет: направления с durable-повреждением и с физическим удалением данных закрыты. Журнал (период/поиск/фильтр) тоже закрыт.

- **ручная проверка на Windows** — не задача для модели: нужен реальный прогон и фиксация evidence пользователем;
- настройки (автоблокировка, внешняя локализация), страница «О программе», tray и автозапуск — **Sonnet 5, высокая сложность**: объёмная работа по существующим репозиториям, без конкурентных инвариантов;
- продуктовые решения (лицензия, дефолты, период журнала, схема распространения) — **Sonnet 5, средняя сложность**.
