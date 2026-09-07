# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-07.

Этот файл — operational checkpoint. Mutable GitHub state всегда важнее текста handoff. Перед любой durable записью нужен fresh TOCTOU GitHub.

## A. Project identity

Clipensk — Open Source resident Windows clipboard-history manager.

- repository: `lukindv77/Clipensk`;
- canonical branch: `main`;
- platform: Windows x64/AMD64 only; ARM64 вне product scope;
- stack: C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected storage: SQLCipher, один MasterKey на storage;
- production schemas: **Current v6 / Catalog v3 / Archive v1**.

Ключевые документы: `AGENTS.md`, `docs/REQUIREMENTS.md`, `docs/ARCHITECTURE.md`, `docs/CURRENT_DATABASE_SCHEMA.md`, `docs/CLIPBOARD_HISTORY_SCHEMA.md`, `docs/STORAGE_CATALOG_SCHEMA.md`, `docs/ARCHIVE_DATABASE_SCHEMA.md`, `docs/GLOBAL_CAPTURE_POLICY.md`, `docs/CUSTOM_BINARY_FORMAT_CONFIGURATION.md`, `docs/PROTECTED_CLIPBOARD_DELIVERY_COMPOSITION.md`, `docs/CLIPBOARD_WORKER_LIFECYCLE.md`, `docs/OPEN_QUESTIONS.md`.

## B. Workflow rules

- GitHub `main` + exact Actions evidence — source of truth.
- Code changes идут через feature branch.
- Перед `main` update: fresh main + exact feature, compare, `behind=0`, merge-base=current main; только fast-forward `force:false`.
- PASS только после exact-SHA official GitHub Actions evidence на `main`.
- Failure-driven fixes only.
- x64 only.
- Whole storage pair validation before migration mutation.
- Cancellation before COMMIT; committed success не превращать в late cancellation failure.
- Temporary feature Build trigger обязан быть восстановлен byte-for-byte до final compare.
- Policy mutation нельзя делать без cleanup semantics.
- Manual WinUI/real clipboard smoke остаётся UNVERIFIED без evidence.

## C. Last fully verified canonical main

Canonical main at start of the active unified-read tranche:

`1faf91ce07cf6497e19381d0ee13061e95e3e5c2`

Этот main уже содержит:

- Archive v1 create/validate;
- resumable Current→Archive transfer;
- Catalog v3 `ArchiveSegmentIndex` + rebuild/derived sealing;
- Current v6 / Catalog v3 schema docs.

Exact official Actions on `1faf91ce…`:

- Build #171 / run `34133966584` — SUCCESS;
- Native SQLCipher #43 / run `34133966625` — SUCCESS, включая pinned x64 build, provenance, encrypted-storage verification, unpackaged publish и runtime SQLCipher loading.

## D. Active feature

Branch:

`feat/unified-current-archive-history`

Base:

`1faf91ce07cf6497e19381d0ee13061e95e3e5c2`

Current owner: **read-only unified Current + Archive history**. Capture/write path, Archive schema и Catalog mutation semantics в этот tranche не входят.

Temporary `.github/workflows/build.yml` trigger сейчас включает feature branch только для Windows feature CI. Canonical main-only workflow blob должен быть восстановлен перед final compare.

Latest runtime/test SHA with full green feature evidence:

`4256d5b53fa0b77e83a48f04c63804cbcf2c90e0`

Build #174 / run `34140406038` — SUCCESS:

- x64-only scope — SUCCESS;
- Restore — SUCCESS;
- Release Build — SUCCESS;
- Test — SUCCESS.

Build #172 compiled successfully but initially had 4 failures только в новых unified tests: test helper сохранял `EventUtc` как `+00:00`, тогда как production reader требует canonical UTC `DateTime` round-trip representation. Helper исправлен на `DateTimeKind.Utc` + `ToString("O")`.

Во время review дополнительно найден exact-duplicate edge: `DateTimeOffset.Equals` не является достаточной проверкой persisted offset. Runtime comparison усилен до explicit `UtcTimestamp + Offset + exact WindowsTimeZoneId`; regression с одинаковым UTC instant и другим persisted offset входит в green #174.

После `4256d5b5…` меняются только authoritative docs; финальный docs-inclusive feature SHA должен получить отдельный Build/Test перед promotion.

## E. Unified history contracts implemented

Core read-side добавляет:

- `IUnifiedClipboardHistoryRepository`;
- `UnifiedClipboardHistoryEntry`;
- `ClipboardHistoryPhysicalLocationKind`;
- `ClipboardHistoryPhysicalLocation`;
- существующий `ClipboardHistoryCursor` теперь документирован как global logical UTC/EventId cursor, без DB-specific state.

`ProtectedClipboardHistoryServices.Create` сохраняет прежние `HistorySink` и Current-only `HistoryRepository`, а также инертно compose-ит новый `UnifiedHistoryRepository`. Creation не открывает БД и не вызывает custom extension provider.

## F. Archive physical reader

`SqliteArchiveClipboardHistoryRepository` — internal ReadOnly boundary для одного planned `ArchiveSegmentDescriptor`.

Он:

1. требует canonical filename и non-empty DatabaseId;
2. full-validates Archive v1 через `ProtectedArchiveDatabaseService.ValidateAsync` до read;
3. сверяет exact planned DatabaseId/coverage с authoritative Archive identity;
4. открывает выбранную Archive DB ReadOnly;
5. читает complete events тем же SQL/keyset order, что Current: `EventUtc DESC, EventId BINARY DESC`, payload order ASC;
6. проверяет event-time/source/payload/external-reference metadata;
7. full-validates Archive повторно после page read;
8. объединяет caller cancellation с protected-session cancellation.

External bytes не читаются; generic mutable archive repository не создаётся.

## G. Unified query algorithm

`ProtectedUnifiedClipboardHistoryRepository` выполняет:

1. Catalog v3 `ArchiveSegmentIndex` read;
2. exact comparison Catalog filenames с top-level canonical `Archive/archive_*.db` filename set;
3. ReadOnly Current MIN/MAX CalendarDate для `CurrentStoreDescriptor`;
4. `StorageQueryPlanner.Build` для requested `JournalDateRange`;
5. Current page read **до** Archive page reads;
6. открытие только selected Archive segments;
7. logical merge по `EventUtc DESC, canonical EventId DESC`;
8. exact duplicate collapse по EventId;
9. global `limit` по logical events;
10. повторный Catalog + physical filename stability check перед return.

Current-first physical order является intentional safety rule относительно maintenance order `Archive COMMIT -> Archive verify -> Current purge`: конкурентный transfer не создаёт логический miss. Если Current ещё существует, событие видно там; если Current уже purged, Archive COMMIT уже состоялся.

## H. Duplicate and physical-location semantics

Crash-window `Current=yes, Archive=yes` возвращается одной `UnifiedClipboardHistoryEntry` с двумя physical locations.

Exact duplicate требует совпадения:

- EventId;
- UTC timestamp;
- persisted offset;
- exact WindowsTimeZoneId;
- durable SourceApplicationId;
- runtime source snapshot;
- exact ordered payload records, включая external reference metadata.

Любое расхождение одного EventId между physical DB fail-closed. Никакого Current-wins/Archive-wins нет.

Current location не хранит Archive identity. Archive location хранит exact DatabaseId + FileName.

## I. Snapshot/concurrency semantics

Unified page не является одной multi-file SQLite transaction. Каждый physical DB read имеет отдельный ReadOnly snapshot.

Без глобального MaintenanceCoordinator API не обещает point-in-time snapshot всей storage pair. Корректность поддерживаемого Current→Archive lifecycle обеспечивается:

- Archive-first transfer durability;
- Current-first unified read order;
- exact duplicate verification;
- Archive ValidateAsync до/после selected read;
- Catalog/directory layout stability checks до и после merge.

Stale Catalog filename set fail-closed и требует rebuild; unindexed Archive DB не открывается silently.

## J. Regression coverage

Green feature tests покрывают:

- Current-only read + Current location;
- temporary transfer duplicate -> one logical row + Current/Archive locations;
- global Current+Archive order and keyset continuation;
- only requested-period Archive DB opens for history rows;
- stale Catalog fails before opening unindexed Archive;
- conflicting payload duplicate fails closed;
- same UTC instant with different persisted offset fails closed;
- caller cancellation before work returns no partial page and opens no DB;
- protected history composition remains inert;
- caller cancellation / lock / session dispose reject unified reads before DB open.

## K. Invariants to preserve

- `WM_CLIPBOARDUPDATE` signal/enqueue only.
- Capture only under active unlocked protected access.
- Password never persisted.
- Current/Catalog/Archive share one MasterKey.
- no silent identity merge or invented custom extension/size defaults.
- external payload addresses resolved before history transaction.
- Catalog first stored path wins for exact SHA.
- archive ownership is whole calendar days; assigned coverage does not overlap.
- Current→Archive ordering: Archive write -> verify -> Current purge.
- temporary duplicate allowed; missing both copies forbidden.
- Catalog is rebuildable accelerator, not sole authority.
- committed success is not demoted by late cancellation.

## L. Immediate resume steps

1. Fetch latest feature head and latest docs-inclusive feature Build.
2. Require exact-head x64/Restore/Build/Test all SUCCESS.
3. Restore `.github/workflows/build.yml` byte-for-byte to canonical main-only blob `657f11356566b459dc46f1639b0d4ea7728083f2`.
4. Fresh fetch `main` immediately before promotion.
5. Compare main→feature; require `behind=0`, merge-base=current main and no workflow diff.
6. Fast-forward main with `force:false` only.
7. Require official Build on exact promoted SHA.
8. Feature changes `src/Clipensk.Storage/**`, поэтому Native SQLCipher workflow должен trigger; require exact promoted-SHA Native SUCCESS before PASS.
9. Manual real WinUI/clipboard smoke remains UNVERIFIED.

## M. Remaining major work after unified read

Recommended architecture-safe order:

1. full external SHA Catalog rebuild from Current + Archive history and/or shared maintenance coordination foundation;
2. external-reference last-reference cleanup + Trash GC;
3. policy mutation with required Current/Archive cleanup;
4. archive split/repair/migration;
5. unified JournalWindow UI/search/FTS;
6. manual real clipboard/WinUI smoke;
7. installer/packaging and final license decisions.

Do not implement policy UPDATE as a Current-only shortcut: requirements demand cleanup across Current/Archive and external-reference last-reference semantics.

## Resume rule

Mutable GitHub state supersedes this file. Never repeat completed Archive foundation, Current→Archive transfer, Catalog v3 or unified-read work if canonical main already contains a newer verified tranche.
