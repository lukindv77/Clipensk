# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-18.

Mutable GitHub state is authoritative and supersedes this file. Перед любой durable repository write обязательна fresh TOCTOU-проверка relevant refs/files; перед promotion — fresh `main`, feature ref, compare и canonical workflows.

## A. Project identity

Clipensk — resident Windows clipboard-history manager.

- repository / source of truth: `lukindv77/Clipensk`;
- canonical branch: `main`;
- Windows x64/AMD64 only; ARM64 вне scope;
- C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected SQLite uses SQLCipher; один MasterKey на storage;
- Current schema v9, Storage Catalog schema v3, Archive schema v1;
- обязательные правила: `AGENTS.md`, `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`, `docs/CI_LOG_ACCESS.md`, `docs/ARCHIVE_SPLIT_PROTOCOL.md`.

## B. User intent

Пользователь ожидает автономное продолжение разработки без повторения завершённых этапов и без лишних уточняющих вопросов.

Постоянные требования:

- promotion в `main` только fast-forward, `force=false`, без merge commit;
- PASS/build/release/promotion только по exact-SHA CI evidence;
- при Actions failure сначала connector logs/artifacts, затем `docs/CI_LOG_ACCESS.md`; причину не угадывать;
- временные feature triggers/diagnostics перед promotion восстанавливать byte-for-byte к fresh canonical workflow;
- manual production WinUI UX/smoke не считать проверенным без отдельного durable evidence.

## C. Current authoritative state

Последний полностью принятый main product baseline на момент этого checkpoint:

`ef96c1aba2eaad7a2af853a85981ac1be9d4a45e`

Он содержит Archive Rotation settings/planner foundation:

- `ArchiveRotationSettings` с `MaxRecordCount`, `MaxBytes`, `MaxCalendarDays`;
- explicit `ArchiveRotationThresholdMode.Any | All` для multi-threshold policy;
- JSON persistence validation;
- pure `ArchiveRotationPlanner` для count/day inputs;
- pure planner fail-closed на `MaxBytes`, потому что requirement/architecture определяют size как physical Archive DB size.

Exact-main CI для `ef96c1ab…`:

- Build #438 / run `35293366264` — **SUCCESS**;
- Native SQLCipher #81 / run `35293366256` — **SUCCESS**.

Canonical Build workflow blob:

`6bb0e8b8eda657c082738a64a4ba80acd857daf4`

Active docs-only branch:

`docs/archive-rotation-protocol-20260918`

At checkpoint time its latest known head was:

`bb0bf5c4d31379b58f089ec25fba5fbf88c916fe`

Branch already contains:

- new `docs/ARCHIVE_ROTATION_PROTOCOL.md`;
- `docs/ARCHITECTURE.md` rotation cross-reference/open-tail semantics update.

This handoff update itself advances that docs branch, so the next chat must fresh-check its actual head before any write/promotion.

## D. Current owner / active task

Archive Rotation is the active engineering tranche.

The immediate task is docs/protocol closure before storage implementation:

1. finalize `ARCHIVE_ROTATION_PROTOCOL.md`;
2. keep `ARCHITECTURE.md` aligned;
3. refresh this handoff;
4. promote the docs-only branch after fresh compare;
5. require exact-main Build after docs promotion.

After protocol acceptance, the next product slice is a **pure planner correction**, not storage mutation yet.

## E. What has been completed

Archive Split remains complete and must not be reimplemented without evidence-driven cause:

- Current v9 pending split schema/repository/migration;
- pure split planner;
- shadow builder;
- copy-first physical publisher;
- Catalog publisher;
- recovery coordinator;
- atomic start service;
- Maintenance UI;
- startup pre-runtime split recovery.

Important Archive Split checkpoints remain documented in `ARCHIVE_SPLIT_PROTOCOL.md`.

Archive Rotation completed foundation:

1. additive optional settings contract and persistence;
2. positive-threshold validation;
3. explicit ANY/ALL multi-threshold mode;
4. pure count/day planning foundation;
5. physical-size path intentionally rejected by pure planner;
6. exact-main Build/Native acceptance on `ef96c1ab…`.

## F. Current Archive Rotation conclusions

Requirements and architecture establish:

- rotation by record count, physical Archive DB size, and/or calendar span;
- whole-day boundaries only;
- physical size means actual SQLCipher Archive `.db` length, not logical clipboard payload bytes;
- ANY/ALL combination belongs to policy;
- Catalog is rebuildable projection and not durable operation journal;
- existing Current→Archive transfer is copy-first/idempotent but expects an already-created Archive whose assigned coverage contains the transfer range;
- existing `ProtectedArchiveDatabaseService.CreateAsync` publishes immediately and generates DatabaseId internally.

The new protocol branch fixes the intended rotation model:

- threshold predicates are evaluated after a complete day;
- predicate is reached at `metric >= threshold`;
- once ANY/ALL rule is reached, that segment is ready for Archive rotation;
- last not-yet-reached segment is an open tail and remains in Current;
- zero-record dates inside a candidate range count toward calendar span;
- only closed dates are eligible;
- new rotation outputs use new unsplit base Archive names;
- physical-size planning requires storage-backed shadow DB construction;
- rotation needs a durable Current pending marker and roll-forward recovery before clipboard runtime resumes;
- final source purge should reuse/refactor existing exact Current→Archive compare/purge logic rather than create a parallel transfer implementation.

## G. Important invariants

- Before every durable repository write: fresh target branch/ref, fresh `AGENTS.md`, fresh relevant files/refs.
- Before promotion: fresh `main`, fresh feature ref, fresh compare; require `behind_by=0`; merge-base must equal current `main`; review full net diff.
- `main` update only fast-forward with `force=false`.
- Temporary workflow changes restored byte-for-byte before promotion.
- Any `src/Clipensk.Storage/**` or `src/Clipensk.Core/Storage/**` promotion requires exact-main Build + Native SQLCipher according to current path filter.
- No automatic rotation may split a CalendarDate.
- No source purge before validated Archive durability.
- Assigned Archive coverage is authoritative in Archive DB and is not casually extended in-place.
- Unexpected canonical Archive filename/identity collision is fail-closed.
- Pending rotation must block conflicting Archive layout/history maintenance.
- Manual WinUI smoke remains independent from CI.

## H. Known risks / unresolved questions

- Manual production WinUI Archive Split smoke remains **UNVERIFIED**.
- Archive Rotation storage implementation does not yet exist.
- Current pure `ArchiveRotationPlanner` still has a known semantic gap relative to the new protocol: it partitions and returns the final tail instead of distinguishing ready ranges vs open tail, and its existing tests use pre-threshold lookahead semantics. It must be corrected before automatic rotation consumes it.
- Pending Archive Rotation durable schema/repository is not implemented; likely next Current schema version after v9.
- Storage-backed physical-size shadow planning, deterministic base-name allocation, planned DatabaseId creation, copy-first publication, source purge integration, Catalog publication, recovery, scheduler, and Settings UI are all still pending.
- Existing transfer acquires its own mutation lease; future rotation coordinator must refactor/expose a lease-aware exact compare/purge core rather than nest lease acquisition.

## I. Remaining work

Priority order:

1. Finish docs-only protocol branch:
   - fresh-check `main`, docs branch, `AGENTS.md`, canonical workflows;
   - compare and require `behind_by=0`, merge-base exact current main;
   - verify net diff docs-only;
   - fast-forward `main`, `force=false`;
   - wait for exact-main Build and record evidence.
2. New product branch from accepted docs main:
   - correct pure rotation planner to produce ready ranges + open tail;
   - adopt post-day `>=` threshold semantics for count/day;
   - preserve physical-size fail-closed behavior;
   - tests for equality, oversized day, ANY/ALL, zero-record span, empty/no-ready case.
3. Current pending rotation schema/repository/migration.
4. Storage-backed source scanner + shadow planner/builder.
5. Copy-first publication + lease-aware transfer purge.
6. Catalog/recovery/startup integration.
7. Scheduler/manual trigger + Settings UI.
8. Manual production smoke evidence remains separate.

## J. Exact resume point

Fresh-check current `main` and `docs/archive-rotation-protocol-20260918`. Read `AGENTS.md`, `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`, `docs/HANDOFF_CURRENT.md`, and `docs/ARCHIVE_SPLIT_PROTOCOL.md`. If the docs branch remains a clean descendant of `main` with net diff limited to `docs/ARCHIVE_ROTATION_PROTOCOL.md`, `docs/ARCHITECTURE.md`, and this handoff, fast-forward it into `main` and require exact-main Build success. Then begin the pure planner correction slice on a new branch; do not start storage mutation code before that planner contract is corrected.

## K. Bootstrap rules

1. Repository state beats this handoff if refs/CI moved.
2. Do not repeat accepted Archive Split work.
3. Do not treat current pure planner as ready for automatic executor use until ready-tail semantics are fixed.
4. Do not interpret `MaxBytes` as payload byte sum.
5. On CI failure, retrieve evidence before fixes.
6. Keep manual desktop UX status `UNVERIFIED` until actual manual evidence exists.
