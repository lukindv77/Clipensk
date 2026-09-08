# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-08.

Этот файл — operational checkpoint. Mutable GitHub state всегда важнее текста handoff. Перед любой durable записью нужен fresh TOCTOU GitHub.

## A. Project identity

Clipensk — Open Source resident Windows clipboard-history manager.

- repository: `lukindv77/Clipensk`;
- canonical branch: `main`;
- platform: Windows x64/AMD64 only; ARM64 вне product scope;
- stack: C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected storage: SQLCipher, один MasterKey на storage;
- production schemas: **Current v6 / Catalog v3 / Archive v1**.

Ключевые storage docs: `CURRENT_DATABASE_SCHEMA.md`, `CLIPBOARD_HISTORY_SCHEMA.md`, `STORAGE_CATALOG_SCHEMA.md`, `EXTERNAL_PAYLOAD_CATALOG_REBUILD.md`, `STORAGE_CATALOG_RECOVERY.md`, `ARCHIVE_DATABASE_SCHEMA.md`.

## B. Workflow rules

- GitHub `main` + exact Actions evidence — source of truth.
- Code changes идут через feature branch.
- Перед `main` update: fresh main + exact feature, compare, `behind=0`, merge-base=current main; fast-forward только `force:false`.
- PASS только после exact-SHA official Build и, для storage/runtime changes, Native SQLCipher evidence на `main`.
- Failure-driven fixes only.
- Temporary feature Build trigger обязан быть восстановлен byte-for-byte до final compare.
- Whole storage pair validation before migration mutation.
- Cancellation before COMMIT/publication; committed durable success не демотируется late cancellation.
- Manual WinUI/real clipboard smoke остаётся UNVERIFIED без отдельного evidence.

## C. Last canonical main before active recovery feature

Canonical main:

`50efc0ccca95a99f71d075217635f8fbba76fceb`

Этот main уже содержит:

- Archive v1 create/validate;
- resumable Current→Archive transfer;
- Catalog v3 `ArchiveSegmentIndex` + derived sealing;
- unified read-only Current+Archive history;
- full external SHA Catalog projection rebuild;
- session-wide mutation lease между capture SHA reservation→Current COMMIT и external-Catalog rebuild.

Exact official evidence на `50efc0cc…`:

- Build #185 / run `34145266628` — SUCCESS;
- Native SQLCipher #45 / run `34145266718` — SUCCESS;
- Native #45: pinned x64 build, provenance, encrypted-storage verification, unpackaged publish, production runtime SQLCipher loading и artifact uploads — SUCCESS.

Следовательно external-payload Catalog rebuild tranche полностью PASS на canonical main.

## D. Active feature

Branch:

`feat/storage-catalog-file-recovery`

Base:

`50efc0ccca95a99f71d075217635f8fbba76fceb`

Owner: **explicit pre-session recreation отсутствующего `Current/storage-catalog.db` из authoritative Current + Archive state**.

Temporary `.github/workflows/build.yml` включает feature branch для Windows CI. Canonical main-only blob:

`657f11356566b459dc46f1639b0d4ea7728083f2`

обязан быть восстановлен перед final compare.

Последний exact runtime/test SHA до docs:

`1d7070e320592398f52884739010256dc2966e86`

Build #186 / run `34146358793` — SUCCESS:

- x64-only scope — SUCCESS;
- Restore — SUCCESS;
- Release Build — SUCCESS;
- Test — SUCCESS.

После этого SHA меняются только recovery docs. Последний docs-inclusive head должен получить отдельный exact Build/Test перед promotion.

## E. Recovery boundary implemented

`ProtectedStorageCatalogRecoveryService.RecoverMissingCatalogAsync` — explicit **pre-session** operation.

Inputs:

- storage root;
- expected StorageId;
- 32-byte MasterKey;
- explicit `currentCalendarDate`;
- cancellation token.

Recovery разрешена только для состояния:

```text
Current/current.db exists
Current/storage-catalog.db missing
```

Normal `ProtectedStorageDatabaseService.InitializeOrValidateAsync` не меняет semantics: partial pair остаётся `MissingOrPartialStorage`; никакого hidden auto-repair на unlock нет.

Existing Catalog не перезаписывается. Missing Current не восстанавливается из accelerator metadata.

## F. Authoritative validation

Current валидируется до recovery publication:

- configured keyed SQLite/SQLCipher open;
- quick_check;
- exact single-row DatabaseIdentity;
- expected StorageId/Current role/encryption/schema version;
- matching `PRAGMA user_version`;
- schema tables, доступные для фактической Current v1..v6;
- foreign keys;
- history metadata, если Current имеет history schema.

Recovery не мигрирует legacy Current. После успешной Catalog recreation обычный pair validation/migration path выполняет штатные Current migrations.

Все top-level `Archive/archive_*.db`:

- обязаны иметь canonical exact `ArchiveFileName`;
- Archive v1 / same StorageId;
- unique non-empty DatabaseId;
- filename-compatible base/split identity;
- valid assigned coverage;
- valid user_version/schema/foreign keys/history coverage.

Duplicate DatabaseId и overlapping Archive coverage — fail-closed.

## G. Rebuilt Catalog v3 contents

### ExternalPayloadAddressIndex

Строится из persisted Current + Archive history references:

```text
ExternalSha256 + ExternalRelativePath + ExternalSizeBytes
```

Rules сохраняются из production rebuild:

- lowercase 64-hex SHA;
- non-negative size;
- exact size == CanonicalByteCount;
- relative path внутри Files root;
- exact duplicate collapse;
- same SHA with different path/size — fail-closed;
- same path for different SHA — fail-closed.

Physical `Files/...` object может отсутствовать: history metadata остаётся authoritative для Catalog recreation.

### ArchiveSegmentIndex

Для каждого Archive выводятся DatabaseId, canonical filename, assigned coverage и rebuildable `IsSealed`.

```text
IsSealed = Coverage.EndDate < currentCalendarDate
           AND Current не содержит history row внутри coverage
```

Archive v1 остаётся authoritative source; Catalog не становится sole owner metadata.

## H. Staging / publication semantics

Recovery никогда не публикует пустой Catalog, который предполагается достроить позже.

1. Derive complete Current+Archive snapshot #1.
2. Создать полный Catalog v3 во staging file внутри `Current/`.
3. Записать обе projections одной staging transaction.
4. Полностью валидировать staging Catalog.
5. Derive complete source snapshot #2.
6. Snapshot #2 обязан exact совпасть с #1.
7. Перед publication повторно проверить: Current всё ещё существует, final Catalog всё ещё отсутствует.
8. Atomic `File.Move(staging, storage-catalog.db)`.

Source change между двумя passes приводит к failure/retry, final Catalog остаётся отсутствующим.

Cancellation разрешена до atomic publication. После successful move cancellation не проверяется: durable validated Catalog считается success.

## I. Regression coverage

Build #186 покрывает:

- full Current + Archive recovery обеих Catalog projections;
- отказ при existing Catalog;
- overlapping Archive coverage;
- conflicting external address metadata;
- cancellation до publication;
- source mutation между snapshot passes.

## J. Explicit non-goals / remaining recovery work

Этот feature **не** реализует:

- repair/quarantine повреждённого существующего Catalog;
- восстановление missing Current;
- recovery `storage-crypto.json`/MasterKey;
- UI recovery flow;
- external-file Trash/GC;
- policy cleanup.

Нельзя автоматически удалять/перезаписывать существующий Catalog только потому, что normal unlock сообщил invalid identity/database: damaged-Catalog quarantine требует отдельного explicit workflow.

## K. Immediate resume steps

1. Fetch latest feature head after docs.
2. Require latest docs-inclusive feature Build exact SHA: x64/Restore/Build/Test SUCCESS.
3. Restore `.github/workflows/build.yml` byte-for-byte to `657f11356566b459dc46f1639b0d4ea7728083f2`.
4. Fresh fetch current `main` immediately before promotion.
5. Compare main→feature; require `behind=0`, merge-base=current main, no workflow diff.
6. Fast-forward main with `force:false`.
7. Require official Build on exact promoted SHA.
8. Because feature changes protected storage code, require exact-SHA Native SQLCipher SUCCESS before PASS.
9. Manual real recovery/WinUI smoke остаётся UNVERIFIED.

## L. Recommended next work after recovery

Architecture-safe order:

1. damaged existing Catalog quarantine + explicit replace/recovery coordinator;
2. external-reference last-reference cleanup + Trash GC;
3. policy mutation with required Current/Archive cleanup;
4. archive split/repair/migration;
5. unified JournalWindow UI/search/FTS;
6. manual real clipboard/recovery smoke;
7. installer/packaging and final license decisions.

Do not implement policy UPDATE as a Current-only shortcut: cleanup semantics must cover Current + Archive and external last-reference handling.

## Resume rule

Mutable GitHub state supersedes this file. Never repeat completed Archive foundation, Current→Archive transfer, Catalog v3, unified history read, external SHA projection rebuild, or missing-Catalog recreation if canonical main already contains a newer verified tranche.
