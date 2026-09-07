# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-07.

Этот файл — operational checkpoint. Mutable GitHub state всегда важнее текста handoff. Перед любой durable записью нужен fresh TOCTOU GitHub.

## A. Project identity

Clipensk — Open Source резидентный Windows clipboard-history manager.

- repository: `lukindv77/Clipensk`;
- canonical branch: `main`;
- platform: Windows x64/AMD64 only; ARM64 не поддерживается;
- stack: C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected storage: SQLCipher, один MasterKey на storage;
- production schemas: Current v6 / Catalog v2 / Archive v1.

Ключевые документы: `AGENTS.md`, `docs/REQUIREMENTS.md`, `docs/ARCHITECTURE.md`, `docs/CURRENT_DATABASE_SCHEMA.md`, `docs/CLIPBOARD_HISTORY_SCHEMA.md`, `docs/STORAGE_CATALOG_SCHEMA.md`, `docs/ARCHIVE_DATABASE_SCHEMA.md`, `docs/GLOBAL_CAPTURE_POLICY.md`, `docs/CUSTOM_BINARY_FORMAT_CONFIGURATION.md`, `docs/PROTECTED_CLIPBOARD_DELIVERY_COMPOSITION.md`, `docs/CLIPBOARD_WORKER_LIFECYCLE.md`, `docs/OPEN_QUESTIONS.md`.

## B. User intent and workflow

Пользователь просит продолжать разработку без повторения уже завершённых этапов.

Обязательные правила:

- GitHub `main` + exact Actions evidence — source of truth;
- feature branches для code changes;
- перед `main` update: fresh main, compare, `behind=0`, merge-base=current main, затем только fast-forward `force:false`;
- PASS только после exact-SHA GitHub Actions evidence;
- failure-driven fixes only;
- x64 only;
- не ослаблять fail-closed/cancellation/transaction invariants;
- policy mutation нельзя реализовывать без cleanup semantics;
- manual WinUI/real-clipboard smoke не считать выполненным без evidence.

## C. Last verified canonical main before active feature

Fresh canonical main at start of the active transfer tranche:

`7ba70876ef854b99c2b6365b1c0dececaa35e38f`

Этот main уже содержит Archive v1 foundation и docs sync.

Exact Actions on that SHA:

- Build #162 — SUCCESS;
- Native SQLCipher #41 — SUCCESS.

Open PRs and open issues were both 0 when checked.

Before that, custom-binary initial setup functional baseline `797d736bf1ae22d329f46a4b293a6aa76b00f3ed` also had exact Build #158 + Native SQLCipher #40 SUCCESS.

## D. Active feature

Branch:

`feat/current-to-archive-transfer`

Base:

`7ba70876ef854b99c2b6365b1c0dececaa35e38f`

Current owner: resumable Current→Archive transfer.

A temporary Build workflow trigger for this exact feature branch exists only to obtain Windows CI evidence and MUST be restored to canonical `branches: [ main ]` before final compare/main promotion.

Latest runtime/test SHA with green feature evidence before this docs checkpoint:

`82e46ccf75881278fed17333924d9a54a301d73f`

Build #167 / run `34123652746` — SUCCESS:
- Verify x64-only implementation scope — SUCCESS;
- Restore — SUCCESS;
- Build — SUCCESS;
- Test — SUCCESS.

A prior Build #166 failed only on CS0136 in the new test helper due to duplicate pattern-variable name; runtime projects compiled. The minimal variable rename fixed it. Do not treat #166 as a runtime regression.

This handoff/docs commit is after the green runtime SHA, so final docs-inclusive feature SHA needs its own fresh feature Build before promotion.

## E. Completed product foundations

Already completed and not to be repeated:

- clipboard signal/enqueue resident capture pipeline;
- standard WinRT readers and exact MaxBytes semantics;
- prohibited Wave/Riff/virtual-file-content guards;
- Clipensk-owned durable ApplicationId with conflict fail-closed;
- Current history write/read/keyset continuation;
- Catalog v2 external content-address index;
- Current v6 global capture policy;
- storage-scoped custom binary FormatName→FileExtension configuration/provider;
- atomic initial global policy + custom extension setup;
- WinUI initial custom-binary policy setup;
- protected delivery composition;
- app clipboard worker lifecycle tied to active protected session;
- Archive v1 create/validate foundation.

Manual WinUI/real clipboard smoke remains UNVERIFIED.

## F. Archive v1 foundation

`ProtectedArchiveDatabaseService` already exists on canonical main.

Archive v1:
- canonical `archive_######.db` / `archive_###### _####.db` family naming (actual filenames have no spaces);
- exact filename ↔ identity base/split validation;
- DatabaseRole.Archive;
- mandatory inclusive coverage dates;
- SQLCipher / active protected session;
- self-contained ApplicationIdentity + ClipboardHistory schema;
- ReadOnly validation with quick_check, schema shape, FK check, coverage checks;
- staging create + cancellation before final atomic move;
- no late cancellation demotion after successful final move.

Normal Archive access remains ReadOnly; writes are explicit maintenance boundaries only.

## G. Current→Archive transfer implemented on active feature

`ProtectedCurrentToArchiveTransferService.TransferAsync` implements an explicit maintenance transfer without changing Archive schema version.

Preflight:
- canonical target ArchiveFileName;
- only completed calendar days; today/future rejected;
- all canonical `Archive/archive_*.db` are ReadOnly validated;
- `StorageQueryPlanner.ValidateArchiveCoverage` rejects any cross-archive coverage overlap before mutation;
- target must exist;
- transfer range must be fully inside target assigned coverage.

Durable ordering:

1. Current v6 is opened ReadOnly, validated and exact selected-range event/payload/application rows are materialized.
2. Referenced ApplicationIdentity rows plus exact history rows are written to Archive in one transaction.
3. Existing Archive EventId is reusable only when envelope + ordered payload rows exact-match; conflicts fail closed.
4. Cancellation before Archive COMMIT.
5. After Archive COMMIT, target is fully revalidated ReadOnly.
6. Current opens ReadWrite only for purge.
7. Inside Current purge transaction the same range is reread and exact-compared with the copied batch.
8. If Current changed, purge rolls back and retry is required.
9. Only exact verified events are deleted; payloads cascade.
10. Cancellation immediately before Current COMMIT.
11. No cancellation demotion after successful Current COMMIT.

Resumability is idempotent durable replay, not a new operation-log schema. Crash/cancel after Archive COMMIT but before Current purge leaves the safe temporary duplicate state. Retry exact-compares already copied rows and continues verify→purge.

External payload bytes are not copied or moved; existing SHA/RelativePath/Size references are preserved.

Only referenced ApplicationIdentity rows are copied; mutable ApplicationIdentityAlias rows and policy overlays are not automatically copied.

## H. Transfer regression coverage

New tests cover:

- normal copy → verify → purge for a closed day;
- text + external PNG metadata preservation;
- referenced ApplicationIdentity copy without aliases;
- cancellation after Archive COMMIT leaves Current intact and retry succeeds;
- concurrent Current change before purge prevents deletion; retry copies full changed range and succeeds;
- conflicting pre-existing Archive event fails without Current purge;
- today/future date rejection before storage open;
- transfer range outside target coverage rejection;
- overlapping archive coverage rejection before mutation.

## I. Important invariants

- `WM_CLIPBOARDUPDATE` remains signal/enqueue only.
- Capture only under active unlocked protected access.
- Password never persisted.
- Current/Catalog/Archive share one MasterKey.
- one clipboard snapshot through capture readers.
- no silent identity merge.
- no invented custom extension/format/size defaults.
- external payload addresses resolved before history transaction.
- Catalog first stored path wins per exact SHA.
- archive ownership is whole calendar days; assigned archive coverages may not overlap.
- Current→Archive order is Archive write → Archive verify → Current purge; never delete Current first.
- temporary duplicate is allowed; missing both copies is forbidden.
- Archive coverage does not shrink merely because rows are cleaned.
- committed success is not converted into late cancellation failure.

## J. Known remaining risks / unimplemented work

Current transfer feature does NOT yet provide:

- persisted/derived segment sealing;
- Catalog archive metadata;
- global maintenance coordinator/lock across independent maintenance operations;
- archive history read/unified Current+Archive query;
- Catalog rebuild from Current+Archives;
- external reference last-reference cleanup / Trash GC;
- policy mutation with required Current/Archive cleanup;
- archive split/repair/migration;
- FTS/unified search completion;
- manual real clipboard/WinUI smoke;
- final installer/packaging decision;
- final Open Source license selection.

Preflight validates all archives before transfer but no global MaintenanceCoordinator exists yet. Do not claim that concurrent independent archive create/maintenance is globally serialized.

## K. Immediate resume steps

1. Fresh fetch active feature head and latest feature Build after this docs checkpoint.
2. Require x64/Restore/Build/Test all SUCCESS on the final docs-inclusive feature SHA.
3. Restore `.github/workflows/build.yml` exactly to canonical main-only trigger.
4. Fresh fetch `main` immediately before promotion.
5. Compare current main→feature and require `behind=0`, merge-base=current main, and no `.github/workflows/build.yml` diff.
6. Fast-forward `main` with `force:false` only.
7. Verify official managed Build and Native SQLCipher on the exact new main SHA before declaring transfer PASS.
8. Next code tranche after transfer PASS: segment sealing + Catalog archive metadata. Then archive read/unified query, Catalog rebuild, external-reference cleanup/Trash, and only then policy mutation cleanup.

## Resume rule

Mutable GitHub state supersedes this file. Never repeat completed archive foundation or transfer work if main already contains a newer verified tranche.