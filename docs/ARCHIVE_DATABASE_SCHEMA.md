# Archive database schema v1

Этот документ фиксирует durable contract для `Archive/archive_*.db`. Archive schema version независима от Current v6 и Catalog v3.

## Scope

Archive v1 — foundation для Current→Archive transfer, unified history query, policy cleanup и catalog rebuild. База self-contained и сохраняет referential history contract.

Catalog v3 уже предоставляет rebuildable archive inventory/sealing projection, но **не меняет Archive v1 schema**. Assigned coverage и DatabaseId остаются authoritative в самой Archive DB. `IsSealed` не записывается в Archive.

## File naming

Допустимы только canonical names из `ArchiveFileName`:

```text
archive_000025.db
archive_000025_0001.db
```

- `ArchiveBaseNumber` — positive six-digit family number;
- unsplit base file имеет `ArchiveSplitSequence = NULL`;
- split file имеет positive sequence, совпадающий с four-digit suffix;
- nested split names не используются;
- filename и persisted base/split обязаны совпадать exact; mismatch fail-closed.

## DatabaseIdentity

Archive v1 использует тот же structural `DatabaseIdentity` envelope, что protected Current/Catalog, но archive-specific fields обязательны:

- `StorageId` совпадает с active `ProtectedStorageSessionLease.StorageId`;
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

Global/application capture policy overlays, `GlobalCapturePolicy`, `CustomBinaryFormatConfiguration`, `ExternalPayloadAddressIndex` и `ArchiveSegmentIndex` в Archive v1 не создаются.

Application identity tables присутствуют потому, что history event `SourceApplicationId` сохраняет существующий FK contract. Transfer копирует только `ApplicationIdentity` rows, на которые реально ссылаются переносимые events. Mutable aliases и policy overlays автоматически не переносятся.

## Coverage ownership

Archive владеет целыми календарными днями. Каждый persisted `ClipboardHistoryEvent.CalendarDate` обязан быть canonical `yyyy-MM-dd` и лежать внутри assigned inclusive coverage.

Coverage является assigned ownership metadata и не должна автоматически сужаться, если maintenance позднее удалит часть rows.

Ни одна Archive DB сама по себе не может доказать отсутствие overlap с соседями. Authoritative cross-archive preflight всегда должен строиться из валидированных Archive identities. Catalog v3 может ускорять discovery/read planning, но его projection не заменяет durable validation Archive DB.

## Catalog v3 projection

`ProtectedArchiveSegmentCatalog.RebuildAsync(currentCalendarDate)` читает Archive v1 только ReadOnly и строит `ArchiveSegmentIndex` из:

- `DatabaseIdentity.DatabaseId`;
- canonical filename;
- assigned `CoverageStartDate..CoverageEndDate`.

До Catalog mutation rebuild требует:

- успешной полной ReadOnly validation каждого discovered Archive;
- unique DatabaseId;
- canonical exact filename;
- отсутствие cross-archive coverage overlap.

`IsSealed` выводится, а не читается из Archive:

```text
Coverage.EndDate < currentCalendarDate
AND в Current нет history rows внутри coverage
```

Поэтому crash-window после Archive COMMIT и до Current purge остаётся unsealed. После purge последующий rebuild может вывести sealed=true. Удаление/rebuild Catalog не требует изменения Archive v1.

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

`ProtectedArchiveDatabaseService.ValidateAsync` открывает существующий canonical archive только ReadOnly и проверяет:

- active protected session / linked caller cancellation;
- SQLCipher/openability;
- `PRAGMA quick_check`;
- exact `DatabaseIdentity` shape;
- exactly one identity row;
- StorageId/role/version/encryption version;
- UTC CreatedAt;
- filename↔base/split consistency;
- mandatory canonical coverage;
- `PRAGMA user_version`;
- ApplicationIdentity schema;
- ClipboardHistory schema;
- `PRAGMA foreign_key_check`;
- persisted CalendarDate values внутри assigned coverage.

Corruption/mismatch завершается fail-closed; validator не ремонтирует archive и не меняет metadata.

## Current → Archive transfer boundary

`ProtectedCurrentToArchiveTransferService.TransferAsync` — explicit maintenance boundary поверх Archive v1. Новую archive schema version он не вводит.

Preconditions:

- target archive уже создан и валиден;
- все canonical Archive DB ReadOnly валидируются, assigned coverage не пересекаются;
- transfer range целиком внутри target coverage;
- переносить можно только завершённые календарные дни;
- active protected session остаётся действующей.

Durable ordering:

1. Current открывается ReadOnly и exact выбранный batch материализуется;
2. referenced ApplicationIdentity + history rows записываются в Archive одной transaction;
3. существующий Archive EventId переиспользуется только при exact совпадении envelope + ordered payload rows;
4. cancellation перед Archive COMMIT;
5. после Archive COMMIT target полностью revalidate-ится ReadOnly;
6. Current открывается ReadWrite только для purge;
7. выбранный range reread/exact-compared внутри purge transaction;
8. concurrent Current change откатывает purge;
9. exact verified events удаляются, payload rows cascade;
10. cancellation непосредственно перед Current COMMIT;
11. successful Current COMMIT не демотируется late cancellation.

Crash/cancellation resumability обеспечивается idempotent replay. Временное `Current=yes, Archive=yes` допустимо; порядок не создаёт `Current=no, Archive=no`.

External payload bytes не копируются и не перемещаются: history rows сохраняют те же `ExternalSha256`, `ExternalRelativePath`, `ExternalSizeBytes`.

После transfer Catalog v3 не изменяется автоматически внутри transfer transaction. Projection rebuild является отдельной maintenance boundary; это избегает превращения rebuildable Catalog в часть authoritative durability ordering.

## Lifecycle and access mode

Archive service использует MasterKey только через `ProtectedStorageSessionLease`.

- lock/dispose session отменяет operation;
- ordinary validation — ReadOnly;
- Archive ReadWrite разрешён только explicit maintenance/transfer boundary;
- generic mutable archive repository не предоставляется.

## Remaining work

После Archive v1 + Current→Archive transfer + Catalog v3 archive projection остаются отдельными tranches:

1. archive history read + unified Current/Archive query;
2. full Catalog external-address rebuild from Current + Archive history;
3. external reference cleanup / Trash last-reference handling;
4. policy mutation with required Current/Archive cleanup;
5. archive split/repair/migration maintenance operations;
6. global maintenance coordination/serialization where required.
