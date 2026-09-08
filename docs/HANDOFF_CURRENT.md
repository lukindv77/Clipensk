# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-08.

Mutable GitHub state supersedes this file. Перед любой durable записью нужен fresh GitHub TOCTOU.

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
- Manual WinUI/real clipboard/recovery smoke остаётся UNVERIFIED без отдельного evidence.

Canonical main-only `build.yml` blob:

`657f11356566b459dc46f1639b0d4ea7728083f2`

## C. Canonical main

Canonical main:

`a6de1eee53b003a792307f537123d875f5d265eb`

Этот main уже содержит:

- Archive v1 create/validate;
- resumable Current→Archive transfer;
- Catalog v3 `ArchiveSegmentIndex` + derived sealing;
- unified read-only Current+Archive history;
- full external SHA Catalog projection rebuild;
- session-wide mutation lease между capture SHA reservation→Current COMMIT и external-Catalog rebuild;
- explicit pre-session recreation отсутствующего `storage-catalog.db` из authoritative Current + Archive state.

Exact official evidence на `a6de1eee…`:

- Build #191 / run `34179608900` — SUCCESS;
- Native SQLCipher #46 / run `34179608875` — SUCCESS;
- Native #46: pinned SQLCipher x64 build, provenance, encrypted-storage verification, unpackaged publish, production runtime SQLCipher loading и artifact uploads — SUCCESS.

Следовательно missing-Catalog recreation полностью PASS на canonical main.

## D. Active feature

Branch:

`feat/damaged-catalog-replacement`

Base:

`a6de1eee53b003a792307f537123d875f5d265eb`

Owner: **explicit pre-session replacement существующего damaged/stale Catalog только после полного reconstruction, с quarantine backup прежних bytes**.

Temporary `.github/workflows/build.yml` сейчас включает feature branch. Перед final compare он обязан быть восстановлен к canonical blob `657f1135…`.

Последний exact runtime/test SHA до docs:

`6c09f100b6b4a8903b2261031c7b7d3e4f0ce358`

Build #195 / run `34188065858` — SUCCESS:

- x64-only scope — SUCCESS;
- Restore — SUCCESS;
- Release Build — SUCCESS;
- Test — SUCCESS.

После этого SHA меняются только authoritative docs. Последний docs-inclusive head обязан получить отдельный exact Build/Test перед promotion.

## E. Existing-Catalog replacement implemented

`ProtectedStorageCatalogReplacementService.ReplaceExistingCatalogAsync` — explicit **pre-session** operation.

Precondition:

```text
Current/current.db exists
Current/storage-catalog.db exists
```

API получает storage root, expected StorageId, 32-byte MasterKey, explicit `currentCalendarDate` и cancellation token.

Normal unlock не вызывает replacement автоматически. Missing-Catalog recreation и existing-Catalog replacement остаются разными API.

## F. Replacement construction

Старый Catalog не используется как trusted reconstruction source.

Перед build запоминаются:

- SHA-256 exact bytes existing Catalog;
- ordinal canonical Archive filename set.

Создаётся shadow root под storage root. Shadow `current.db`/Archive aliases через `ReadOnlySourceRoutingConnectionFactory` направляются к реальным authoritative Current/Archive DB, а shadow Catalog строится существующим `ProtectedStorageCatalogRecoveryService`.

Таким образом replacement переиспользует тот же проверенный contract:

- Current/Archive identity/schema/history validation;
- external SHA/path/size collision rules;
- Archive DatabaseId/coverage validation;
- overlap rejection;
- derived `IsSealed`;
- full Catalog v3 staging build;
- staging validation;
- double Current+Archive source snapshot equality.

Нет второй независимой SQL implementation recovery.

## G. Pre-publication TOCTOU

После successful shadow recovery и до publication повторно проверяются:

- Current существует;
- existing Catalog существует;
- Archive filename set exact не изменился;
- SHA-256 existing Catalog exact не изменился.

Current/Archive content mutation внутри фиксированного layout ловится внутренним double-snapshot contract; новый/удалённый Archive ловится внешним filename-set gate.

## H. Quarantine and publication

Перед publication создаётся:

`Current/CatalogQuarantine/`

с unique backup filename.

После последнего cancellation check выполняется `File.Replace`:

```text
validated shadow Catalog -> Current/storage-catalog.db
previous destination      -> Current/CatalogQuarantine/...
```

Следовательно:

- старый Catalog не удаляется до готовности replacement;
- actual previous destination bytes сохраняются quarantine backup;
- success возвращает relative quarantine path;
- cancellation после successful replacement не проверяется;
- best-effort shadow cleanup не может демотировать durable success.

Missing Current не восстанавливается. Crypto metadata recovery, user-facing recovery UI и quarantine retention остаются отдельными задачами.

## I. Feature regression history

Build #192:

- Restore/x64 SUCCESS;
- Build failed только из-за missing `using Clipensk.Core.Storage` для `ProtectedStorageDatabaseStatus`;
- исправлено без semantic runtime change.

Build #193 и #194:

- Release Build SUCCESS;
- по одному test-only failure из-за synthetic concurrent write inside connection-open hook, оставлявшего SQLite file handle на teardown;
- production runtime assertion не требовал workaround.

Final runtime regression заменён deterministic connection routing: второй ReadOnly Current snapshot читает заранее подготовленную alternate Current DB с дополнительной external reference, без concurrent filesystem write.

Build #195 на `6c09f100…` полностью SUCCESS.

Regression coverage теперь включает:

- damaged existing Catalog -> valid Catalog v3;
- quarantine exact old bytes;
- normal pair validation после replacement;
- missing Catalog refusal;
- cancellation до publication leaves original untouched;
- deterministic source projection mismatch fail-closed;
- new Archive after alias snapshot fail-closed;
- shadow cleanup.

## J. Immediate resume steps

1. Fetch latest docs-inclusive feature head.
2. Require latest exact feature Build: x64/Restore/Build/Test SUCCESS.
3. Restore `.github/workflows/build.yml` byte-for-byte to `657f11356566b459dc46f1639b0d4ea7728083f2`.
4. Fresh fetch current `main` immediately before promotion.
5. Compare main→feature; require `behind=0`, merge-base=current main, no workflow diff.
6. Fast-forward main with `force:false`.
7. Require official Build on exact promoted SHA.
8. Feature changes `src/Clipensk.Storage/**`, поэтому require exact promoted-SHA Native SQLCipher SUCCESS before PASS.
9. Manual real recovery/WinUI smoke remains UNVERIFIED.

## K. Recommended next work after replacement

Architecture-safe order:

1. external-reference last-reference cleanup + Trash move/GC foundation;
2. policy mutation with required Current/Archive cleanup;
3. quarantine retention/user recovery UI;
4. archive split/repair/migration;
5. unified JournalWindow UI/search/FTS;
6. manual real clipboard/recovery smoke;
7. installer/packaging and final license decisions.

Do not implement policy UPDATE as a Current-only shortcut: cleanup semantics must cover Current + Archive and external last-reference handling.

## Resume rule

Never repeat completed Archive foundation, Current→Archive transfer, Catalog v3, unified history read, external SHA projection rebuild or missing-Catalog recreation if canonical main already contains a newer verified tranche.
