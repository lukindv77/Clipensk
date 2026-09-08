# Storage Catalog recovery and replacement

Этот документ фиксирует два explicit **pre-session** recovery boundary для rebuildable `Current/storage-catalog.db`:

1. recreation отсутствующего Catalog из authoritative Current + Archive state;
2. replacement существующего damaged/stale Catalog только после полного построения replacement и с сохранением старых bytes в quarantine.

Обычный unlock ничего не ремонтирует автоматически.

## Production boundaries

- `ProtectedStorageCatalogRecoveryService.RecoverMissingCatalogAsync` — только missing-Catalog recreation;
- `ProtectedStorageCatalogReplacementService.ReplaceExistingCatalogAsync` — только explicit replacement существующего Catalog.

Обе операции получают:

- storage root;
- ожидаемый `StorageId`;
- уже выведенный 32-byte MasterKey;
- caller-supplied `currentCalendarDate` для derived archive sealing;
- cancellation token.

`ProtectedStorageDatabaseService.InitializeOrValidateAsync` сохраняет fail-closed semantics. Partial pair по-прежнему возвращает `MissingOrPartialStorage`, invalid Catalog не запускает hidden repair, а recovery/replacement должны быть вызваны явно до создания protected session.

## Authoritative source of truth

Новый Catalog v3 полностью выводится из durable sources:

- Current `DatabaseIdentity` и clipboard history;
- canonical top-level Archive v1 databases и их `DatabaseIdentity`;
- persisted external references `ExternalSha256 + ExternalRelativePath + ExternalSizeBytes`.

Physical `Files/...` objects не являются gate recovery: отсутствие файла не удаляет persisted address metadata и не препятствует Catalog reconstruction.

Catalog сам остаётся rebuildable accelerator. Missing Current не может восстанавливаться из Catalog.

## Source validation

Current проходит:

- keyed SQLite/SQLCipher open;
- `PRAGMA quick_check`;
- exact single-row `DatabaseIdentity` contract;
- expected StorageId/role/schema/encryption version;
- matching `PRAGMA user_version`;
- schema validation для таблиц, доступных в фактической Current version;
- foreign-key validation;
- history metadata validation, если history schema уже существует.

Recovery принимает legacy Current versions, поддерживаемые обычным migration path. Она не мигрирует Current; после publication штатный pair validation/migration path выполняет обычные Current migrations.

Каждый Archive обязан:

- иметь canonical exact `ArchiveFileName`;
- быть Archive v1 того же StorageId;
- иметь непустой unique DatabaseId;
- хранить filename-compatible base/split metadata;
- иметь valid assigned coverage;
- пройти schema/user-version/foreign-key/history-coverage validation.

Duplicate Archive DatabaseId и overlapping coverage завершаются fail-closed до publication.

## Rebuilt Catalog projections

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

## Missing-Catalog recreation

`RecoverMissingCatalogAsync` разрешён только если:

```text
Current/current.db exists
Current/storage-catalog.db missing
```

Если final Catalog уже существует, этот API его не перезаписывает. Если отсутствует Current, operation завершается fail-closed.

### Staging and atomic publication

1. выводится complete source snapshot #1;
2. во временном файле внутри `Current/` создаётся полный Catalog v3;
3. обе projection записываются в staging transaction;
4. staging Catalog полностью валидируется ReadOnly;
5. source snapshot выводится второй раз;
6. snapshot #2 обязан exact совпасть с #1;
7. непосредственно перед publication повторно проверяется, что Current существует, а final Catalog всё ещё отсутствует;
8. staging file атомарно переименовывается в `storage-catalog.db`.

Изменение Current/Archive source state между snapshot passes приводит к failure/retry и не публикует stale Catalog.

Cancellation проверяется до final `File.Move`. После successful atomic move cancellation больше не проверяется: durable validated Catalog считается success.

## Existing-Catalog replacement with quarantine

`ReplaceExistingCatalogAsync` является отдельным explicit API для состояния:

```text
Current/current.db exists
Current/storage-catalog.db exists
```

Существующий Catalog **не используется как trusted source** для построения replacement. Его bytes хэшируются только как TOCTOU evidence и при successful publication сохраняются как quarantine backup.

Replacement не выполняет последовательность `delete old Catalog -> rebuild`. До момента publication существующий Catalog остаётся на final path.

### Shadow recovery reuse

Чтобы не создавать вторую реализацию Current/Archive derivation, replacement переиспользует `ProtectedStorageCatalogRecoveryService`:

1. создаётся temporary shadow root под тем же storage root;
2. shadow `Current/current.db` и canonical Archive filenames представлены alias placeholders;
3. injected routing connection factory разрешает эти aliases только как ReadOnly и направляет opens в реальные authoritative Current/Archive DB;
4. shadow Catalog строится обычным missing-Catalog recovery code;
5. следовательно используются те же identity/schema/history checks, external collision rules, archive overlap checks, double source snapshot и staging validation;
6. shadow `storage-catalog.db` существует полностью построенным и validated **до** изменения real Catalog.

Shadow root расположен под тем же data root, поэтому final filesystem replacement остаётся within-volume.

### Replacement TOCTOU gates

До shadow build запоминаются:

- SHA-256 exact bytes существующего Catalog;
- ordinal set canonical `Archive/archive_*.db` filenames.

После successful shadow recovery и до publication повторно проверяются:

- real Current всё ещё существует;
- real Catalog всё ещё существует;
- archive filename set exact совпадает с исходным;
- SHA-256 real Catalog exact совпадает с исходным.

Current/Archive content changes внутри фиксированного filename set ловятся внутренним double-snapshot recovery. Новый/удалённый Archive после alias snapshot ловится внешним filename-set gate.

### Atomic replacement and quarantine

Перед publication создаётся `Current/CatalogQuarantine/` и unique backup path вида:

```text
Current/CatalogQuarantine/storage-catalog-<utc>-<guid>.db
```

После последнего cancellation check выполняется Windows `File.Replace`:

```text
validated shadow Catalog -> Current/storage-catalog.db
old destination bytes     -> Current/CatalogQuarantine/...
```

Это означает:

- replacement публикуется только после полного build + validation;
- фактический старый destination сохраняется как backup тем же filesystem operation;
- success возвращает relative quarantine path;
- shadow root удаляется best-effort после operation;
- failure cleanup не может демотировать уже опубликованный durable success.

Cancellation не проверяется после successful `File.Replace`: validated replacement уже durable, а старый Catalog сохранён в quarantine.

## Failure boundaries

Recovery/replacement fail closed при:

- wrong StorageId/key/schema/role;
- SQLite quick-check/schema/foreign-key failure;
- malformed Archive identity/coverage;
- duplicate Archive DatabaseId;
- overlapping Archive coverage;
- invalid/conflicting external SHA/path/size metadata;
- source projection change между snapshot passes;
- archive filename layout change during replacement;
- existing Catalog byte change до replacement publication;
- cancellation до publication;
- filesystem publication failure.

Операции не восстанавливают отсутствующий Current и не ремонтируют crypto metadata.

## Explicit non-goals

Эти backend boundaries не реализуют:

- recovery `storage-crypto.json` или MasterKey;
- восстановление отсутствующего `current.db`;
- пользовательский recovery UI / confirmation flow;
- автоматическое решение, когда именно invalid Catalog следует replacement-нуть;
- retention/удаление quarantine backups;
- external-file Trash/GC;
- policy cleanup.

Normal unlock никогда не должен сам удалять или replacement-ить Catalog только из-за validation failure.

## Evidence

Missing-Catalog regression coverage включает:

- full Current + Archive recreation обеих Catalog projections;
- отказ при existing Catalog;
- overlapping Archive coverage;
- conflicting external address metadata;
- cancellation до publication;
- изменение source projection между snapshot passes.

Existing-Catalog replacement regression coverage включает:

- damaged Catalog -> validated Catalog v3 + exact quarantine bytes;
- refusal при missing Catalog;
- cancellation до publication с untouched original;
- deterministic second-Current-snapshot projection change -> fail-closed;
- новый Archive после alias snapshot -> fail-closed.

Manual recovery через production WinUI пока не заявляется как проверенный evidence; CI покрывает storage boundaries через тестовый keyed SQLite factory, а production SQLCipher path подтверждается отдельным обязательным Native SQLCipher workflow на promoted main SHA.
