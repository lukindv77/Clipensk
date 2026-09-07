# Archive database schema v1

Этот документ фиксирует durable contract для `Archive/archive_*.db`. Archive schema version независима от Current v6 и Catalog v2.

## Scope

Archive v1 — foundation для Current→Archive transfer, unified history query, policy cleanup и catalog rebuild. База self-contained и сохраняет referential history contract. Segment sealing, Catalog archive metadata, unified query и policy cleanup остаются отдельными слоями.

## File naming

Допустимы только canonical names из `ArchiveFileName`:

```text
archive_000025.db
archive_000025_0001.db
```

- `ArchiveBaseNumber` — positive six-digit family number;
- unsplit base file имеет `ArchiveSplitSequence = NULL` в `DatabaseIdentity`;
- split file имеет positive sequence, совпадающий с четырёхзначным suffix;
- nested split names не используются;
- filename и persisted base/split обязаны совпадать exact; mismatch fail-closed.

## DatabaseIdentity

Archive v1 использует тот же structural `DatabaseIdentity` envelope, что protected Current/Catalog, но archive-specific fields обязательны по смыслу:

- `StorageId` — должен совпадать с active `ProtectedStorageSessionLease.StorageId`;
- `DatabaseId` — непустой GUID;
- `DatabaseRole = Archive`;
- `SchemaVersion = 1`;
- `EncryptionVersion = ProtectedStorageDatabaseService.CurrentEncryptionVersion`;
- `CreatedAtUtc` — round-trip UTC timestamp;
- `ArchiveBaseNumber` — mandatory и совпадает с filename;
- `ArchiveSplitSequence` — NULL для base archive либо positive exact split sequence;
- `CoverageStartDate`, `CoverageEndDate` — mandatory canonical `yyyy-MM-dd` inclusive range;
- `CoverageEndDate >= CoverageStartDate`.

`PRAGMA user_version` обязан быть равен Archive schema version.

## Schema contents

Archive v1 содержит:

1. `DatabaseIdentity`;
2. `ApplicationIdentity` + `ApplicationIdentityAlias` schema;
3. `ClipboardHistoryEvent` + `ClipboardHistoryPayload` history schema.

Global/application capture policy overlays, `GlobalCapturePolicy`, `CustomBinaryFormatConfiguration` и Catalog tables в Archive v1 не создаются.

Application identity tables присутствуют потому, что history event `SourceApplicationId` сохраняет существующий FK contract. Archive schema не ослабляет referential integrity ради удобства transfer.

Transfer копирует только `ApplicationIdentity` rows, на которые реально ссылаются переносимые events. `ApplicationIdentityAlias` schema остаётся доступной для self-contained compatibility, но mutable Current aliases и policy overlays автоматически не переносятся.

## Coverage ownership

Archive владеет целыми календарными днями. Каждый persisted `ClipboardHistoryEvent.CalendarDate` обязан:

- быть canonical `yyyy-MM-dd`;
- лежать внутри assigned inclusive coverage.

Validator проверяет distinct persisted calendar dates против `DatabaseIdentity` coverage.

Coverage является assigned ownership metadata и не должна автоматически сужаться, если maintenance позднее удалит часть rows.

Cross-archive non-overlap проверяется более высоким storage/catalog/query layer через `StorageQueryPlanner`; один Archive file сам по себе не может доказать отсутствие overlap с соседями.

## Create boundary

`ProtectedArchiveDatabaseService.CreateAsync` привязан к active `ProtectedStorageSessionLease`.

Создание:

1. проверяет canonical `ArchiveFileName`;
2. создаёт staging database внутри `Archive` directory;
3. записывает archive identity + ApplicationIdentity/history schema одной SQLite transaction;
4. ставит `PRAGMA user_version = 1`;
5. валидирует staging database через ReadOnly open;
6. проверяет cancellation непосредственно перед final move;
7. атомарно перемещает staging file в canonical final name.

Existing final archive не перезаписывается. Если cancellation/failure происходит до final move, staging file удаляется. После successful final move метод не выполняет позднюю cancellation check, чтобы already-published archive не превращался в reported failure.

## Validation boundary

`ProtectedArchiveDatabaseService.ValidateAsync` открывает существующий canonical archive только в `ReadOnly` и проверяет:

- active protected session / linked caller cancellation;
- SQLCipher/openability через configured keyed connection factory;
- `PRAGMA quick_check`;
- exact `DatabaseIdentity` column type/nullability/PK shape;
- exactly one identity row;
- StorageId/role/version/encryption version;
- UTC CreatedAt;
- filename↔base/split consistency;
- mandatory canonical coverage;
- `PRAGMA user_version`;
- ApplicationIdentity schema;
- ClipboardHistory schema;
- `PRAGMA foreign_key_check`;
- all persisted CalendarDate values inside assigned coverage.

Corruption/mismatch завершается fail-closed; validator не ремонтирует archive и не меняет его metadata.

## Current → Archive transfer boundary

`ProtectedCurrentToArchiveTransferService.TransferAsync` — explicit maintenance boundary поверх существующего Archive v1. Новую archive schema version он не вводит.

Preconditions:

- target archive уже создан и проходит `ProtectedArchiveDatabaseService.ValidateAsync`;
- transfer range целиком лежит внутри assigned archive coverage;
- переносить можно только завершённые календарные дни: сегодняшний и будущие `CalendarDate` отклоняются;
- active protected session остаётся действующей всю операцию.

Операция переносит **весь явный календарный range**, а не произвольное число rows внутри дня. Это сохраняет whole-day ownership contract.

Порядок durable фаз:

1. Current открывается ReadOnly, проверяются Current v6 identity/schema/foreign keys, и exact rows выбранного range материализуются;
2. необходимые source `ApplicationIdentity` rows и history event/payload rows записываются в Archive одной transaction;
3. существующий Archive event с тем же `EventId` принимается только если envelope и все ordered payload rows exact совпадают; конфликт завершается fail-closed;
4. cancellation проверяется до Archive COMMIT;
5. после Archive COMMIT target archive заново проходит полный ReadOnly validator;
6. Current открывается ReadWrite только для purge phase;
7. внутри Current transaction тот же range перечитывается и exact сравнивается с ранее скопированным batch;
8. если Current изменился между copy и purge, purge transaction откатывается и caller должен повторить transfer;
9. только exact verified batch удаляется из Current; payload rows удаляются существующим `ON DELETE CASCADE`;
10. cancellation проверяется непосредственно перед Current COMMIT;
11. после successful Current COMMIT late cancellation не превращает уже завершённый durable transfer в reported failure.

Crash/cancellation resumability обеспечивается idempotent durable replay, а не отдельной operation-log table. Если Archive COMMIT уже состоялся, но Current ещё не purged, повторный вызов сравнит существующие Archive rows exact и продолжит verify→purge. Временное состояние `Current=yes, Archive=yes` допустимо. Состояние `Current=no, Archive=no` этот порядок не создаёт.

External payload bytes не копируются и не перемещаются: history rows сохраняют те же `ExternalSha256`, `ExternalRelativePath` и `ExternalSizeBytes`. Catalog/Files остаются отдельным content-address layer.

Этот transfer boundary пока не:

- помечает segment sealed;
- изменяет archive coverage;
- обновляет Catalog archive metadata;
- удаляет orphan ApplicationIdentity rows;
- выполняет unified Current+Archive query;
- выполняет policy cleanup или Trash GC.

## Lifecycle and access mode

Archive service использует MasterKey только через `ProtectedStorageSessionLease`. Password/MasterKey не получают нового persistence path.

- lock/dispose session отменяет archive operation;
- ordinary validation — ReadOnly;
- Archive ReadWrite разрешён только explicit maintenance/transfer boundary;
- generic mutable archive repository не предоставляется.

## Not implemented yet

После Archive v1 + Current→Archive transfer остаются отдельными tranches:

1. persisted/derived segment sealing and Catalog archive metadata;
2. archive history read + unified Current/Archive query;
3. Catalog rebuild from Current + Archive;
4. external reference cleanup/Trash last-reference handling;
5. policy mutation with required Current/Archive cleanup;
6. archive split/repair/migration maintenance operations.
