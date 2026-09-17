# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-17.

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

Пользователь ожидает автономное продолжение разработки без повторения уже завершённых этапов и без уточняющих вопросов, если нет реального blocker.

Постоянные требования работы:

- promotion в `main` только fast-forward, `force=false`, без merge commit;
- нельзя объявлять PASS/build/release/promotion без exact-SHA CI evidence;
- при Actions failure сначала connector logs/artifacts, затем `docs/CI_LOG_ACCESS.md`; причину не угадывать;
- временные feature triggers/diagnostics перед promotion восстанавливать byte-for-byte к fresh canonical workflow;
- manual production WinUI UX/smoke не считать проверенным без отдельного durable evidence.

## C. Current authoritative state

Последний принятый product code baseline на момент подготовки checkpoint:

`306599c00b29b3efefd6e18d3ab19fbca35c42ce`

Это `main` после Archive Split Maintenance UI и startup recovery integration.

Exact-main CI для этого product SHA:

- Build #430;
- run `35249103960`;
- job `105296726619`;
- Restore / Build / Test / diagnostics / complete job: **SUCCESS**.

Native SQLCipher для `306599c0…` не запускался и не требовался: net product diff startup-recovery затрагивал только `src/Clipensk.App/App.PolicyMaintenance.cs`, а native workflow `paths` filter покрывает storage/Core.Storage/App.csproj/native tooling, но не этот файл.

Последний storage-changing accepted baseline:

`15aff534258e4302c828ea437e86113ff2d09562`

Evidence:

- Build #426 — **SUCCESS**;
- Native SQLCipher #79 — **SUCCESS**.

Canonical workflow blobs:

- `.github/workflows/build.yml`: `6bb0e8b8eda657c082738a64a4ba80acd857daf4`;
- `.github/workflows/sqlcipher-native.yml`: `e8968b283cc747aa3a9ba5f566538066b89f01be`.

Active docs-only refresh branch during checkpoint preparation:

`docs/archive-split-status-refresh-20260917`

It was created directly from product baseline `306599c0…`; `docs/ARCHIVE_SPLIT_PROTOCOL.md` has already been refreshed on that branch. Because this handoff update and later docs promotion advance mutable refs, the next chat must fresh-check actual `main` and branch heads rather than assuming this paragraph contains the final docs SHA.

## D. Current owner / active task

Archive Split engineering tranche is complete in code and CI. The active task at checkpoint time is documentation/status closure only:

- synchronize `docs/ARCHIVE_SPLIT_PROTOCOL.md` with implemented planner → shadow → publication/recovery → Catalog → start/recovery orchestration → Maintenance UI → startup recovery;
- replace stale `docs/HANDOFF_CURRENT.md` marker/planner checkpoint with this current checkpoint;
- promote the docs-only branch after fresh compare if it remains a clean descendant of current `main`.

Acceptance criteria for the docs tranche:

- net diff contains only intended docs;
- no workflow or product-code changes;
- `behind_by=0` and merge-base exactly current `main` before promotion;
- promotion fast-forward only;
- Archive Split manual WinUI smoke remains explicitly `UNVERIFIED`.

## E. What has been completed

Archive Split implementation is complete across these layers:

1. Current v9 durable `PendingArchiveSplit` schema/repository/migration/validation.
2. `ArchiveSplitPlanner` with exact partition validation and family suffix allocation.
3. `ProtectedArchiveSplitShadowBuilder` with staged SQLCipher Archive construction and exact source/output cross-check.
4. `ProtectedArchiveSplitPublisher` with copy-first, replacement-backup and roll-forward physical publication.
5. `ProtectedArchiveSplitCatalogPublisher` with Catalog rebuild/validation and durable completion cleanup.
6. `ProtectedArchiveSplitRecoveryService` coordinating continuation from every persisted phase.
7. `ProtectedArchiveSplitStartService` atomically validating the selected source snapshot, planning and persisting `Planned`, then handing off to recovery.
8. WinUI Maintenance split UI with archive selection, one internal split boundary producing two contiguous ranges, explicit confirmation, busy/error state and pending-operation recovery gate.
9. Startup/unlock integration in `App.PolicyMaintenance.cs`: pending Archive Split recovery runs inside the existing pre-runtime clipboard suspension before policy-maintenance recovery; continuation failure remains fail-closed and does not resume clipboard runtime.

Important exact acceptance checkpoints:

- recovery coordinator main `4d254c13e6ce8fd157fa02fb32ce3ae9730d8580`: Build #424 **SUCCESS**, Native SQLCipher #78 **SUCCESS**;
- start service main `15aff534258e4302c828ea437e86113ff2d09562`: Build #426 **SUCCESS**, Native SQLCipher #79 **SUCCESS**;
- Maintenance UI main `7362e0ff2809a35627ac091c98e1a4380a0c0a56`: Build #428 / run `35242522130` **SUCCESS**;
- startup recovery main `306599c00b29b3efefd6e18d3ab19fbca35c42ce`: Build #430 / run `35249103960` **SUCCESS**.

Do not reopen or reimplement these slices without new evidence-driven cause.

## F. Current conclusions

Archive Split uses a copy-first / roll-forward-only crash-safe protocol.

Key decisions:

- Current durable marker, not Catalog, is the source of truth for unfinished split state.
- Source canonical filename and source DatabaseId are preserved for the first result segment.
- New family suffixes and new DatabaseIds are allocated at planning time and stored immutably.
- Shadow set is fully built and cross-checked before publication.
- Additional outputs publish before replacement of the source; the old full source remains backed up until physical + Catalog validation succeeds.
- After `ReadyToPublish`, recovery rolls forward; cancellation is not represented as rollback.
- Catalog remains rebuildable projection, never the sole holder of split metadata.
- UI currently exposes the common two-segment operation through one split-boundary date. Backend supports multi-segment plans; repeated UI splits can further subdivide archives.
- Pending split is integrated into startup pre-runtime recovery so clipboard monitoring is not resumed over an unfinished durable split.

Earlier docs saying planner was NEXT or later slices NOT STARTED are superseded.

## G. Important invariants

- Before every durable repository write: fresh target branch/ref, fresh `AGENTS.md`, fresh relevant files/refs.
- Before promotion: fresh `main`, fresh feature ref, fresh compare; require `behind_by=0`; merge-base must equal current `main`; review full net diff.
- `main` update only fast-forward with `force=false`.
- Temporary workflow changes must be restored byte-for-byte before promotion.
- Storage-changing promotion requires exact-main Build + Native SQLCipher according to current workflow path contract.
- Archive Split immutable plan and durable phase ownership must remain fail-closed.
- No overwrite of unexpected canonical Archive files.
- No deletion of source backup/staging/marker before final physical/Catalog validation required by the protocol.
- Clipboard runtime suspension must remain held after startup maintenance recovery failure.
- Manual desktop UX status is independent from CI status.

## H. Known risks and unresolved questions

- Manual production WinUI Archive Split smoke/UX is **UNVERIFIED**. CI confirms build/tests, not interactive desktop behavior.
- The Maintenance UI intentionally provides a two-segment split boundary, while backend supports arbitrary multi-segment plans. This is a product-scope choice, not a backend limitation.
- `docs/REQUIREMENTS.md` contains broader requirements beyond Archive Split. A fresh gap audit is needed before selecting the next engineering tranche; do not assume Archive Split completion means all project requirements are complete.
- Archive rotation configuration by record count/size/day span is explicitly required by `docs/REQUIREMENTS.md` §4.2; no current implementation was established by this checkpoint. Treat its implementation status as **UNVERIFIED / candidate next gap** until fresh code audit confirms it.

## I. Remaining work

Immediate mandatory work:

1. Finish the active docs-only refresh branch.
2. Fresh-check current `main`, docs branch, `AGENTS.md`, canonical workflows and compare.
3. Require a docs-only net diff and clean fast-forward relationship.
4. Fast-forward the docs branch into `main` with `force=false` if still valid.

Next engineering work after docs closure:

1. Perform a focused requirements-to-code gap audit, starting with `docs/REQUIREMENTS.md` §4.2 Archive rotation thresholds/configuration and related §9 scheduled transfer / §10 maintenance requirements.
2. Select the first demonstrably missing requirement as the next bounded tranche; do not implement based only on prose without verifying current code.
3. Preserve the existing storage/CI promotion rules for any storage-changing tranche.

Optional/manual validation:

- run real WinUI Maintenance split smoke on Windows against a disposable protected storage and record durable evidence if/when the environment permits it.

## J. Exact resume point

Следующий чат должен начать с: **fresh проверить `main` и `docs/archive-split-status-refresh-20260917`, прочитать `AGENTS.md`, затем сравнить docs branch с current `main`; если `behind_by=0`, merge-base exact current main и net diff только `docs/ARCHIVE_SPLIT_PROTOCOL.md` + `docs/HANDOFF_CURRENT.md`, fast-forward docs branch в `main`. После docs closure выполнить focused requirements/code gap audit, начиная с Archive rotation requirements §4.2, а Archive Split implementation не повторять.**

## K. First-turn bootstrap instructions

1. Не доверять этому handoff вместо свежего GitHub state там, где ref/CI могли измениться.
2. Сначала проверить current `main`, active docs branch и relevant Actions results.
3. Прочитать fresh `AGENTS.md`, `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`, `docs/ARCHIVE_SPLIT_PROTOCOL.md`, `docs/REQUIREMENTS.md`.
4. Не повторять completed Archive Split backend/UI/startup work без evidence-driven причины.
5. При противоречии handoff и repository state считать repository source of truth приоритетным.
6. При CI failure получить logs/artifacts до любых fixes.
7. Сохранить `UNVERIFIED` для manual WinUI smoke до фактической ручной проверки.
8. Продолжить ровно с Exact resume point выше.
