# NEW CHAT HANDOFF — Clipensk

Дата checkpoint: 2026-09-05.

Этот файл — durable operational checkpoint по `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`. Изменяемое состояние GitHub всегда имеет приоритет. Перед любой durable записью обязательна fresh TOCTOU-проверка `main`; `main` никогда не обновлять с `force=true`.

## A. Project identity

- Проект: **Clipensk** — Open Source Windows clipboard-history manager.
- Репозиторий: `lukindv77/Clipensk`.
- Основная ветка: `main`.
- Поддерживаемая архитектура продукта: **только Windows x64 (AMD64)**. ARM64 вне product scope и не должен возвращаться в code/CI/runtime/docs как будущий target.
- Runtime contract: `Platforms=x64`, `RuntimeIdentifiers=win-x64`.
- Текущий development host: unpackaged (`WindowsPackageType=None`).
- Финальная схема распространения MSIX/portable/оба не выбрана.

Authoritative документы:

- `AGENTS.md`;
- `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`;
- `docs/REQUIREMENTS.md`;
- `docs/ARCHITECTURE.md`;
- `docs/CRYPTOGRAPHY.md`;
- `docs/NATIVE_SQLCIPHER_BUILD.md`;
- `docs/OPEN_QUESTIONS.md`;
- этот `docs/HANDOFF_CURRENT.md`.

## B. Operating rules

- GitHub repository и GitHub Actions — source of truth для mutable state.
- Перед durable write делать fresh TOCTOU `main`.
- Никогда не force-update `main`.
- Работать failure-driven; не вносить speculative fixes.
- Не объявлять PASS/build/release/encryption gate без фактического evidence.
- `WM_CLIPBOARDUPDATE` WndProc должен оставаться signal/enqueue only: никакого clipboard read, source resolution, DB или persistence в handler.
- Clipboard monitoring разрешён только при protected data access = UNLOCKED.
- `SourceApplication` и `InvocationApplication` — разные понятия; InvocationApplication остаётся отдельной будущей задачей.
- Event time должен сохраняться через утверждённый `EventTimeContext`: UtcTimestamp, LocalOffset, WindowsTimeZoneId, CalendarDate.
- Не проектировать полную history/catalog/archive schema внутри capture tranches.
- Defaults форматов и конкретные size limits пока открыты; не hardcode их до отдельного решения.

### GitHub Actions waiting rule

Если Actions run ещё идёт:

1. При наличии независимой полезной работы — делать её.
2. Если другой работы нет — допускается синхронно ждать завершения Actions не более **2 минут**.
3. Если после 2 минут run не завершён — остановиться, сообщить run ID/SHA/gate и дать краткую сводку: что сделано, что PASS, что UNVERIFIED и точную resume point.
4. Бесконечный/пустой polling запрещён.
5. Подготовленные параллельные ветки не продвигать в `main`, пока это сделает текущий evidence run нерелевантным.

## C. Current authoritative source state

На момент подготовки этого checkpoint:

- `main`: `134c209cc5fad29aeb9b37f5cb2a5cda542ebc85`
- tree: `244b791ec63457e08446ccdee913c86fd22db749`
- commit: `feat: compose clipboard capture pipeline in Windows host`
- Build #56: run `33940889760`, SHA `134c209c...`, на момент подготовки handoff **IN_PROGRESS / UNVERIFIED**.

Важно: handoff готовится в отдельной docs branch поверх `134c209c...`. Если Build #56 завершится и docs commit будет продвинут в `main`, следующий чат обязан fresh проверить фактический `main` и новый Actions run вместо предположений из этого файла.

## D. Verified CI chain for capture pipeline

Последовательно подтверждены managed Build gates:

- Build #44 / run `33937739276` / `631e2dc...`: clipboard listener boundary — SUCCESS.
- Build #45 / run `33938504886` / `d76e7ec...`: protected-access binding — SUCCESS.
- Build #47 / run `33939070986` / `bc02cb58...`: event-time context — SUCCESS.
- Build #48 / run `33939545079` / `d60d461c...`: source application resolution — SUCCESS.
- Build #49 / run `33939777749` / `1d4f9d07...`: capture policy boundary — SUCCESS.
- Build #50 / run `33939865383` / `9c246d46...`: format discovery — SUCCESS.
- Build #51 / run `33940244402` / `6e858354...`: retained clipboard content snapshot — SUCCESS.
- Build #52 / run `33940356864` / `ab04088d...`: effective format selection — SUCCESS.
- Build #53 / run `33940486854` / `7817134c...`: capture policy provider boundary — SUCCESS.
- Build #54 / run `33940583892` / `6fee56bb...`: single-shot capture pipeline — SUCCESS.
- Build #55 / run `33940726841` / `106b8fa3...`: standard Text/HTML/RTF reader capability — SUCCESS.

Для каждого перечисленного SUCCESS проверены обязательные шаги: x64-only scope guard, Restore, Build, Test.

Build #56 / run `33940889760` / `134c209c...` — текущий gate composition tranche, пока не считать PASS без fresh evidence.

## E. Clipboard capture pipeline implemented so far

### 1. Listener and queue — DONE

- `ResidentMessageWindow` принимает `WM_HOTKEY` и `WM_CLIPBOARDUPDATE`.
- `ClipboardUpdateMonitor` использует `AddClipboardFormatListener` / `RemoveClipboardFormatListener`.
- WndProc только формирует/enqueue capture request.
- `ClipboardCaptureQueue` bounded capacity 1, `DropOldest`, single reader; pending updates coalesce to latest.
- Monitoring стартует только после UNLOCKED и останавливается при уходе из protected access.

### 2. Event-time context — DONE

Commit `bc02cb58a5a9c7357358c549841cb772746128ec`.

`ClipboardCaptureRequest` сохраняет полный `EventTimeContext`, захваченный на event boundary. Не терять его в следующих стадиях.

### 3. Source Application resolution — DONE

Commit `d60d461c35b6b4af329b800e6d55d837a42c57c7`.

- `ClipboardSourceApplication(ProcessId, ExecutablePath?)`.
- Windows resolver: `GetClipboardOwner` → PID → `QueryFullProcessImageName`.
- Если path недоступен, PID всё равно сохраняется; source может быть полностью unknown.
- Resolution выполняется после dequeue, не в WndProc.

### 4. Capture policy model/evaluator — DONE

Commit `1d4f9d07af177a2bdacab16c1c7c8dd99dcd707e`.

- `ClipboardCapturePolicyRule`: Inherit / Allow / Deny.
- Global policy + optional per-application policy.
- Per-format policy содержит Capture rule + optional `MaxBytes`.
- Application overrides global; Inherit сохраняет base value.
- Отсутствующая настройка не превращается автоматически в Allow/Deny.
- Конкретные default formats и default limits не выбраны.

### 5. Format discovery + retained DataPackageView — DONE

Commits:

- `9c246d46d26ac2554777505a8b10a97ddf577488` — discovery;
- `6e85835460536440cbb1597d818d6a93b8697aef` — retained content snapshot.

Rules:

- Clipboard read для discovery происходит только если whole-capture effective policy = Allow.
- Deny и unresolved Inherit fail-closed и не вызывают clipboard reader.
- `WindowsClipboardContentSnapshot` удерживает один `DataPackageView`; следующие readers должны использовать именно его, а не второй `Clipboard.GetContent()`, чтобы не прочитать уже другое clipboard state.

### 6. Effective format selection — DONE

Commit `ab04088db4c25da433c9b5156a50744312d1ad89`.

- Selection идёт только из реально доступных snapshot formats.
- Пропускаются только форматы с effective per-format `Allow`.
- Missing/Inherit/Deny не проходят.
- Дубликаты format IDs удаляются ordinal-сравнением.
- `MaxBytes` переносится как metadata, но ещё не интерпретируется и не enforce'ится.

### 7. Policy provider runtime boundary — DONE

Commit `7817134c5e84785eaef7ef2d0bf1a58afda4a603`.

`IClipboardCapturePolicyProvider` возвращает `ClipboardCapturePolicySet` для уже resolved `ClipboardCaptureContext`:

- обязательный GlobalPolicy;
- optional ApplicationPolicy.

`ClipboardCapturePolicyResolutionStage` получает policies, merge'ит их существующим evaluator и сохраняет capture context. CancellationToken пробрасывается provider'у.

Concrete persistence/provider storage пока **не реализован**. Не встраивать policy в `ApplicationSettings` без отдельного архитектурного решения: документы требуют persistent `Application` + `ApplicationCapturePolicy`, но конкретное место хранения ещё не зафиксировано.

### 8. Single-shot capture pipeline — DONE / CI PASS

Commit `6fee56bbb0a5bcd649c301d9c2cf065f31e7ecd8`.

`ClipboardCapturePipeline.ProcessNextAsync()` выполняет ровно один capture:

```text
Capture Queue
  ↓
Source Application Resolution
  ↓
Policy Provider + Evaluation
  ↓
Content Snapshot / Format Discovery
  ↓
Effective Format Selection
```

Tests подтверждают порядок стадий, сохранение request/source context и отсутствие format read при whole-capture Deny.

Это **не** background worker/pump и **не** end-to-end persistence pipeline.

### 9. Standard text reader capability — DONE / CI PASS

Commit `106b8fa31709bea3631c10663ffce061d0ff953d`.

- Core boundary: `IClipboardTextContentReader`.
- Windows adapter поддерживает только стандартные `StandardDataFormats.Text`, `.Html`, `.Rtf`.
- Использует retained `WindowsClipboardContentSnapshot.Content` / один `DataPackageView`.
- Text → `GetTextAsync()`; HTML → `GetHtmlFormatAsync()`; RTF → `GetRtfAsync()`.
- Adapter не вызывается из capture pipeline автоматически.
- Нет default enablement форматов.
- Нет size-limit enforcement.
- HTML/RTF raw representation ещё не превращается в SearchText.

### 10. Windows-host pipeline composition — CURRENT GATE

Commit `134c209cc5fad29aeb9b37f5cb2a5cda542ebc85`.

`ResidentWindowsHost.CreateCapturePipeline(IClipboardCapturePolicyProvider)` собирает существующие source/policy/discovery/selection stages только при явном provider от caller.

Не делает:

- не выбирает persistence;
- не создаёт default policy;
- не запускает pipeline автоматически;
- не создаёт resident worker;
- не читает payload.

Build #56 должен быть проверен до объявления PASS.

## F. Protected storage / SQLCipher state

Protected-storage lifecycle DONE:

- first-run DataRoot;
- `storage-crypto.json`;
- Argon2id v1.3, 64 MiB / 3 iterations / 4 lanes / 16-byte salt / 32-byte MasterKey;
- MasterKey verifier;
- LOCKED / UNLOCKING / UNLOCKED;
- password never persisted;
- protected `Current/current.db` + `Current/storage-catalog.db`;
- atomic initial pair;
- `DatabaseIdentity` validation;
- partial Current/Catalog fail-closed;
- archive-presence recovery gate.

SQLCipher production boundary DONE / VERIFIED:

- `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.sqlcipher`;
- self-built `sqlcipher.dll`;
- raw 32-byte key via `sqlite3_key`;
- `cipher_compatibility=4`, `cipher_memory_security=ON`;
- `cipher_version >= 4.12.0`, `cipher_status=1`, sqlite master, quick_check, DatabaseIdentity;
- SQLCipher Community 4.17.0 commit `810db22f575ee7cf94ea96a3e91622b5fcece3dc`;
- OpenSSL 3.5.8 tar SHA-256 `a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2`.

Native evidence:

- Native SQLCipher #9 run `33906431371` SUCCESS.
- Native SQLCipher #10 run `33935810021` SUCCESS.
- `clipensk-sqlcipher-win-x64` artifact ID `9960499242`.
- `clipensk-app-win-x64-unpackaged` artifact ID `9960500027`.

Verified unpackaged x64 staging есть. Это не final installer/installed-app launch proof. Native/runtime tranche не трогать без нового factual failure.

## G. Important unresolved work

Не считать готовым:

- Build #56 evidence для `134c209c...`;
- concrete policy persistence/provider implementation;
- persistent Application identity/key semantics для per-app policy;
- exact default format set;
- exact size-limit semantics/default values (`OPEN_QUESTIONS.md` §6);
- text payload read stage с enforcement limits;
- HTML/RTF SearchText projection;
- image reader + existing PNG normalization wiring;
- CF_HDROP reader/metadata;
- URL/WebLink/ApplicationLink handling;
- unknown/custom binary reader only after explicit enablement;
- normalization/deduplication/persistence;
- resident background processing loop и lock/unlock cancellation/teardown;
- stale-capture handling across lock/unlock;
- minimal history schema;
- full history/catalog/archive schema;
- FTS/search;
- Current→Archive transfer/split/catalog rebuild;
- InvocationApplication runtime resolution;
- password/MasterKey change;
- crypto metadata recovery;
- partial Current/Catalog recovery;
- installed-app native-load/launch evidence;
- MSIX vs portable distribution;
- byte-for-byte reproducibility;
- hot backup/snapshot (сознательно deferred);
- branch hygiene.

## H. Exact resume point

При следующем `продолжай`:

1. Fresh fetch `main`.
2. Fresh fetch Build #56 run `33940889760` и убедиться, что SHA = `134c209cc5fad29aeb9b37f5cb2a5cda542ebc85`.
3. Если completed/success — fetch jobs и проверить обязательные шаги: x64-only guard, Restore, Build, Test = SUCCESS. Только тогда composition tranche PASS.
4. Если failed — определить первый failed step, получить только нужные logs, внести минимальный failure-driven fix, fresh TOCTOU и non-force push.
5. Если run ещё идёт — делать независимую полезную работу; если её нет, можно ждать не более 2 минут по правилу выше.
6. Docs branch `docs/refresh-capture-handoff-20260905` содержит этот актуальный handoff; продвигать её в `main` только после проверки Build #56 и fresh TOCTOU.
7. Следующий code tranche после этого должен оставаться до payload-size semantics безопасным: не подключать raw text reader в автоматический capture pipeline без решения, как `MaxBytes` измеряется/enforce'ится.

## I. Invariants not to regress

- ARM64 не возвращать.
- Password не persist'ить.
- MasterKey не хранить plaintext.
- Protected DB open/identity failure оставляет приложение LOCKED.
- Clipboard listener выключен при LOCKED.
- WndProc signal/enqueue only.
- `EventTimeContext` сохранять от event boundary.
- SourceApplication != InvocationApplication.
- Retained `DataPackageView` использовать для discovery/read одного capture; не делать второй `Clipboard.GetContent()` на следующей стадии.
- Не invent'ить format defaults/limits.
- Не invent'ить history schema в capture tranche.
- Не считать managed Build native encryption evidence.
- Не объявлять PASS без точного Actions evidence.
