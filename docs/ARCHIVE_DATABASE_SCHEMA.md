# Archive database schema v1

Этот документ фиксирует первый durable contract для `Archive/archive_*.db`. Archive schema version независима от Current v6 и Catalog v2.

## Scope

Archive v1 — foundation для будущих Current→Archive transfer, unified history query, policy cleanup и catalog rebuild. Текущий tranche создаёт и валидирует self-contained archive database, но **не** переносит туда события, не удаляет Current rows, не объявляет segment sealed и не обновляет Catalog.

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

Будущий transfer coordinator должен копировать только те identity rows, которые необходимы архивируемой истории. Наличие таблиц не означает автоматическое копирование всех mutable Current aliases/overlays.

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

## Lifecycle and access mode

Archive service использует MasterKey только через `ProtectedStorageSessionLease`. Password/MasterKey не получают нового persistence path.

- lock/dispose session отменяет archive operation;
- ordinary validation — ReadOnly;
- Archive ReadWrite будет разрешён только будущим maintenance/transfer boundary;
- current foundation не предоставляет generic mutable archive repository.

## Not implemented yet

После Archive v1 foundation остаются отдельными tranches:

1. resumable Current→Archive transfer: write → verify → purge Current;
2. persisted/derived segment sealing and catalog metadata;
3. archive history read + unified Current/Archive query;
4. Catalog rebuild from Current + Archive;
5. external reference cleanup/Trash last-reference handling;
6. policy mutation with required Current/Archive cleanup;
7. archive split/repair/migration maintenance operations.
