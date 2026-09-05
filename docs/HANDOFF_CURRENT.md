# CURRENT DEVELOPMENT HANDOFF — Clipensk

Checkpoint date: 2026-09-05.

GitHub repository and GitHub Actions remain the mutable source of truth. Before every durable write to `main`, perform a fresh TOCTOU check. Never force-update `main`.

## Product / platform invariants

- Clipensk is an Open Source Windows clipboard-history manager.
- Repository: `lukindv77/Clipensk`; primary branch: `main`.
- Product platform: Windows x64 (AMD64) only. ARM64 is outside product scope and must not be reintroduced into Platforms, RuntimeIdentifiers, native scripts/jobs, runtime artifacts, or docs as a future target.
- Current development host is unpackaged (`WindowsPackageType=None`). Final MSIX/portable strategy is unresolved.
- `WM_CLIPBOARDUPDATE` WndProc must remain signal/enqueue only. No clipboard reading, process resolution, policy lookup, DB work, normalization, or persistence in WndProc.
- Clipboard monitoring is active only while protected data access is UNLOCKED.
- Capture event time uses `EventTimeContext` and preserves UTC timestamp, local offset, Windows time-zone ID, and calendar date semantics from the capture boundary.
- SourceApplication and InvocationApplication are distinct concepts. InvocationApplication is still later work.
- Do not design the full history/catalog/archive schema as part of capture-pipeline tranches.

## Verified native / protected-storage baseline

Already complete and verified from earlier tranches:

- storage-wide password → Argon2id → 32-byte MasterKey lifecycle;
- password never persisted; MasterKey never stored in plaintext;
- protected `Current/current.db` and `Current/storage-catalog.db` with DatabaseIdentity validation;
- production SQLCipher boundary using self-built `sqlcipher.dll`, raw MasterKey via `sqlite3_key`, SQLCipher Community 4.17.0 + OpenSSL 3.5.8;
- x64-only native build/provenance/encryption smoke;
- verified unpackaged x64 runtime staging with the exact `sqlcipher.dll` and no `e_sqlcipher.dll`.

Do not reopen this boundary without a new factual failure.

## Current `main` source state

At this checkpoint, `main` is:

- `362ff6ef3993f9392d437ce8339ce4effda91cfb`
- message: `feat: plan clipboard content reads without payload access`

This commit is the current CI gate and is **not yet PASS** until Build #62 completes and required steps are verified.

## Managed CI evidence

Recent managed-build evidence already confirmed:

- Build #59 — run `33941383433`, SHA `f80fceb8471948a5d82147e1080b6e6d02e99325`: PASS; x64 guard / Restore / Build / Test all SUCCESS.
- Build #60 — run `33941909260`, SHA `138d7a45a2f9777bb4440cb16b751bcb6d15c8c7`: PASS; x64 guard / Restore / Build / Test all SUCCESS.
- Build #61 — run `33942004626`, SHA `4d481223b8ed93acbeb7eb55e9c2cfc5f243024f`: PASS; x64 guard / Restore / Build / Test all SUCCESS.
- Build #62 — run `33942111178`, SHA `362ff6ef3993f9392d437ce8339ce4effda91cfb`: currently IN PROGRESS at this checkpoint. Do not call the read-plan tranche PASS until the run completes successfully and required steps are checked.

Workflow rule: while a run is in progress, do independent useful work only. If no useful independent work remains, stop with the exact run ID/SHA/gate. Do not empty-poll Actions.

## Clipboard capture stack now on `main`

### Listener / lifecycle boundary

DONE and previously CI-verified:

- shared resident message-only HWND for hotkey and clipboard messages;
- bounded coalescing `ClipboardCaptureQueue` with capacity 1 / DropOldest / single reader;
- monitoring starts only after protected access reaches UNLOCKED and stops when protected access ends;
- capture request preserves `EventTimeContext`.

### Source application resolution

DONE and CI-verified:

- source resolution occurs outside WndProc;
- clipboard-owner HWND → process ID → best-effort executable path;
- inaccessible executable path preserves PID with nullable path;
- SourceApplication remains distinct from InvocationApplication.

### Policy boundary

DONE at runtime-contract level:

- global + optional per-application policy model;
- tri-state per-parameter semantics: Inherit / Allow / Deny;
- `IClipboardCapturePolicyProvider` and repository boundary exist;
- repository boundary receives the resolved `ClipboardSourceApplication`, but persistent Application identity/key and concrete policy storage schema remain intentionally unspecified;
- no policy fields were silently added to `ApplicationSettings`.

### Retained clipboard snapshot / discovery

DONE and CI-verified:

- format discovery holds one `IClipboardContentSnapshot` backed by the same Windows `DataPackageView`;
- later readers consume that retained snapshot instead of calling `Clipboard.GetContent()` again;
- format selection only passes formats that are present and effectively `Allow`;
- selection preserves `MaxBytes` as metadata but does not interpret or enforce it.

### Standard content-reader capabilities

Available on `main`:

- Text / HTML / RTF reader capability;
- Bitmap reader capability that uses the retained `DataPackageView`, opens the bitmap stream, and normalizes to PNG with the existing `PngImageNormalizer`;
- WebLink / ApplicationLink reader capability; deprecated generic URI format is not used;
- StorageItems metadata reader for Explorer/file-list clipboard data, producing only metadata (`FullPath`, `Name`, `Extension`, directory flag, item order, preferred Copy/Move/Link/Unknown operation). File contents are never copied by this reader.

These are capability adapters only. They are not yet automatically executed by a background capture worker.

### Reader routing / read planning

Current state:

- `ClipboardContentReaderRouter` routes a `ClipboardSelectedFormat` to exactly one known reader kind: Text, PngImage, Link, or StorageItems;
- ambiguous reader support fails closed;
- unsupported formats return no route rather than being silently read by a fallback;
- `ClipboardContentReadPlanStage` separates routable formats from explicit `UnsupportedFormats` while preserving original `ClipboardSelectedFormat`, including `MaxBytes`;
- no payload read occurs while creating the plan.

## Prepared stacked commits not yet on `main`

### 1. Windows host read-plan composition

Branch: `feat/clipboard-read-plan-composition`

Commit:

- `beb089bcbd24fc04a2f6f4fe79d1de1b034e1fe7`
- message: `feat: compose clipboard content read planning in Windows host`

Parent: `362ff6ef...`.

Change is intentionally small:

- `ResidentWindowsHost` constructs `ClipboardContentReaderRouter` from the four existing reader capabilities;
- constructs/exposes `ClipboardContentReadPlanStage`;
- does not automatically read payload or start a consumer loop.

Promotion rule: only after Build #62 is PASS, confirm branch is ahead 1 / behind 0, fresh-check `main`, then fast-forward non-force and obtain a new Build gate.

### 2. Single-shot capture read-planning pipeline

Branch: `feat/clipboard-capture-read-planning-pipeline`

Commit:

- `e20fc6006fa5e29deb5f4b1bc5c5a29e75f255cf`
- message: `feat: add clipboard capture read-planning pipeline`

Parent: `beb089bc...`.

This adds a separate wrapper:

`capture/source/policy/discovery/selection -> read-plan`

The existing `ClipboardCapturePipeline` contract is unchanged. The wrapper still performs zero content reads; it only returns routes + unsupported formats. The Windows host gets an explicit factory for this single-shot planning pipeline. No background loop is started.

## Important unresolved blocker before actual payload execution

`MaxBytes` semantics are not yet defined precisely enough for safe enforcement.

`docs/OPEN_QUESTIONS.md` explicitly leaves concrete default limits unresolved for Text, HTML, RTF, images, custom binary, and CF_HDROP. More importantly, the current docs do not define whether a configured size limit applies to:

- raw clipboard representation size;
- decoded text byte count / character count;
- normalized PNG output size;
- stored representation size;
- or another phase-specific measurement.

Do not invent this semantic in code. Until it is specified, content readers may remain capability adapters and read-planning may remain metadata-only, but an automatic payload-read/enforcement stage should not be promoted.

## Other still-open runtime work

- concrete persistent Application identity/catalog and capture-policy storage implementation;
- discovered application/format catalog persistence;
- actual content-read execution stage;
- exact size-limit semantics and default values;
- HTML/RTF SearchText normalization/extraction;
- custom registered/private binary reader and external-file handling for explicitly enabled formats;
- deduplication and persistence boundary;
- minimal history schema, then full Current/Archive/catalog schema;
- FTS/search;
- Current → Archive transfer/split/catalog rebuild;
- auto-lock end-to-end capture-worker teardown once a worker exists;
- InvocationApplication resolution;
- password/MasterKey change and remaining recovery procedures;
- final installer/package and installed-app native-load/launch evidence.

## Exact resume procedure

On the next development continuation:

1. Fresh-fetch Build #62 run `33942111178` and current `main`.
2. If Build #62 completed SUCCESS, fetch jobs and require:
   - Verify x64-only implementation scope = SUCCESS;
   - Restore = SUCCESS;
   - Build = SUCCESS;
   - Test = SUCCESS.
3. Only then mark `362ff6ef...` read-plan tranche PASS.
4. Compare `feat/clipboard-read-plan-composition` against fresh `main`; expected ahead 1 / behind 0.
5. Fresh TOCTOU `main` immediately before write and fast-forward to `beb089bc...` with `force=false`.
6. Obtain Build evidence for that commit.
7. After that PASS, repeat the same process for `e20fc600...`.
8. Do not cross into actual payload execution until the size-limit semantic is explicitly settled or a purely non-enforcing boundary can be added without implying a semantic choice.

## Branch hygiene

Several historical feature branches may remain after their commits became ancestors of `main`. They are not source of truth. Cleanup is optional and separate from functional development; never use a stale feature branch instead of fresh `main`.
