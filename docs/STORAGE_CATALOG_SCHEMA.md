# Storage catalog schema evolution

`storage-catalog.db` является rebuildable accelerator и картой физического хранилища. Критическая информация не должна существовать только в каталоге.

## Version ownership

- `storage-catalog.db` v1: только `DatabaseIdentity`;
- `storage-catalog.db` v2: добавляет rebuildable индекс адресов внешних clipboard payload;
- `storage-catalog.db` v3: добавляет rebuildable projection архивных сегментов.

Версия Catalog независима от `current.db` и Archive schema versions. Текущий production bootstrap создаёт protected pair как **Current v6 / Catalog v3**. Archive остаётся **v1**.

## Catalog v2 — external payload address index

`ExternalPayloadAddressIndex` хранит ускоряющее отображение:

```text
SHA-256(exact stored bytes) -> RelativePath + SizeBytes
```

Поля:

- `Sha256` — lowercase 64-character SHA-256, primary key;
- `RelativePath` — persisted путь внутри `Files`, unique;
- `SizeBytes` — размер exact stored bytes, неотрицательный.

Дата первого физического размещения входит в canonical relative path `YYYY-MM-DD/<sha>.<extension>`. Глобальный ключ — SHA-256 exact stored bytes, независимо от source application, clipboard format name, capture date и физической history DB.

`SqliteExternalPayloadAddressIndex.GetOrAdd(candidate)` транзакционно сохраняет первый address для нового SHA. Для уже известного SHA возвращается первый persisted address. Тот же SHA с другим `SizeBytes` или collision одного `RelativePath` между разными SHA завершается fail-closed.

## Catalog v3 — archive segment projection

Catalog v3 добавляет `ArchiveSegmentIndex`:

```text
DatabaseId -> FileName + CoverageStartDate + CoverageEndDate + IsSealed
```

Поля:

- `DatabaseId TEXT NOT NULL PRIMARY KEY` — durable `DatabaseIdentity.DatabaseId` Archive DB;
- `FileName TEXT NOT NULL` — canonical `ArchiveFileName`, unique;
- `CoverageStartDate TEXT NOT NULL` — canonical `yyyy-MM-dd`;
- `CoverageEndDate TEXT NOT NULL` — canonical `yyyy-MM-dd`, не раньше start;
- `IsSealed INTEGER NOT NULL` — только `0` или `1`.

Индексы:

- unique `UX_ArchiveSegmentIndex_FileName(FileName)`;
- `IX_ArchiveSegmentIndex_Coverage(CoverageStartDate, CoverageEndDate)`.

`ArchiveSegmentIndex` не является authoritative metadata. Источником истины для DatabaseId/filename/coverage остаются сами Archive v1 databases и их `DatabaseIdentity`. `IsSealed` также не хранится в Archive и является только rebuildable derived projection.

## Segment discovery and rebuild

`ProtectedArchiveSegmentCatalog` работает только через active `ProtectedStorageSessionLease`.

`RebuildAsync(currentCalendarDate)` выполняет следующие фазы:

1. перечисляет только `Archive/archive_*.db` верхнего уровня;
2. требует canonical exact `ArchiveFileName`;
3. каждый Archive полностью валидируется ReadOnly через `ProtectedArchiveDatabaseService`;
4. duplicate `DatabaseId` между файлами завершается fail-closed;
5. обязательные assigned coverage берутся из Archive `DatabaseIdentity`;
6. все discovered coverage проверяются на отсутствие overlap **до** Catalog mutation;
7. Current открывается ReadOnly и для каждого segment проверяется наличие ещё не purged `ClipboardHistoryEvent` внутри его coverage;
8. derived `IsSealed` вычисляется;
9. Catalog открывается ReadWrite только после успешного discovery/preflight;
10. вся `ArchiveSegmentIndex` projection заменяется одной transaction;
11. cancellation проверяется непосредственно перед COMMIT.

Если discovery/validation/overlap/cancellation завершается ошибкой до Catalog transaction, существующая projection не меняется. Если Catalog transaction не commit-ится, прежняя projection остаётся durable.

### Derived sealing rule

Для текущего `currentCalendarDate`:

```text
IsSealed = Coverage.EndDate < currentCalendarDate
           AND Current не содержит ни одной history row внутри coverage
```

Это специально учитывает safe crash window Current→Archive transfer. После Archive COMMIT, но до Current purge, данные временно присутствуют в обеих DB, поэтому segment остаётся unsealed. После успешного purge следующий rebuild может вывести `IsSealed = true` для полностью прошлого coverage.

`IsSealed` не изменяет Archive v1, не запрещает explicit maintenance write сам по себе и не заменяет повторную authoritative Archive validation.

## External payload projection rebuild

`ProtectedExternalPayloadCatalogRebuildService` восстанавливает `ExternalPayloadAddressIndex` из durable external references в Current + всех canonical Archive v1.

Rebuild намеренно не использует paged unified journal API. Он выполняет исчерпывающий physical scan:

1. Current ReadOnly первым;
2. canonical Archive ReadOnly в ordinal filename order;
3. полный identity/schema/history preflight;
4. collision validation для SHA/path/size;
5. Catalog ReadWrite только после successful preflight;
6. replacement только `ExternalPayloadAddressIndex` одной transaction;
7. cancellation непосредственно перед COMMIT.

Current-first scan совместим с существующим Current→Archive durable order `Archive COMMIT -> verify -> Current purge`: если reference уже исчез из Current, его Archive copy обязана быть durable до последующего Archive scan; если Current scan произошёл раньше purge, reference уже присутствует в projection.

Exact одинаковый `SHA + RelativePath + SizeBytes` из Current/Archive схлопывается. Один SHA с другим path/size либо один relative path для разных SHA завершается fail-closed до Catalog mutation.

Physical `Files/...` не является source of truth для rebuild: файл может отсутствовать, но persisted history address остаётся authoritative metadata. Rebuild не читает file bytes, не пересчитывает SHA, не перемещает файлы в Trash и не меняет `ArchiveSegmentIndex`. Stale Catalog reservations без history references удаляются из rebuilt projection; orphan physical-file cleanup относится к отдельному GC contract.

Подробный concurrency/recovery contract: `EXTERNAL_PAYLOAD_CATALOG_REBUILD.md`.

### Mutation coordination

`ProtectedStorageSessionLease.AcquireMutationLeaseAsync` предоставляет session-wide exclusive mutation gate.

Capture history sink удерживает его от момента **до external SHA reservation** до successful Current history COMMIT. External payload Catalog rebuild удерживает тот же lease от начала scan до Catalog replacement. Это исключает race, в котором rebuild удалил бы свежую SHA reservation до появления соответствующей durable Current row.

Caller cancellation и protected-session cancellation отменяют ожидание/операцию. Поздний release lease после lock безопасен и idempotent.

Поддерживаемый Current→Archive transfer не обязан участвовать в этом gate для external projection correctness, поскольку archive-first durable order + Current-first rebuild scan уже предотвращают logical miss. Будущие maintenance writes, удаляющие или переписывающие durable external references, обязаны использовать этот gate либо иметь отдельно доказанный эквивалентный ordering contract.

## Read boundary

`ProtectedArchiveSegmentCatalog.ReadAsync`:

- открывает Catalog ReadOnly;
- проверяет active storage identity, Catalog schema/user version, v2 external-payload contract и v3 archive-segment contract;
- materializes `ArchiveSegmentDescriptor` в coverage order;
- повторно reject-ит overlap fail-closed.

Catalog row с malformed GUID, filename, date range или sealing value не принимается.

## Unified history read consumer

`ProtectedUnifiedClipboardHistoryRepository` использует Catalog v3 projection как **discovery/read-planning accelerator**, а не как замену Archive validation.

Перед physical history reads:

1. Catalog v3 materializes полный descriptor set;
2. top-level canonical `Archive/archive_*.db` filename set перечисляется без открытия самих Archive DB;
3. physical filename set обязан exact совпадать с Catalog projection; новый/unindexed, missing или иначе stale archive layout завершается fail-closed и требует rebuild;
4. `StorageQueryPlanner` выбирает только segments, coverage которых пересекает requested period;
5. только выбранные Archive databases открываются для history rows;
6. каждый выбранный Archive до и после чтения authoritative-валидируется через `ProtectedArchiveDatabaseService.ValidateAsync`, включая exact planned DatabaseId/coverage.

После logical merge Catalog projection и physical filename set проверяются повторно. Изменение descriptor metadata или filename layout во время unified query не возвращает silently incomplete page: caller получает failure/retry boundary.

Таким образом Catalog ускоряет selection и позволяет не открывать все Archive для каждого journal page, но critical identity/coverage выбранного файла по-прежнему подтверждаются самой Archive DB. Поддерживаемые сегодня maintenance operations не изменяют assigned coverage существующего Archive in-place; будущие repair/split/coverage-mutation операции обязаны определить coordinator/rebuild ordering до использования unified reader.

## Initialization and migration

Новая storage pair создаётся как:

- `current.db` v6;
- `storage-catalog.db` v3 с `ExternalPayloadAddressIndex` и `ArchiveSegmentIndex`.

Current и Catalog имеют независимые schema versions. Для существующей pair обе БД сначала валидируются в допустимых входных версиях; whole-pair validation предшествует mutation.

### Catalog v1 → v2

Отдельная transaction:

1. создать `ExternalPayloadAddressIndex`;
2. `DatabaseIdentity.SchemaVersion: 1 -> 2` только для ожидаемого StorageCatalog/StorageId;
3. `PRAGMA user_version = 2`;
4. cancellation check;
5. COMMIT.

### Catalog v2 → v3

Отдельная transaction:

1. валидировать существующий v2 `ExternalPayloadAddressIndex` contract;
2. создать пустую `ArchiveSegmentIndex` + required indexes;
3. `DatabaseIdentity.SchemaVersion: 2 -> 3` только для ожидаемого StorageCatalog/StorageId;
4. `PRAGMA user_version = 3`;
5. cancellation check;
6. COMMIT.

Каждый migration boundary resumable. Ошибка до COMMIT оставляет полноценную предыдущую schema version. v1 не перепрыгивает прямо в v3: шаги выполняются `v1 -> v2 -> v3`.

После migration pair повторно валидируется как **Current v6 / Catalog v3**.

## Source of truth and recovery

Catalog остаётся rebuildable. Исторические payload rows сохраняют `ExternalSha256`, `ExternalRelativePath`, `ExternalSizeBytes`; Archive DB сами хранят `DatabaseId` и assigned coverage.

Production recovery имеет четыре отдельных уровня:

1. `ProtectedArchiveSegmentCatalog.RebuildAsync` перестраивает `ArchiveSegmentIndex` внутри существующего валидного Catalog v3;
2. `ProtectedExternalPayloadCatalogRebuildService.RebuildAsync` перестраивает `ExternalPayloadAddressIndex` внутри существующего валидного Catalog v3;
3. `ProtectedStorageCatalogRecoveryService.RecoverMissingCatalogAsync` **pre-session** создаёт отсутствующий `storage-catalog.db` целиком из authoritative Current + Archive state;
4. `ProtectedStorageCatalogReplacementService.ReplaceExistingCatalogAsync` **pre-session** строит validated replacement существующего Catalog из тех же authoritative sources и затем атомарно replacement-ит final Catalog с quarantine backup старых bytes.

Missing-Catalog recreation разрешена только при существующем Current и отсутствующем final Catalog. Existing-Catalog replacement, наоборот, требует существующего Current и существующего Catalog. Эти API намеренно разделены.

Обычный unlock/`InitializeOrValidateAsync` остаётся fail-closed и не запускает ни recreation, ни replacement автоматически.

Missing-Catalog recovery строит полный Catalog v3 во staging file, валидирует его, повторно выводит весь Current+Archive source snapshot и требует exact совпадения, после чего атомарно публикует staging через `File.Move`.

Existing-Catalog replacement переиспользует тот же recovery builder через shadow root с ReadOnly routing к реальным Current/Archive. До publication повторно проверяются archive filename set и SHA-256 existing Catalog. Fully validated shadow Catalog затем публикуется через Windows `File.Replace`, а фактические bytes прежнего Catalog сохраняются под `Current/CatalogQuarantine/`.

Cancellation проверяется до publication; после successful `File.Move`/`File.Replace` durable result не демотируется.

Physical external files не требуются для восстановления address metadata. Missing Current не восстанавливается из Catalog. Recovery `storage-crypto.json`/MasterKey и user-facing recovery UI остаются отдельными задачами.

Полный contract: `STORAGE_CATALOG_RECOVERY.md`.

## Custom binary extension

Catalog schema не выбирает extension нового custom binary payload. Current v6 хранит storage-scoped `FormatName -> FileExtension`; Catalog сохраняет уже выбранный SHA/address. Для существующего SHA extension provider повторно не требуется.
