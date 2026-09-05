# NEW CHAT HANDOFF — Clipensk

Checkpoint: 2026-09-05.

Этот файл — durable operational checkpoint для продолжения разработки в новом чате. Mutable state всегда перепроверяется по GitHub/GitHub Actions; перед любой durable записью в `main` обязательна fresh TOCTOU-проверка.

## A. Project identity / invariants

- Проект: **Clipensk** — Open Source Windows clipboard-history manager.
- Репозиторий: `lukindv77/Clipensk`.
- Product scope: **только Windows x64 (AMD64)**. ARM64 полностью вне scope и не должен возвращаться в code/CI/RuntimeIdentifiers/Platforms/native artifacts/docs, кроме явной формулировки unsupported/out-of-scope.
- Runtime contract: `Platforms=x64`, `RuntimeIdentifiers=win-x64`.
- Текущий development host: unpackaged (`WindowsPackageType=None`).
- Финальная distribution-модель MSIX / portable / обе — не выбрана.
- `WM_CLIPBOARDUPDATE` WndProc остаётся signal/enqueue only: никаких clipboard reads, source resolution, DB/persistence или тяжёлой работы.
- Clipboard monitoring работает только при protected data access = `UNLOCKED`.
- Один Windows `DataPackageView` удерживается от discovery до payload reader calls; повторный `Clipboard.GetContent()` на reader stage запрещён.
- Event boundary сохраняет `EventTimeContext`: UTC timestamp + local offset + Windows time-zone ID + calendar date.
- `SourceApplication` и `InvocationApplication` — разные понятия/runtime boundaries.
- Не придумывать full history/catalog/archive schema внутри capture tranches.
- Не вводить неизвестные product defaults для clipboard formats или numerical size limits.

Authoritative docs:

- `AGENTS.md`
- `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`
- `docs/REQUIREMENTS.md`
- `docs/ARCHITECTURE.md`
- `docs/CRYPTOGRAPHY.md`
- `docs/NATIVE_SQLCIPHER_BUILD.md`
- `docs/CLIPBOARD_CAPTURE_SIZE_LIMITS.md`
- `docs/OPEN_QUESTIONS.md`
- `docs/HANDOFF_CURRENT.md`

## B. Mandatory workflow

- GitHub repository + GitHub Actions = source of truth для mutable state.
- Перед каждым durable write в `main`: fresh fetch `main` → убедиться в ожидаемом parent/compare → fast-forward only, `force:false`.
- `main` никогда не force-update.
- Работа failure-driven; без speculative fixes.
- PASS/build/release/encryption gate объявлять только по точному CI evidence.
- Пока run выполняется, делать только независимую полезную работу. Если такой работы нет — остановиться с exact run/SHA/gate; не делать бессмысленный polling loop.
- Native SQLCipher/runtime staging не трогать без фактического failure.

## C. Current authoritative source state

На момент checkpoint fresh GitHub state:

- `main = 16533885886a4a014d38fb3627ba327672646e7a`
- commit: `feat: add accepted clipboard capture sink boundary`
- parent: `40ba05509af8f6d20bc7be606f985b4070977f30`

Последний подтверждённый managed CI:

- **Build #75**
- run `33949609784`
- SHA `16533885886a4a014d38fb3627ba327672646e7a`
- conclusion `SUCCESS`
- mandatory steps `Verify x64-only implementation scope`, `Restore`, `Build`, `Test` — все `SUCCESS`.

Этот handoff специально подготовлен в отдельной docs-ветке, а не продвинут в `main`, чтобы сохранить следующий feature commit как чистый fast-forward.

## D. Protected storage / SQLCipher foundation

DONE / VERIFIED и без фактического failure не переделывать:

- first-run DataRoot + `storage-crypto.json`;
- Argon2id v1.3: 64 MiB / 3 iterations / 4 lanes / 16-byte salt / 32-byte MasterKey;
- password не persistится; LOCKED / UNLOCKING / UNLOCKED;
- protected `Current/current.db` и `Current/storage-catalog.db`;
- atomic initial pair + `DatabaseIdentity` validation;
- partial Current/Catalog fail-closed + archive-presence recovery gate;
- `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.sqlcipher` + self-built `sqlcipher.dll`;
- raw 32-byte key через `sqlite3_key`;
- `cipher_compatibility=4`, `cipher_memory_security=ON`;
- SQLCipher Community 4.17.0 commit `810db22f575ee7cf94ea96a3e91622b5fcece3dc`;
- OpenSSL 3.5.8 SHA-256 `a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2`;
- Native SQLCipher #10 run `33935810021` — SUCCESS;
- verified unpackaged x64 staging with exact `sqlcipher.dll`, no `e_sqlcipher.dll`.

Это не финальное installer/installed-launch evidence.

## E. Clipboard capture pipeline — current completed state

Старые verified tranches через Build #65 остаются действительными: listener/protected-access binding, event-time capture, SourceApplication resolution, policy evaluator/provider/repository boundary, retained snapshot, format discovery/selection, routing/read planning, standard readers Text/HTML/RTF, Bitmap→PNG, WebLink/ApplicationLink, StorageItems metadata, InvocationApplication resolution.

Recent chain after Build #65:

- `bd9c56761ec638b24742d7214712ae17e1d1ecd5` — docs handoff refresh.
- `98306b41f2c4f9fae311c13ceaf83dad3a250208` — record unresolved clipboard size-limit semantics question.
- `1a59b22c758558ffeac0c73c0f6883580a49040f` — define canonical clipboard size-limit semantics + Core measurement utility. Build #68 exposed only xUnit theory-data binding failure.
- `eec705056a68e5756249c8cbb0b86ff446767ec2` — minimal test-only fix; Build #69 run `33948950769` — PASS.
- `0e3f149b2cf903afbd2f88e5e9a8ea5f76ed22b9` — execute clipboard content reads with canonical size-limit enforcement; Build #70 run `33949077346` — PASS.
- `6cd0a6a2841bd3fb94d8d6be2dff7b12581858de` — compose `ClipboardContentReadExecutionStage` in Windows host; Build #71 run `33949171614` — PASS.
- `6bca08f3e9721fa5cf6d35c56645d0a0b0f528b8` — single-shot `capture → read-plan → execute`; Build #72 run `33949273923` — PASS.
- `02d9a63cf58311a20a93ef47db182f6729fbd726` — cancellation check after each async read so cancelled capture cannot publish an execution result; Build #73 run `33949362477` — PASS.
- `40ba05509af8f6d20bc7be606f985b4070977f30` — `ClipboardAcceptedCapture` projection: only actually captured payloads become persistence candidates; rejected/deferred/unsupported state remains transient; Build #74 run `33949518140` — PASS.
- `16533885886a4a014d38fb3627ba327672646e7a` — `IClipboardAcceptedCaptureSink` + accepted-only sink stage; sink is not invoked when there is no accepted payload; no concrete DB schema/storage implementation; Build #75 run `33949609784` — PASS.

Still **no automatic background capture worker** and **no concrete history persistence implementation**.

## F. `MaxBytes` semantics — now fixed technically

`docs/CLIPBOARD_CAPTURE_SIZE_LIMITS.md` is the technical contract. Numerical defaults remain unresolved product choices.

`MaxBytes` measures canonical capture bytes for the specific selected format, not DB/FTS/index overhead:

- Text / HTML / RTF → UTF-8 bytes of the original text representation returned by the reader.
- WebLink / ApplicationLink → UTF-8 bytes of the canonical URI representation used by capture.
- Bitmap → bytes of the normalized PNG result.
- SearchText, DB row metadata, indexes and FTS overhead do not count toward the limit.
- StorageItems / CF_HDROP canonical metadata byte representation is **not yet defined**. Therefore `StorageItems` with configured `MaxBytes` is deferred without reading payload; with `MaxBytes = null` metadata can be read.
- Custom registered/private binary remains unsupported unless explicitly implemented later.

Do not reinterpret this contract silently.

## G. Current execution / persistence boundary

Current `main` has an explicit single-shot chain capable of:

`queue → source resolution → policy → retained snapshot/discovery → effective format selection → routing/read plan → payload read → canonical size enforcement → execution result → accepted-capture projection → accepted-only sink boundary`.

Important behavior:

- Text/HTML/RTF, links and normalized PNG are actually readable and size-enforced.
- StorageItems with no configured size limit returns metadata only; file contents are never copied/read.
- StorageItems with configured `MaxBytes` is deferred without a payload read.
- Unsupported selected formats remain explicit unsupported state and never reach the accepted sink.
- Oversize payloads remain size-rejected and never reach the accepted sink.
- Post-read cancellation prevents publishing a cancelled result.
- Sink contract carries `CancellationToken`; transactional cancellation semantics belong to the future concrete storage implementation.
- No auto-run/background consumer yet.

## H. Prepared next feature — NOT on main

Branch:

- `feat/clipboard-reader-cancellation`

Prepared commit:

- `b18e7f32eaa5085a06d1a0dce8789c55feda9cdf`
- message: `feat: propagate cancellation into clipboard readers`
- parent: current `main` `16533885886a4a014d38fb3627ba327672646e7a`
- fresh compare at checkpoint: **ahead 1 / behind 0**.

What it changes:

- passes `CancellationToken` through all four Core reader interfaces;
- `ClipboardContentReadExecutionStage` passes the same token into readers;
- Windows Text/HTML/RTF, link and StorageItems WinRT operations use cancelable `AsTask(token)`;
- PNG path propagates cancellation through `GetBitmapAsync`, stream open, decoder, software bitmap, encoder flush and data read/normalization chain;
- existing post-await cancellation checks remain;
- Core tests/stubs verify that execution passes the exact token to readers.

This commit is **UNVERIFIED on `main`**. Do not call it PASS until its own main Build succeeds.

## I. Exact resume instructions for next chat

When user says `продолжай` / `продолжай разработку`:

1. FIRST fresh-fetch `main` and current Actions state.
2. Expected starting `main`: `16533885886a4a014d38fb3627ba327672646e7a`; do not assume if GitHub differs.
3. Confirm Build #75 run `33949609784` remains SUCCESS if needed; mandatory steps were x64 guard / Restore / Build / Test.
4. Fresh compare `feat/clipboard-reader-cancellation` vs actual `main`.
5. If still ahead 1 / behind 0 with parent `16533885…`, perform another **fresh TOCTOU immediately before durable write**.
6. Fast-forward `main` to `b18e7f32eaa5085a06d1a0dce8789c55feda9cdf`, `force:false`.
7. Fetch the exact newly-created Build run; never assume its run number.
8. Require `Verify x64-only implementation scope`, `Restore`, `Build`, `Test` = SUCCESS before declaring reader-level cancellation PASS.
9. If it fails, inspect first failed step/log and apply the smallest failure-driven fix.
10. After that gate, safe next design/code areas are an explicit single-shot accepted-capture delivery wrapper/composition and then concrete persistence prerequisites. Do **not** start a background worker until protected-access cancellation + real sink/policy source lifecycle are ready.

## J. Current blockers / remaining major work

- stable persistent Application identity/key semantics are unresolved; do not use PID or executable path as a durable key by assumption;
- concrete policy repository/storage schema blocked on that identity decision;
- concrete accepted-capture persistence/history schema still absent;
- exact StorageItems canonical byte representation for `MaxBytes` still unresolved;
- HTML/RTF `SearchText` extraction/normalization algorithm unresolved;
- custom/private binary reader absent;
- deduplication + external payload placement/persistence absent;
- real lock-aware background capture worker / cancellation/teardown absent;
- stale capture handling across lock/unlock absent;
- minimal history schema, FTS/search, Current→Archive transfer/split/catalog rebuild absent;
- password/MasterKey change, crypto metadata recovery, partial Current/Catalog recovery remain;
- installed-app native-load/launch evidence, MSIX vs portable choice, byte-for-byte reproducibility remain.

## K. This handoff branch

This refreshed handoff is intentionally stored on branch:

- `docs/new-chat-handoff-20260905-1420`

It must not be merged into `main` before the prepared reader-cancellation fast-forward unless the next chat deliberately chooses to rebuild/rebase that feature commit, because moving `main` with a docs-only commit would make `b18e7f32…` no longer a direct fast-forward child.
