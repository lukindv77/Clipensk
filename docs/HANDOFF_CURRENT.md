# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-18 (Archive Rotation durable marker tranche).

Mutable GitHub state is authoritative and supersedes this file. Перед любой durable repository write обязательна fresh TOCTOU-проверка relevant refs/files; перед promotion — fresh `main`, feature ref, compare и canonical workflows.

## A. Project identity

Clipensk — resident Windows clipboard-history manager.

- repository / source of truth: `lukindv77/Clipensk`;
- canonical branch: `main`;
- Windows x64/AMD64 only; ARM64 вне scope;
- C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected SQLite uses SQLCipher; один MasterKey на storage;
- **Current schema v10**, Storage Catalog schema v3, Archive schema v1;
- обязательные правила: `AGENTS.md`, `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`, `docs/CI_LOG_ACCESS.md`, `docs/ARCHIVE_SPLIT_PROTOCOL.md`, `docs/ARCHIVE_ROTATION_PROTOCOL.md`.

## B. User intent

Пользователь ожидает автономное продолжение разработки без повторения завершённых этапов и без лишних уточняющих вопросов.

Постоянные требования:

- promotion в `main` только fast-forward, `force=false`, без merge commit;
- PASS/build/release/promotion только по exact-SHA CI evidence;
- при Actions failure сначала connector logs/artifacts, затем `docs/CI_LOG_ACCESS.md`; причину не угадывать;
- временные feature triggers/diagnostics перед promotion восстанавливать byte-for-byte к fresh canonical workflow;
- manual production WinUI UX/smoke не считать проверенным без отдельного durable evidence;
- основной язык взаимодействия и комментариев с пользователем — **русский**;
- после каждой задачи сообщать рекомендуемую модель и сложность рассуждений (`AGENTS.md`).

## C. Current authoritative state

Последний полностью принятый main product baseline:

`e9dcd575996a284778b11df6ad30c480f838383c`

Exact-main CI для `e9dcd57…`:

- Build #445, run `35365996480` — **SUCCESS**;
- Native SQLCipher #83, run `35365996498` — **IN PROGRESS на момент подготовки checkpoint, результат НЕ ПОДТВЕРЖДЁН**.

**Первое действие нового чата:** проверить conclusion run `35365996498`. Пока он не SUCCESS, baseline `e9dcd57…` считать **NOT ACCEPTED**: tranche трогает `src/Clipensk.Storage/**` и `src/Clipensk.Core/Storage/**`, а такие изменения требуют обоих exact-main workflow. При failure — сначала connector logs/artifacts и `docs/CI_LOG_ACCESS.md`, причину не угадывать, и только затем fix-ветка от текущего main.

Canonical Build workflow blob: `6bb0e8b8eda657c082738a64a4ba80acd857daf4`

Предыдущий baseline `8f28c8fb187e20b85ef906b7f75a1ba2179e837e` (Build #442 / Native #82) остаётся историческим и больше не является актуальным main.

Рабочая ветка `claude/affectionate-brahmagupta-vtgfm9` была продвинута в `main` fast-forward и после этого перезапущена от `origin/main`. PR #2 закрыт этим promotion. Новая работа — с fresh ветки от exact accepted main.

## D. Current owner / active task

Archive Rotation остаётся активным engineering tranche.

Immediate next slice — **slice 4 из `docs/ARCHIVE_ROTATION_PROTOCOL.md` §15**: rotation source scanner + shadow builder.

Он включает:

- сканирование closed Current days и их record-метрик;
- построение Archive v1 shadow в hidden staging directory операции;
- измерение фактического физического размера закрытого валидированного shadow `.db`;
- exact source→shadow cross-check по §8 протокола;
- детерминированное выделение base-номеров по §4 (`max + 1`, `SplitSequence = 0`, fail-closed при исчерпании).

## E. What has been completed

Archive Split завершён и не подлежит повторной реализации без evidence-driven причины (Current v9 pending split schema/repository/migration, pure split planner, shadow builder, copy-first publisher, Catalog publisher, recovery coordinator, atomic start service, Maintenance UI, startup pre-runtime split recovery). Детали — в `ARCHIVE_SPLIT_PROTOCOL.md`.

Archive Rotation принято в `main`:

1. `ArchiveRotationSettings` с `MaxRecordCount`, `MaxBytes`, `MaxCalendarDays`;
2. explicit `ArchiveRotationThresholdMode.Any | All`;
3. JSON persistence/validation;
4. Archive Rotation protocol (`docs/ARCHIVE_ROTATION_PROTOCOL.md`);
5. corrected pure `ArchiveRotationPlanner`: ready ranges + open tail, post-day `>=` semantics, fail-closed на `MaxBytes`;
6. **Current schema v10 + durable pending-rotation marker** (этот tranche).

### Что именно добавил slice v10

- `PendingArchiveRotationSqlSchema` — таблицы `PendingArchiveRotation` (singleton; `OperationId`, policy snapshot `MaxRecordCount`/`MaxBytes`/`MaxCalendarDays`/`ThresholdMode`, `Phase` 0..4, `CreatedAtUtc`, table-level CHECK на наличие хотя бы одного threshold) и `PendingArchiveRotationTarget` (`SegmentOrder`, canonical unsplit `FileName`, planned `DatabaseId`, coverage, `ExpectedRecordCount >= 0`, `ShadowPhysicalSizeBytes > 0`, FK `ON DELETE CASCADE`).
- `PendingArchiveRotationOperation` / `PendingArchiveRotationTarget` records + `ArchiveRotationPhase` (`Planned → ReadyToPublish → PhysicalPublished → SourcePurged → CatalogPublished`).
- `SqlitePendingArchiveRotationRepository` — Read/Start/AdvancePhase/ClearCompleted под mutation lease; валидация Current identity/schema v10, policy snapshot, canonical unsplit имён, строго возрастающих base numbers, непрерывности coverage по целым `CalendarDate`, single-step phase advance, clear только после `CatalogPublished`.
- Взаимная блокировка pending rotation ↔ pending split в обе стороны (`HasPendingOperation` в обеих schema-классах); split-сторона учитывает, что на Current v9 rotation-таблицы физически нет.
- Миграция `v9 → v10`, создание таблиц при инициализации новой storage pair, разведение validation gates: split-контракт от v9, rotation-контракт от v10.
- Побочный fix: `ProtectedStorageCatalogRecoveryService` принимал Current версии `[1..5, CurrentSchemaVersion]`, из-за чего каждый bump молча лишал recovery предыдущих версий (6,7,8,9 уже были потеряны до этого tranche). Заменено на полный диапазон `1..CurrentSchemaVersion`; custom-binary contract теперь валидируется от своей реальной минимальной версии 6. Без этого bump до v10 сломал бы catalog recovery для существующих v9-хранилищ.
- Тесты: `ProtectedStorageCurrentSchemaV10MigrationTests` (миграция с сохранением v9-состояния, invalid catalog, SQL-failure rollback+retry, cancellation rollback+retry) и `SqlitePendingArchiveRotationRepositoryTests` (14 сценариев: immutability плана, отказ второй операции, skip/stale phase, clear до финальной фазы, разрыв coverage, split-суффикс, невозрастающие base numbers, отсутствие shadow-size evidence, пустой план, policy без explicit mode, physical-size-only policy, zero-record ready range, взаимная блокировка со split, потеря target-строки, отказ на Current v9).
- Документация: `CURRENT_DATABASE_SCHEMA.md` (раздел Current v10, миграция v9→v10, fail-closed список, repository boundaries), `ARCHIVE_ROTATION_PROTOCOL.md` §5/§15, `ARCHITECTURE.md`.

## F. Current Archive Rotation conclusions

Зафиксированная модель (полностью — в `ARCHIVE_ROTATION_PROTOCOL.md`):

- rotation по record count, физическому размеру Archive DB и/или calendar span;
- границы только по целым `CalendarDate`; один день неделим;
- physical size = фактическая длина закрытого валидированного SQLCipher Archive `.db`, а не сумма logical payload bytes;
- threshold оценивается после полного дня, предикат достигнут при `metric >= threshold`;
- ANY/ALL — это policy; последний недостигший сегмент остаётся open tail в Current;
- zero-record даты внутри candidate window входят в span и coverage, поэтому ready range может иметь `ExpectedRecordCount = 0`;
- rotation создаёт только новые unsplit base-файлы; split-суффиксы зарезервированы за Archive Split;
- policy snapshot в marker — то, что исполняет recovery; перепланирование по текущим настройкам запрещено;
- финальный source purge должен reuse/refactor существующую Current→Archive compare/purge логику, а не создавать параллельную реализацию.

## G. Important invariants

- Before every durable repository write: fresh target branch/ref, fresh `AGENTS.md`, fresh relevant files/refs.
- Before promotion: fresh `main`, fresh feature ref, fresh compare; require `behind_by=0`; merge-base must equal current `main`; review full net diff.
- `main` update только fast-forward, `force=false`, без merge commit.
- Temporary workflow changes восстанавливать byte-for-byte до promotion; проверять, что в net diff нет `.github/`.
- Любой promotion, трогающий `src/Clipensk.Storage/**` или `src/Clipensk.Core/Storage/**`, требует exact-main Build **и** Native SQLCipher.
- No automatic rotation may split a `CalendarDate`.
- No source purge before validated Archive durability.
- Assigned Archive coverage авторитетна в Archive DB и не расширяется in-place.
- Unexpected canonical Archive filename/DatabaseId collision — fail-closed.
- Pending rotation блокирует конфликтующие history/archive mutations; pending rotation и pending split взаимно исключаются.
- Catalog — rebuildable projection, не operation journal.
- Manual WinUI smoke независим от CI.

## H. Known risks / unresolved questions

- Manual production WinUI Archive Split smoke остаётся **UNVERIFIED**.
- Manual production WinUI smoke для Archive Rotation — **UNVERIFIED** (функциональности ещё нет).
- Storage-backed physical-size shadow planning, deterministic base-name allocation, planned DatabaseId creation, copy-first publication, source purge integration, Catalog publication, recovery coordinator, startup integration, scheduler и Settings UI — **не реализованы**.
- Существующий `ProtectedCurrentToArchiveTransferService` сам берёт mutation lease; rotation coordinator обязан отрефакторить/выставить lease-aware exact compare/purge core, а не вкладывать повторный захват lease.
- Локальные тесты идут на `e_sqlite3`, а не на SQLCipher: поведение шифрования, native provenance и published-runtime loading локально не проверяются.

## I. Remaining work

Порядок по `ARCHIVE_ROTATION_PROTOCOL.md` §15:

1. **Slice 4** — rotation source scanner + shadow builder (следующая задача).
2. Slice 5 — copy-first publication с retained staging backup и exact planned final validation.
3. Slice 6 — lease-aware transfer integration (reuse exact compare/purge без вложенного lease).
4. Slice 7 — Catalog publication + recovery coordinator + atomic start service.
5. Slice 8 — startup/runtime integration: recovery pending rotation до возобновления clipboard capture.
6. Slice 9 — scheduler/manual trigger + Settings UI для thresholds/mode.
7. Slice 10 — manual production WinUI/storage smoke evidence (остаётся отдельным, статус `UNVERIFIED`).

## J. Exact resume point

1. Fresh-read `AGENTS.md`, `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`, `docs/HANDOFF_CURRENT.md`, `docs/ARCHIVE_SPLIT_PROTOCOL.md`, `docs/ARCHIVE_ROTATION_PROTOCOL.md`, `docs/CURRENT_DATABASE_SCHEMA.md`, `docs/LOCAL_BUILD_AND_TEST.md`.
2. Fresh-check `origin/main`; ожидаемое значение на момент checkpoint — `e9dcd575996a284778b11df6ad30c480f838383c` или его потомок.
3. Прочитать реальный код перед изменениями: `src/Clipensk.Core/Storage/ArchiveRotationPlanner.cs`, `src/Clipensk.Storage/Databases/PendingArchiveRotationSqlSchema.cs`, `SqlitePendingArchiveRotationRepository.cs`, `ProtectedArchiveSplitShadowBuilder.cs`, `ProtectedArchiveDatabaseService.cs`, `ProtectedCurrentToArchiveTransferService.cs`.
4. Создать fresh ветку от exact accepted main и начать slice 4.
5. Не начинать заново pure planner correction и Current v10 marker — они приняты.

## K. Bootstrap rules

1. Repository state beats this handoff if refs/CI moved.
2. Не повторять принятые Archive Split и Archive Rotation slices 1-3.
3. Не трактовать `MaxBytes` как сумму payload bytes.
4. На CI failure сначала получить evidence, потом чинить; «flake» причиной не считать.
5. Manual desktop UX статус остаётся `UNVERIFIED` до фактического manual evidence.
6. Локальный PASS не является acceptance evidence; принятие только по exact-SHA CI.

## L. Development environment note

Сетевая политика cloud-окружения переведена на **Custom** с доменами SDK .NET, поэтому в сессии доступна локальная сборка и прогон `Clipensk.Core.Tests`, `Clipensk.Storage.Tests`, `Clipensk.Infrastructure.Tests`. Процедура, ограничения и диагностика — в `docs/LOCAL_BUILD_AND_TEST.md`.

На момент checkpoint локально на `e9dcd57…` прошли 726 тестов (481 + 201 + 44), что совпадает с числом тестов в Windows CI.

SDK в контейнере не сохраняется между сессиями: новая сессия ставит его заново по инструкции из `LOCAL_BUILD_AND_TEST.md`. Если окружение будет пересоздано, сетевую политику нужно настроить снова.
