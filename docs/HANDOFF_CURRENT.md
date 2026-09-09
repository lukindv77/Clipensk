# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-09.

Mutable GitHub state supersedes this file. Перед любой durable записью нужен fresh GitHub TOCTOU.

## A. Project identity

Clipensk — Open Source resident Windows clipboard-history manager.

- repository: `lukindv77/Clipensk`;
- canonical branch: `main`;
- Windows x64/AMD64 only; ARM64 вне scope;
- C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected SQLite: SQLCipher, один MasterKey на storage.

## B. Canonical baseline

Последний exact PASS `main` перед активной Archive-фазой:

`58135deaa2ce57244d41e4cb5e9e72c8f2d80e59`

На этом SHA уже завершены:

- authoritative global policy change for Current;
- atomic Current cleanup;
- resumable pending-maintenance marker;
- Current v7 policy-maintenance foundation.

Official evidence на `58135dea…`:

- Build #251 / run `34302777049` — SUCCESS;
- Native SQLCipher #52 / run `34302777039` — SUCCESS;
- canonical `.github/workflows/build.yml` blob SHA: `657f11356566b459dc46f1639b0d4ea7728083f2`.

Fresh read перед handoff показывал, что `main` всё ещё равен `58135deaa2ce57244d41e4cb5e9e72c8f2d80e59`.

## C. Active feature

Branch:

`feat/policy-maintenance-archive-external-phase`

Base:

`58135deaa2ce57244d41e4cb5e9e72c8f2d80e59`

Последний implementation/CI head до этого handoff-doc commit:

`ae5f34aad60ea1163953e29ff619e8cdba8e42e6`

Compare `58135dea…` → `ae5f34aa…` перед handoff:

- status: ahead;
- ahead_by: 21;
- behind_by: 0;
- merge base: exact `58135dea…`;
- intended code/test files:
  - `src/Clipensk.Storage/Clipboard/GlobalPolicyMaintenanceState.cs`;
  - `src/Clipensk.Storage/Clipboard/ProtectedArchiveExternalPolicyMaintenanceService.cs`;
  - `tests/Clipensk.Storage.Tests/ProtectedArchiveExternalPolicyMaintenanceServiceTests.cs`;
- `.github/workflows/build.yml` временно отличается для feature CI и обязан быть восстановлен byte-for-byte до promotion.

Этот handoff-doc commit добавляет только authoritative transition data; перед продолжением получить новый exact branch head из GitHub.

## D. Archive external-reference cleanup contract

Текущая feature-фаза реализует только Archive cleanup для external payload references.

Semantics:

- обычные Archive DB payloads сохраняются;
- удаляются только disallowed external references, где persisted `PayloadKind` = `PngImage` или `CustomBinary`;
- persisted `PayloadKind` authoritative; inference по `FormatName` запрещён;
- effective policy = global merged with application policy;
- `SourceApplicationId == null` => global only;
- payload допустим только если overall Capture == Allow И exact/BINARY `Formats[FormatName].Capture == Allow`;
- MaxBytes reduction не является retroactive purge criterion;
- event headers не удаляются при удалении последнего payload row;
- marker меняет только `archiveExternalReferenceCleanup: pending -> completed`;
- `catalogRebuild`, `externalTrashCollection`, `completion` остаются pending;
- marker не очищается в этой фазе.

## E. Durable maintenance state

`GlobalPolicyMaintenanceState` / codec:

- version = 1;
- `currentPhase` должен быть `completed`;
- continuation fields только `pending|completed`;
- monotonic ordering enforced;
- strict JSON shape;
- uppercase 64-char SHA256 policy fingerprint;
- malformed durable state => fail closed.

Expected marker shape:

```json
{
  "version": 1,
  "policyFingerprint": "<64-char uppercase SHA256>",
  "currentPhase": "completed",
  "archiveExternalReferenceCleanup": "pending|completed",
  "catalogRebuild": "pending",
  "externalTrashCollection": "pending",
  "completion": "pending"
}
```

Operation kind remains:

`GlobalCapturePolicyMaintenance`

## F. Production implementation

`ProtectedArchiveExternalPolicyMaintenanceService`:

- holds shared `ProtectedStorageMutationLease` across Current state/policy read, full Archive preflight, Archive writes, and marker completion;
- validates exact Current v7 identity/schema and pending global maintenance marker;
- verifies persisted global policy fingerprint equals marker fingerprint;
- loads application policies with canonical IDs and exact/BINARY format names;
- enumerates canonical `archive_*.db` files;
- preflights every archive before first mutation;
- validates Archive v1 identity/schema/FK and payload representation;
- detects duplicate archive `DatabaseId`;
- validates external SHA/path/size;
- collects deletion keys only during preflight;
- revalidates archive identity before and after write;
- verifies archive filename set is unchanged across preflight/writes;
- commits each archive DB independently in an immediate RW transaction;
- requires exactly one row deleted for every planned deletion;
- retry after partial archive commits is idempotent;
- cancellation after an archive commit may leave durable partial cleanup with marker pending;
- final Current marker update is transactional;
- no cancellation check after final marker commit, so durable success is not demoted by late cancellation;
- if Archive phase already completed, returns idempotently without Archive writes.

Checkpoint enum used by deterministic tests:

- `MutationLeaseAcquired`;
- `PreflightCompleted`;
- `ArchiveCommitCompleted`;
- `BeforeMarkerCommit`;
- `AfterMarkerCommit`.

## G. Tests present

`ProtectedArchiveExternalPolicyMaintenanceServiceTests.cs` covers:

1. denied external refs removed while ordinary archive payloads/event headers remain;
2. application override, null source, exact binary format names;
3. MaxBytes reduction does not purge allowed external payload;
4. malformed later archive fails preflight before any mutation;
5. cancellation after first archive commit is resumable/idempotent;
6. late cancellation after marker commit does not demote success;
7. exact retry after completed Archive phase is idempotent;
8. malformed marker fails closed before archive writes;
9. missing global maintenance marker rejects before archive writes;
10. shared mutation lease is held through archive observation/writes and marker commit preparation.

The lease test was normalized/formatted in commit:

`eb03b486aa0e398f6e4e46fd9a2769404b76a7de`

## H. CI history relevant to debugging

Earlier failures were CI/test-harness issues, not confirmed production logic failures:

- Build #252 / run `34316076202`: compile failure from xUnit `Assert.NotNull` assignment; fixed.
- Build #253 / run `34322019878`: test deadlock because service reached blocking checkpoint synchronously before returning Task; fixed with async harness.
- Build #254 / run `34322595227`: full Test failed; raw logs were opaque.
- Diagnostic workflows were temporarily introduced to isolate failures.
- Build #265 / run `34358892414`: failed in temporary CI formatting/normalization step before tests, so it is not evidence of a product/test failure.

At handoff time clean validation was running on exact pre-handoff head:

- run `34361813378`;
- job `102500430167`;
- head `ae5f34aad60ea1163953e29ff619e8cdba8e42e6`;
- x64 scope / Setup completed successfully;
- Restore was in progress at last observation;
- Build/Test result was not yet observed.

Do not infer PASS or FAIL. Re-read GitHub for the final result.

## I. Current temporary workflow state

Feature `.github/workflows/build.yml` at `ae5f34aa…`:

- trigger: `branches: [ main, feat/policy-maintenance-archive-external-phase ]`;
- permissions: `contents: read`;
- no self-modifying/push steps;
- normal x64 scope check / .NET setup / restore / build;
- test step runs full solution with TRX failure diagnostics.

Before promotion restore workflow byte-for-byte to canonical main version and prove blob SHA:

`657f11356566b459dc46f1639b0d4ea7728083f2`

## J. Resume procedure

On a new chat:

1. Read fresh GitHub branch heads for `main` and `feat/policy-maintenance-archive-external-phase`.
2. Read final status/steps/logs for run `34361813378` if it still corresponds to the active pre-handoff implementation head.
3. Do not repeat old diagnostic CI mutations.
4. If clean feature Build/Test failed, extract the exact failed assertion/stack from TRX/logs and make the smallest code/test fix.
5. If feature Build/Test succeeded:
   - restore `.github/workflows/build.yml` byte-for-byte canonical;
   - verify workflow blob `657f1135…`;
   - fresh TOCTOU main/feature/compare;
   - require behind=0 and merge-base=current main;
   - require no unrelated files and no net workflow diff;
   - fast-forward `main` with force=false to exact reviewed feature SHA.
6. After promotion require BOTH official workflows on exact promoted main SHA:
   - Build SUCCESS;
   - Native SQLCipher SUCCESS.
7. Verify final `main` exact SHA and canonical workflow.

## K. What remains after Archive phase

Only after Archive external-reference cleanup is green and promoted:

1. Catalog rebuild;
2. external Trash collection;
3. final completion / marker clear / resume coordinator;
4. UI/settings wiring later.

Do not expand Archive tranche into those phases.

## L. Promotion discipline

Never claim PASS without exact SHA evidence.

Before main update:

- fresh main;
- fresh feature;
- compare commits;
- `behind=0`;
- merge-base exactly current main;
- canonical workflow restored;
- no unrelated diff.

Then fast-forward only, no force. Official acceptance is based on workflows that actually ran on the exact promoted main SHA.
