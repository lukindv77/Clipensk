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

## Read boundary

`ReadAsync`:

- открывает Catalog ReadOnly;
- проверяет active storage identity, Catalog schema/user version, v2 external-payload contract и v3 archive-segment contract;
- materializes `ArchiveSegmentDescriptor` в coverage order;
- повторно reject-ит overlap fail-closed.

Catalog row с malformed GUID, filename, date range или sealing value не принимается.

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

Потеря `storage-catalog.db` не должна означать потерю критической metadata. Для archive inventory существует production rebuild projection из валидных Archive + Current state. Полный rebuild external SHA index из Current + Archive history остаётся отдельной maintenance capability.

## Custom binary extension

Catalog schema не выбирает extension нового custom binary payload. Current v6 хранит storage-scoped `FormatName -> FileExtension`; Catalog сохраняет уже выбранный SHA/address. Для существующего SHA extension provider повторно не требуется.
