# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-07.

Этот файл — operational checkpoint. Mutable GitHub state всегда важнее текста handoff. Перед любой durable записью нужен fresh TOCTOU GitHub.

## A. Project identity

Clipensk — Open Source resident Windows clipboard-history manager.

- repository: `lukindv77/Clipensk`;
- canonical branch: `main`;
- platform: Windows x64/AMD64 only; ARM64 не поддерживается;
- stack: C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected storage: SQLCipher, один MasterKey на storage;
- target production schemas after active feature: **Current v6 / Catalog v3 / Archive v1**.

Ключевые документы: `AGENTS.md`, `docs/REQUIREMENTS.md`, `docs/ARCHITECTURE.md`, `docs/CURRENT_DATABASE_SCHEMA.md`, `docs/CLIPBOARD_HISTORY_SCHEMA.md`, `docs/STORAGE_CATALOG_SCHEMA.md`, `docs/ARCHIVE_DATABASE_SCHEMA.md`, `docs/GLOBAL_CAPTURE_POLICY.md`, `docs/CUSTOM_BINARY_FORMAT_CONFIGURATION.md`, `docs/PROTECTED_CLIPBOARD_DELIVERY_COMPOSITION.md`, `docs/CLIPBOARD_WORKER_LIFECYCLE.md`, `docs/OPEN_QUESTIONS.md`.

## B. Workflow rules

- GitHub `main` + exact Actions evidence — source of truth.
- Code changes идут через feature branch.
- Перед `main` update: fresh main + feature, compare, `behind=0`, merge-base=current main; только fast-forward `force:false`.
- PASS только после exact-SHA official GitHub Actions evidence на `main`.
- Failure-driven fixes only.
- x64 only.
- Whole storage pair validation before migration mutation.
- Cancellation before COMMIT; committed success не превращать в late cancellation failure.
- Policy mutation нельзя делать без cleanup semantics.
- Manual WinUI/real clipboard smoke остаётся UNVERIFIED без evidence.

## C. Canonical main before Catalog v3 promotion

Fresh main at the start of the active Catalog v3 tranche:

`02f3807e97169173f124360475258397118bc6da`

Это main уже содержит Archive v1 + resumable Current→Archive transfer и восстановленный canonical main-only Build trigger.

Exact Actions on this SHA:

- Build #170 / run `34126866491` — SUCCESS;
- Native SQLCipher #42 / run `34126866567` — SUCCESS.

## D. Active feature

Branch:

`feat/catalog-v3-archive-segments`

Base:

`02f3807e97169173f124360475258397118bc6da`

Runtime/test implementation commit:

`d30460a1824437b27fb77aea2131b5b4a86f4972`

Message: `feat: index archive segments in catalog v3`.

Feature CI evidence before cleanup/docs:

Catalog v3 Feature #3 / run `34131323668` — SUCCESS:

- bootstrap implementation apply — SUCCESS;
- stale Catalog expectation compatibility fix — SUCCESS;
- x64-only scope — SUCCESS;
- Restore — SUCCESS;
- Release Build — SUCCESS;
- Test — SUCCESS;
- validated `src/tests` commit — SUCCESS.

The first feature run compiled successfully and failed only because two pre-existing tests still hardcoded latest Catalog version `2`; 372/374 tests passed. Updating those stale expectations to the production `CatalogSchemaVersion` made the full run green. Do not classify that first failure as a runtime regression.

After `d30460a…`, temporary feature workflow/bootstrap scripts were removed and schema/handoff docs synchronized. Those cleanup/docs commits do not change validated `src/tests`, but they do move the feature head; final docs-inclusive evidence must therefore come from official workflows on the promoted exact main SHA.

## E. Completed product foundations

Already completed; do not repeat:

- resident clipboard signal/enqueue pipeline;
- WinRT readers and exact MaxBytes semantics;
- prohibited Wave/Riff/virtual-file-content guards;
- durable Clipensk-owned ApplicationId;
- Current history write/read/keyset continuation;
- Catalog v2 external content-address index;
- Current v6 global policy + custom-binary extension configuration;
- atomic initial policy/config setup and WinUI setup surface;
- protected delivery composition and app worker lifecycle;
- Archive v1 create/validate;
- resumable Current→Archive whole-calendar-day transfer.

## F. Catalog v3 implementation

`ProtectedStorageDatabaseService.CatalogSchemaVersion = 3`.

New storage pair creates:

- Current v6;
- Catalog v3 with existing `ExternalPayloadAddressIndex` plus new `ArchiveSegmentIndex`.

Migration is resumable and independent:

- Catalog v1 → v2 creates external-payload index;
- Catalog v2 → v3 validates the v2 contract, then creates archive-segment table/indexes and advances schema/user version in its own transaction;
- v1 does not skip directly to v3;
- whole Current/Catalog pair validation still precedes mutation.

`ArchiveSegmentIndex` projection stores:

- Archive `DatabaseId`;
- canonical `FileName`;
- assigned inclusive coverage;
- derived `IsSealed`.

Catalog remains rebuildable and is not authoritative for Archive identity/coverage.

## G. Archive inventory/rebuild boundary

`ProtectedArchiveSegmentCatalog` is active-session bound and exposes `ReadAsync` / `RebuildAsync`.

Rebuild preflight before Catalog mutation:

1. enumerate top-level `Archive/archive_*.db`;
2. require canonical exact filenames;
3. fully validate each Archive v1 ReadOnly;
4. reject duplicate DatabaseId;
5. require assigned coverage;
6. reject any cross-archive coverage overlap.

Only after successful discovery/preflight does it read Current and replace Catalog projection in one Catalog transaction.

Derived sealing rule:

```text
IsSealed = Coverage.EndDate < currentCalendarDate
           AND Current contains no ClipboardHistoryEvent inside coverage
```

This deliberately keeps the normal transfer crash window unsealed: after Archive COMMIT but before Current purge, Current still has rows. After purge a later rebuild may derive sealed=true.

`IsSealed` is not stored in Archive v1 and is not an authoritative mutable sealing bit.

Read boundary validates Catalog identity/schema/user version, v2 external index, v3 archive table/index shape and row values; malformed rows or overlap fail closed.

## H. Regression coverage added

Tests cover at least:

- new storage creates latest Catalog v3;
- Catalog v2→v3 migration preserves external payload index rows;
- Catalog v1 resumes through v2→v3;
- malformed v2 prevents mutation;
- archive rebuild discovers validated segments;
- sealing true only for past coverage with no Current rows;
- pending Current rows keep segment unsealed;
- overlapping archive coverage fails before Catalog replacement;
- cancellation prevents replacement/commit;
- Catalog read materializes persisted projection.

## I. Invariants to preserve

- `WM_CLIPBOARDUPDATE` remains signal/enqueue only.
- Capture requires active unlocked protected access.
- Password never persisted.
- Current/Catalog/Archive share one MasterKey.
- no silent identity merge or invented custom extension/size defaults.
- external payload addresses resolved before history transaction.
- Catalog first stored path wins for exact SHA.
- archive ownership is whole calendar days; assigned coverages may not overlap.
- Current→Archive ordering is Archive write → verify → Current purge.
- temporary duplicate is safe; missing both copies is forbidden.
- Catalog archive projection is rebuildable from Archive + Current state.
- Archive v1 coverage does not shrink merely because rows are cleaned.
- committed success is not demoted by late cancellation.

## J. Immediate resume steps

1. Fresh fetch feature head and `main` after this handoff commit.
2. Compare `main`→feature and inspect exact files.
3. Require `behind=0` and merge-base exact current main.
4. Confirm no temporary Catalog-v3 CI/bootstrap files remain in final diff.
5. Fast-forward `main` with `force:false` only.
6. Fetch official Build and Native SQLCipher runs for the exact promoted main SHA.
7. Declare Catalog v3 tranche PASS only after required steps in both workflows are SUCCESS.
8. Next code tranche: Archive history read + unified Current/Archive journal query path. Do not combine policy cleanup or Trash GC into that tranche.

## K. Remaining major work

- unified Current + Archive history query/read planning;
- full external SHA Catalog rebuild from Current + Archive history;
- external reference last-reference cleanup / Trash GC;
- policy mutation with required Current/Archive cleanup;
- archive split/repair/migration;
- global maintenance coordination where required;
- FTS/unified search completion;
- manual real clipboard/WinUI smoke;
- installer/packaging decision;
- final Open Source license selection.

## Resume rule

Mutable GitHub state supersedes this file. Never repeat completed Archive foundation, Current→Archive transfer, or Catalog v3 inventory work if canonical main already contains a newer verified tranche.
