# Storage Catalog file recovery

Этот документ фиксирует explicit recovery boundary для случая, когда защищённый `Current/current.db` существует, а rebuildable `Current/storage-catalog.db` отсутствует.

## Scope

Production implementation: `ProtectedStorageCatalogRecoveryService.RecoverMissingCatalogAsync`.

Recovery является **pre-session** operation. Она получает:

- storage root;
- ожидаемый `StorageId`;
- уже выведенный 32-byte MasterKey;
- caller-supplied `currentCalendarDate` для derived archive sealing;
- cancellation token.

Обычный `ProtectedStorageDatabaseService.InitializeOrValidateAsync` не меняет fail-closed semantics: partial Current/Catalog pair по-прежнему возвращает `MissingOrPartialStorage`. Recovery не запускается автоматически во время unlock.

## Preconditions

Recovery разрешена только если:

1. storage root существует;
2. `Current/current.db` существует;
3. final `Current/storage-catalog.db` отсутствует;
4. Current открывается переданным MasterKey и соответствует ожидаемому StorageId;
5. все discovered Archive databases принадлежат тому же StorageId и проходят authoritative validation.

Если final Catalog уже существует, recovery не перезаписывает и не ремонтирует его. Повреждённый существующий Catalog требует отдельного quarantine/replace workflow.

Если отсутствует Current, Catalog не может считаться источником истины для его восстановления; operation завершается fail-closed.

## Source of truth

Новый Catalog v3 полностью выводится из authoritative durable sources:

- Current `DatabaseIdentity` и clipboard history;
- canonical top-level Archive v1 databases и их `DatabaseIdentity`;
- persisted external references `ExternalSha256 + ExternalRelativePath + ExternalSizeBytes`.

Physical `Files/...` objects не являются gate recovery: их отсутствие не удаляет persisted address metadata и не препятствует Catalog recreation.

## Validation before publication

Current проходит:

- SQLCipher/key probe через configured connection factory;
- `PRAGMA quick_check`;
- exact single-row `DatabaseIdentity` contract;
- expected StorageId/role/schema/encryption version;
- `PRAGMA user_version` match;
- schema validation для таблиц, доступных в фактической Current version;
- foreign-key validation;
- history metadata validation, если history schema уже существует.

Recovery принимает legacy Current versions, поддерживаемые обычным production migration path. Она **не мигрирует Current**; после публикации Catalog штатный `InitializeOrValidateAsync` снова валидирует pair и выполняет обычные Current migrations.

Каждый Archive обязан:

- иметь canonical exact `ArchiveFileName`;
- быть Archive v1 того же StorageId;
- иметь непустой unique DatabaseId;
- хранить filename-compatible base/split metadata;
- иметь valid assigned coverage;
- пройти schema/user-version/foreign-key/history-coverage validation.

Duplicate Archive DatabaseId и overlapping coverage завершаются fail-closed до publication.

## Rebuilt projections

### ExternalPayloadAddressIndex

Projection строится исчерпывающим Current + Archive history scan.

Rules:

- SHA — lowercase 64-hex;
- size неотрицателен;
- `ExternalSizeBytes == CanonicalByteCount`;
- relative path остаётся внутри configured `Files` root;
- exact same SHA/path/size duplicate схлопывается;
- same SHA с другим path/size — failure;
- один path для разных SHA — failure.

### ArchiveSegmentIndex

Для каждого Archive восстанавливаются:

- DatabaseId;
- canonical FileName;
- assigned CoverageStartDate/CoverageEndDate;
- rebuildable `IsSealed`.

Derived rule:

```text
IsSealed = Coverage.EndDate < currentCalendarDate
           AND Current не содержит history row внутри coverage
```

Archive v1 остаётся authoritative; Catalog projection не становится новым source of truth.

## Staging and atomic publication

Recovery никогда не публикует пустой Catalog с намерением «достроить позже».

1. выводится первый complete source snapshot;
2. во временном файле внутри `Current/` создаётся полный Catalog v3;
3. обе projection записываются в staging transaction;
4. staging Catalog полностью валидируется ReadOnly;
5. source snapshot выводится **второй раз**;
6. второй snapshot обязан exact совпасть с первым;
7. непосредственно перед publication повторно проверяется, что Current существует, а final Catalog всё ещё отсутствует;
8. staging file атомарно переименовывается в `storage-catalog.db`.

Изменение Current/Archive source state между snapshot passes приводит к failure/retry и не публикует stale Catalog.

## Cancellation and crash semantics

Cancellation проверяется во время validation/scan/build и непосредственно до final publication.

До `File.Move` cancellation или ошибка оставляет final Catalog отсутствующим; staging file удаляется best-effort в `finally`.

После успешного atomic move cancellation больше не проверяется: полностью построенный и валидированный durable Catalog считается success и не демотируется поздней отменой.

## Explicit non-goals

Этот boundary не реализует:

- автоматический repair повреждённого существующего Catalog;
- quarantine/rename damaged Catalog;
- восстановление отсутствующего Current;
- восстановление `storage-crypto.json` или MasterKey;
- external-file Trash/GC;
- policy cleanup;
- UI recovery flow.

Эти операции требуют собственных explicit safety contracts.

## Evidence

Regression coverage включает:

- полное Current + Archive recreation обеих Catalog projections;
- отказ при existing Catalog;
- overlapping Archive coverage;
- conflicting external address metadata;
- cancellation до publication;
- изменение source state между двумя snapshot passes.

Manual recovery через production WinUI пока не заявляется как проверенный evidence; CI покрывает storage boundary через тестовый keyed SQLite factory, а native production SQLCipher path подтверждается отдельным обязательным main workflow.