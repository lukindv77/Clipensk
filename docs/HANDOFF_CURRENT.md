# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-13.

Mutable GitHub state is authoritative and supersedes this file. Перед любой durable repository write обязателен fresh GitHub TOCTOU relevant refs/files; перед promotion — fresh `main`, feature ref, compare и canonical workflows.

## A. Project identity and operating rules

Clipensk — resident Windows clipboard-history manager.

- repository / source of truth: `lukindv77/Clipensk`;
- canonical branch: `main`;
- Windows x64/AMD64 only; ARM64 вне scope;
- C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected SQLite uses SQLCipher; один MasterKey на storage;
- `AGENTS.md`, `docs/CI_LOG_ACCESS.md` и `docs/WORKFLOW_NEW_CHAT_HANDOFF.md` обязательны;
- promotion в `main` только fast-forward, `force=false`, без merge commit;
- нельзя объявлять PASS/build/release/promotion без CI evidence на exact SHA;
- при GitHub Actions failure сначала connector logs/artifacts, затем project-safe diagnostics; не угадывать причину failure;
- временные feature triggers/diagnostic changes `.github/workflows/build.yml` перед promotion должны быть восстановлены byte-for-byte к fresh canonical workflow из текущего `main`.

Пользователь ожидает автономное продолжение разработки без повторения уже завершённых этапов и без вопросов, если нет реального blocker.

## B. Last accepted product/storage baseline

Последний product/storage implementation baseline:

`08d23672a75f85ca61c2ad62ae57395f2eadfdbc`

Commit:

`ci: restore canonical build workflow after archive split marker`

На этом exact SHA приняты:

- Current schema **v9**;
- Storage Catalog schema **v3**;
- Archive schema **v1**;
- encryption version **1**;
- durable Current v9 pending Archive Split marker + normalized segment plan;
- `SqlitePendingArchiveSplitRepository`;
- Current v8→v9 migration + fail-closed validation;
- canonical Archive filename validation before marker write.

Docs-only handoff refresh может сделать текущий `main` более поздним descendant без product code changes. Новый чат обязан fresh прочитать фактический `main` и не считать `08d23672…` текущим branch head автоматически.

Canonical workflow blobs на product baseline:

- `.github/workflows/build.yml`: `6bb0e8b8eda657c082738a64a4ba80acd857daf4`;
- `.github/workflows/sqlcipher-native.yml`: `e8968b283cc747aa3a9ba5f566538066b89f01be`.

## C. Exact acceptance evidence for Current v9 / pending split marker

### Feature evidence

Final feature Build:

- Build #407;
- run `34709144738`;
- job `103594583078`;
- exact head `bf5f16c2db2725655d93cf97e63897e4bb20d35b`;
- Restore / Build / Test / diagnostics: **SUCCESS**.

### Exact-main evidence

Build:

- Build #408;
- run `34709397765`;
- job `103595248864`;
- exact head `08d23672a75f85ca61c2ad62ae57395f2eadfdbc`;
- full job: **SUCCESS**.

Native SQLCipher:

- Native SQLCipher #73;
- run `34709397774`;
- job `103595248847`;
- exact head `08d23672a75f85ca61c2ad62ae57395f2eadfdbc`;
- full job: **SUCCESS**;
- pinned SQLCipher x64 build: SUCCESS;
- provenance: SUCCESS;
- smoke host: SUCCESS;
- encrypted-storage x64 verification: SUCCESS;
- unpackaged runtime publish: SUCCESS;
- published-runtime SQLCipher loading verification: SUCCESS;
- evidence/runtime artifacts: SUCCESS.

Therefore Current v9 + pending Archive Split marker tranche is **CLOSED**. Не повторять его без новой evidence-driven причины.

## D. Relevant failure/diagnostic history — do not repeat

Build #403 on feature SHA `fd8f455e124c12095081ca41165b2aa9ec47f3fa` compiled successfully but Test failed.

Artifact evidence exposed exactly two causes:

1. four new marker tests failed because `OpenValidatedCurrent` called `reader.GetInt32(3)` only after another `reader.Read()` had already moved past the single identity row;
2. one older negative schema test still treated `PRAGMA user_version = 9` as invalid after Current v9 became the legitimate current version.

Confirmed fixes:

- read `SchemaVersion` before row-exhaustion check;
- move that negative test to invalid version 10.

Later review also hardened marker start-time validation against noncanonical `ArchiveFileName` values that can be created directly through the record struct constructor; regression test proves rejection before DB open.

Final feature Build #407 and exact-main Build #408 / Native #73 are green. Do not resurrect the Build #403 hypotheses or obsolete manual-schema test setup.

## E. Archive Split slice 1 — completed contract

Implemented files include:

- `src/Clipensk.Storage/Databases/PendingArchiveSplitOperation.cs`;
- `src/Clipensk.Storage/Databases/PendingArchiveSplitSqlSchema.cs`;
- `src/Clipensk.Storage/Databases/SqlitePendingArchiveSplitRepository.cs`;
- Current v9 support in `ProtectedStorageDatabaseService.cs`;
- v9-aware Current maintenance validation;
- `tests/Clipensk.Storage.Tests/ProtectedStorageCurrentSchemaV9MigrationTests.cs`;
- `tests/Clipensk.Storage.Tests/SqlitePendingArchiveSplitRepositoryTests.cs`.

Durable operation phases:

`Planned → ReadyToPublish → PhysicalPublished → CatalogPublished`

Repository invariants:

- one active operation per storage;
- immutable ordered segment plan;
- first result preserves source filename + source DatabaseId;
- additional segments require new DatabaseIds and canonical family filenames;
- exact contiguous source-coverage partition;
- canonical filename and unique filename/DatabaseId validation;
- operation ownership checks;
- phase advances exactly one step;
- marker clear only after `CatalogPublished`;
- Current identity/schema v9 checked fail-closed;
- write operations run under `ProtectedStorageMutationLease`.

This slice intentionally does **not** build shadow Archive DBs, publish files, rebuild Catalog, perform recovery orchestration or expose split UI.

## F. Current durable docs

During this checkpoint a docs-only refresh brought these documents in line with promoted code:

- `docs/CURRENT_DATABASE_SCHEMA.md` — Current v2-v9, migrations through v9, repository boundaries and exact v9 acceptance evidence;
- `docs/ARCHIVE_SPLIT_PROTOCOL.md` — slice 1 marked DONE, planner NEXT, later slices NOT STARTED;
- `docs/ARCHITECTURE.md` — Archive Split now uses copy-first / roll-forward crash-safe protocol instead of the old simplistic move/delete ordering;
- `docs/HANDOFF_CURRENT.md` — this operational checkpoint.

Always prefer fresh code + GitHub state over prose if they ever diverge again.

## G. Completed work not to reopen casually

Besides the Archive Split marker foundation, previously promoted work already includes protected storage lifecycle, Current/Archive/Catalog validation and recovery, Current→Archive transfer, Archive and Current VACUUM/optimize maintenance, Catalog rebuild/replacement UI, global/application capture-policy maintenance/resume, external-payload catalog/trash handling, application discovered formats, journal/capture plumbing and the corresponding tests.

Historical feature branches are not automatically active work. In particular:

`feat/archive-split-pending-marker`

was fully promoted and matched product baseline `08d23672…` at checkpoint time.

Manual production WinUI smoke/UX remains **UNVERIFIED** unless a later durable record or fresh evidence says otherwise; CI success is not a claim of manual desktop UX verification.

## H. NEXT engineering slice — pure Archive Split planner

No engineering feature branch is active after the docs refresh.

Recommended next branch after fresh TOCTOU:

`feat/archive-split-planner`

Scope only: deterministic split planning from already validated source metadata + a snapshot of occupied archive-family filenames/sequences. No DB mutation and no filesystem mutation.

Planner responsibilities:

1. validate that requested result ranges are at least two nonempty whole-day `JournalDateRange` values;
2. require exact contiguous partition of source coverage, with no gaps/overlaps and no coverage outside source;
3. preserve source canonical filename and source DatabaseId for segment 0;
4. allocate new nonempty DatabaseIds for later segments;
5. allocate new suffixes from the same `BaseNumber` after the maximum occupied family sequence, skipping/rejecting collisions according to the immutable plan contract;
6. reject noncanonical/out-of-range archive names and any nested-name semantics;
7. return stable ordered plan data suitable for `SqlitePendingArchiveSplitRepository.StartAsync`;
8. remain pure and deterministic apart from an explicit injected/provided DatabaseId generation boundary if GUID generation is not supplied by the caller.

Expected tests:

- happy-path two/multi-segment partition;
- gap and overlap rejection;
- ranges outside source rejection;
- one-segment/empty split rejection;
- source identity preservation;
- existing sibling suffix allocation;
- duplicate/collision behavior;
- canonical six-digit base / four-digit positive split bounds and overflow behavior;
- deterministic segment ordering and coverage;
- no nested names.

### Dependency-direction design check before coding

`ArchiveFileName` is in `Clipensk.Core.Storage` and `JournalDateRange` is in `Clipensk.Core.History`, but `PendingArchiveSplitSegment` currently lives in `Clipensk.Storage.Databases`.

`Clipensk.Core` must not depend on `Clipensk.Storage`.

Therefore the new chat must first choose one of these clean designs after inspecting fresh code:

- pure planner in Core returning a Core plan DTO, adapted to `PendingArchiveSplitSegment` in Storage; or
- pure planner implemented in Storage while depending only on Core value types and performing no I/O.

Do **not** make Core reference the Storage project merely to reuse `PendingArchiveSplitSegment`.

Relevant starting files:

- `src/Clipensk.Core/Storage/ArchiveFileName.cs`;
- `src/Clipensk.Core/History/JournalDateRange.cs`;
- `src/Clipensk.Core/Storage/StorageQueryPlanner.cs`;
- `src/Clipensk.Core/Storage/ArchiveSegmentDescriptor.cs`;
- `src/Clipensk.Storage/Databases/PendingArchiveSplitOperation.cs`;
- `src/Clipensk.Storage/Databases/SqlitePendingArchiveSplitRepository.cs`;
- `tests/Clipensk.Storage.Tests/SqlitePendingArchiveSplitRepositoryTests.cs`.

Do not expand this slice into shadow DB construction or publication/recovery.

## I. Later Archive Split slices — NOT STARTED

After planner only:

1. shadow Archive builder + exact source/output EventId/content cross-check;
2. publication/recovery state machine with fault-injection at every durable/file boundary;
3. Catalog rebuild integration + end-to-end storage tests;
4. Maintenance UI for Archive selection, split boundaries, explicit confirmation, progress/error reporting.

Protocol details are authoritative in `docs/ARCHIVE_SPLIT_PROTOCOL.md`.

## J. GitHub mutable-state notes

At checkpoint preparation there was one unrelated stale open PR:

- PR #1, `docs: document GitHub Actions log access`;
- historical head `docs/actions-log-access` / `dd500eee1ef76a5e0984f43a88700abf31a24122`;
- the current `main` already contains the durable `docs/CI_LOG_ACCESS.md` policy and much later development.

Treat PR #1 as stale historical GitHub state, **not active engineering work**. Re-check before changing/closing it; do not merge it into current main as a shortcut.

Many old feature branches also remain. Branch existence does not imply unfinished work.

## K. CI / promotion rules for the next slice

Canonical Build normally triggers on `main`. Feature validation may temporarily add the feature branch trigger, but before promotion restore `.github/workflows/build.yml` byte-for-byte to the fresh canonical current-main file.

Before every durable write:

1. fresh branch head;
2. fresh `AGENTS.md`;
3. fresh relevant file/ref.

Before promotion:

1. fresh `main`;
2. fresh feature ref;
3. fresh compare;
4. require `behind_by=0`;
5. require merge base == exact current `main`;
6. review net diff and ensure no temporary workflow/diagnostic changes remain;
7. fast-forward `main` with `force=false`.

After promotion, official acceptance is based only on workflows that actually ran on the exact promoted main SHA.

Current Native SQLCipher path filter includes `src/Clipensk.Storage/**` and `src/Clipensk.Core/Storage/**`. Therefore planner placement may make Native mandatory even if the code is pure. Always fresh-read `.github/workflows/sqlcipher-native.yml` instead of assuming path-filter behavior from this handoff.

## L. Exact resume point for the next chat

On the first turn of the new chat:

1. Fresh-read `main` SHA from GitHub. Mutable GitHub state wins over every SHA in this handoff.
2. Read fresh:
   - `AGENTS.md`;
   - `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`;
   - `docs/HANDOFF_CURRENT.md`;
   - `docs/ARCHIVE_SPLIT_PROTOCOL.md`.
3. Check workflow runs on the current `main`. If the docs-only handoff refresh Build is still running, check that exact run first; if it failed, diagnose logs/artifact before any feature work.
4. Confirm Current v9 marker tranche remains accepted; do not rerun or redesign it without new evidence.
5. Inspect fresh planner inputs/contracts listed in section H, especially the Core↔Storage dependency boundary.
6. Create `feat/archive-split-planner` from exact current `main` only after fresh TOCTOU.
7. Implement only the pure planner + focused tests.
8. Run exact feature Build using the established temporary-trigger discipline if needed.
9. Restore canonical workflow, fresh compare, fast-forward promote only after feature evidence.
10. Determine exact-main Build/Native requirements from the final net diff and current workflow path filters, then require all applicable exact-SHA gates before closing the planner tranche.

If a CI failure occurs at any point, follow `docs/CI_LOG_ACCESS.md`: obtain exact job logs/artifact evidence first and make only the smallest evidence-driven fix.

## M. Recommended model for the next session

Recommended: **GPT-5.6 Sol, High**.

Reason: the next slice is small in code size but has nontrivial invariants around calendar partitioning, canonical filename allocation, Core/Storage dependency direction and later crash-safe compatibility. High reasoning is justified to keep the planner pure and prevent decisions that would complicate publication/recovery slices.
