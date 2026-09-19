# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-19 (Archive Rotation завершена end to end, включая Settings и ручной триггер; следующий блокер — возврат данных в clipboard).

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

Последний промотированный main: `5cc4d34139afb7985bd187a9273c510792fcd708`.

Exact-SHA CI для `5cc4d341…`:

- Build #474, run `35460149755` (push в `main`) — **SUCCESS**;
- Native SQLCipher #97, run `35458569265` (`workflow_dispatch` на том же SHA) — **SUCCESS**, включая pinned SQLCipher x64 build/provenance, `Verify encrypted storage x64` и `Verify published Clipensk x64 runtime SQLCipher loading`.

Baseline **ACCEPTED**: Archive Rotation завершена целиком.

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

Следующее направление — **возврат выбранной записи в clipboard**, главный блокер релиза (`REQUIREMENTS.md` §1).

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

Storage 544, Core 218, Infrastructure 44 — все зелёные локально и в CI.

## F. Load-bearing design decisions

- Пороговое правило Any/All определено **один раз** в `ArchiveRotationSettings.HasReachedThresholds`; конфигурация с `MaxBytes` без измеренного размера — fail-closed.
- Построение Archive v1 shadow, exact source→shadow сверка и полная валидация Archive живут **один раз** в `ArchiveShadowWriter`, общем со Split.
- Purge не имеет собственной реализации переноса.
- **Всякий держатель mutation lease обязан использовать `…InTransaction`-помощники репозитория**, а не его lease-берущие точки входа: повторный вход в lease даёт дедлок. На этом уже один раз наступили.
- Recovery из `Planned` пересобирает shadow и **обязан воспроизвести плановый физический размер** — это evidence, с которым publication сверяет опубликованные копии.
- `currentLocalDate` везде передаётся параметром, а не берётся из системных часов.

## G. Important invariants

- Перед durable write: fresh target ref, `AGENTS.md`, relevant files.
- Перед promotion: fresh `main`, feature ref, compare; `behind_by=0`; merge-base = текущий main; review полного net diff; `.github/` в net diff отсутствует.
- `main` только fast-forward, `force=false`.
- Изменения в `src/Clipensk.Storage/**` или `src/Clipensk.Core/Storage/**` требуют exact-main Build **и** Native SQLCipher.
- **Concurrency-группа Native задана по ref**, поэтому push в `main` отменяет выполняющийся Native предыдущего SHA. Промотировать строго по одному слайсу за гейт.
- Ротация не делит `CalendarDate`; purge только после подтверждённой durable публикации; Catalog — rebuildable projection; коллизия канонического имени — fail-closed; pending rotation и pending Split взаимно исключаются.

## H. Known risks / unresolved questions

- Manual production WinUI smoke — **UNVERIFIED** и для Split, и для Rotation.
- Startup/runtime integration, scheduler/manual trigger, Settings UI — **не реализованы**.
- Локальные тесты идут на `e_sqlite3`, а не SQLCipher: шифрование, native provenance и published-runtime loading локально не проверяются.
- SDK не переживает пересоздание сессии; процедура — `docs/LOCAL_BUILD_AND_TEST.md`.

## I. Remaining work

По направлению Archive Rotation осталось только:

1. Product defaults ротации (`OPEN_QUESTIONS.md` §8) — решение пользователя, не код; до него ротация opt-in.

По продукту в целом (подробности — в оценке готовности к релизу):

2. **Возврат выбранной записи в clipboard** — заявленное назначение продукта (`REQUIREMENTS.md` §1), не реализовано. Главный блокер релиза.
3. Журнал: поиск, период по умолчанию, фильтр по приложению-источнику и приложению вызова.
4. Настройки: автоблокировка (`AutoLockEnabled` объявлено, не используется), срок хранения Trash (`TrashRetentionDays` объявлено, не используется, автоудаления нет), загрузка внешних переводов.
5. Локализация: внешние файлы и папка `Languages` (`REQUIREMENTS.md` §20) — есть только `BuiltInRussianLocalizationService`.
6. Страница «О программе» — заглушка.
7. Tray-иконка и автозапуск отсутствуют.
8. Лицензия не выбрана, файла `LICENSE` нет; схема распространения не выбрана.
9. Manual production WinUI/storage smoke evidence (отдельно, остаётся `UNVERIFIED`).

## J. Exact resume point

1. Fresh-read `AGENTS.md`, `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`, этот файл, `docs/ARCHIVE_ROTATION_PROTOCOL.md`, `docs/ARCHIVE_SPLIT_PROTOCOL.md`, `docs/LOCAL_BUILD_AND_TEST.md`.
2. Fresh-check `origin/main`; ожидаемое значение на момент checkpoint — потомок `5cc4d34139afb7985bd187a9273c510792fcd708`.
3. Для следующего направления прочитать `ExternalPayloadStore`, `ProtectedExternalPayloadTrashCollector` (контракт containment/reparse/SHA), `ClipboardHistoryEntry` и `JournalWindow.Journal.cs`.
4. Создать fresh ветку от exact accepted main.
5. Не переделывать заново принятые слайсы 1–14 ротации и Archive Split.
6. Продуктовые решения (§I пункты 1, 8) не принимать самостоятельно — это выбор пользователя.
7. `Clipensk.App` не собирается на Linux: перед push проверять usings нового App-кода отдельно, иначе цикл CI тратится на `CS0246`.

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

- возврат данных в clipboard — **Opus 5, effort High**: требует построить отсутствующий read-путь внешних payload с проверкой SHA и containment, затем восстановление `DataPackage`, которое автотестами не проверяется;
- продуктовые решения (лицензия, дефолты, схема распространения) — **Sonnet 5, средняя сложность**: выбор из уже сформулированных вариантов;
- журнал (поиск, период, фильтры) и остальные настройки — **Sonnet 5, высокая сложность**: объёмная, но не конкурентная работа.
