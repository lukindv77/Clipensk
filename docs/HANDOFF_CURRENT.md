# NEW CHAT HANDOFF — Clipensk

Дата checkpoint: 2026-09-05.

Этот файл является durable operational checkpoint по правилу `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`. Изменяемое состояние GitHub всегда имеет приоритет; перед следующей durable записью обязательна fresh TOCTOU-проверка.

## A. Project identity

- Проект: **Clipensk** — Open Source Windows clipboard-history manager.
- Репозиторий: `lukindv77/Clipensk`.
- Основная ветка: `main`.
- Source state непосредственно перед этим handoff docs-only checkpoint: `d76e7ec699980cea006ebeb33d197ba877c6ab4b` (`feat: bind clipboard monitoring to protected access`).
- Поддерживаемая архитектура продукта: **только Windows x64 (AMD64)**. ARM64 вне product scope.
- App runtime contract: `Platforms=x64`, `RuntimeIdentifiers=win-x64`, текущий development host unpackaged (`WindowsPackageType=None`).
- Финальная схема распространения MSIX/portable/оба ещё не выбрана.
- Fresh проверка перед handoff: open PR = 0, open Issues = 0.

Authoritative документы:

- `AGENTS.md`;
- `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`;
- `docs/REQUIREMENTS.md`;
- `docs/ARCHITECTURE.md`;
- `docs/CRYPTOGRAPHY.md`;
- `docs/NATIVE_SQLCIPHER_BUILD.md`;
- `docs/OPEN_QUESTIONS.md`;
- этот `docs/HANDOFF_CURRENT.md`.

## B. User intent

Конечная цель: довести Clipensk до рабочего защищённого Windows x64 приложения с резидентным журналированием clipboard, поиском, Current/Archive storage, обслуживанием и безопасным password → MasterKey lifecycle.

Постоянные правила работы:

- GitHub repository/Actions — primary source of truth для изменяемого состояния;
- перед durable write делать fresh TOCTOU-проверку `main`;
- работать failure-driven, не вносить speculative fixes;
- не объявлять PASS/build/release/encryption gate без фактического evidence;
- не возвращать ARM64 implementation;
- не переоткрывать уже утверждённую crypto/storage архитектуру без подтверждённой причины;
- managed tests не являются доказательством SQLCipher encryption;
- **не выполнять пустой polling GitHub Actions**: пока run идёт, делать другую независимую полезную работу; если такой работы нет или безопасно продолжать нельзя — подвести итог, сообщить run ID и ждать следующей команды пользователя; на следующем `продолжай` первым делом fresh проверить этот run;
- параллельную работу во время Actions допускается готовить в отдельной ветке, но нельзя продвигать её в `main`, если это отменит/перезапустит текущий evidence run.

## C. Current authoritative state

### Source / branches

Fresh pre-handoff state:

- `main`: `d76e7ec699980cea006ebeb33d197ba877c6ab4b`, tree `3c3937eb49ae78fc246a6fa3358e6ebc93210c23`, branch unprotected.
- `feat/capture-time-context`: `a93fe4d7ea93852d0064ee07075cc4c64a52338a` (`feat: preserve clipboard event time context`). Перед handoff docs commit она была **ahead 1 / behind 0** относительно `d76e7ec...` и меняла только:
  - `src/Clipensk.Core/Clipboard/ClipboardCaptureRequest.cs`;
  - `src/Clipensk.Windows/Clipboard/ClipboardUpdateMonitor.cs`;
  - `tests/Clipensk.Core.Tests/ClipboardCaptureQueueTests.cs`.
- `feat/clipboard-listener-boundary`: `631e2dc56f0f1bdc03bfe1be56883b1dad9cd9ce`, уже ancestor `main`; ветка функционально избыточна.
- `chore/native-provenance-hardening` и `chore/x64-runtime-delivery` остаются как старые рабочие ветки уже завершённых tranches; их не использовать как source of truth вместо `main`.
- Во время подготовки handoff была ошибочно создана no-op ветка `__noop_should_not_create__`, указывающая на `d76e7ec...`. Текущий connector не предоставляет delete-ref action; ветка не содержит уникальных commits и безопасна для последующего удаления/игнорирования.

**Важно:** этот handoff публикуется docs-only commit поверх `d76e7ec...`, поэтому после записи `main` изменится и `feat/capture-time-context` перестанет быть fast-forward относительно нового `main`. Новый чат обязан fresh сравнить ветку с `main`; ожидаемая логика — перенести один feature commit поверх свежего docs-only `main` без изменения его содержимого, а не force-merge старую базу.

### Managed CI

- Build #44, run `33937739276`, SHA `631e2dc56f0f1bdc03bfe1be56883b1dad9cd9ce`: `SUCCESS` — x64 guard / Restore / Build / Test.
- Build #45, run `33938504886`, SHA `d76e7ec699980cea006ebeb33d197ba877c6ab4b`: `SUCCESS` — x64 guard / Restore / Build / Test.
- `feat/capture-time-context` **не считать PASS**: отдельного подтверждённого CI evidence для этого commit нет; после переноса/merge в `main` нужен новый Build run.

### Native SQLCipher / runtime delivery

Native/provenance/runtime tranche закрыт:

- Native SQLCipher #9, run `33906431371`, SHA `9b8f8aef6080d858d2433be022127b157758ec9a`: `SUCCESS`; подтвердил x64 source build, provenance collector и production encrypted-storage smoke.
- Native SQLCipher #10, run `33935810021`, SHA `3a0ee374b6108ffb12d432c5805a1dad07d30af3`: `SUCCESS` по всем ключевым шагам:
  - Build pinned SQLCipher x64;
  - Record native SQLCipher x64 provenance;
  - Publish SQLCipher smoke host;
  - Verify encrypted storage x64;
  - Publish unpackaged Clipensk x64 runtime;
  - Upload native evidence;
  - Upload unpackaged runtime.
- Artifacts #10:
  - `clipensk-sqlcipher-win-x64`, artifact ID `9960499242`;
  - `clipensk-app-win-x64-unpackaged`, artifact ID `9960500027`.
- Runtime artifact был скачан и проверен: присутствуют `Clipensk.App.exe`, `Clipensk.App.dll`, verified `sqlcipher.dll`; `e_sqlcipher.dll` отсутствует; App EXE и SQLCipher DLL — PE x64 (`0x8664`); `runtime-delivery-manifest.json` содержит `win-x64`; SHA staged DLL совпадает с native manifest; SHA copied native manifest совпадает с ссылкой runtime manifest; provenance repository commit согласован с источником Native #10; native license files присутствуют.

Статус: **x64 unpackaged runtime staging PASS**. Это **не** доказательство финального installer/package и **не** отдельный installed-app runtime-loading/launch PASS.

## D. Current owner / active task

Текущий owner: **продолжить clipboard capture pipeline после уже работающей listener/lifecycle boundary, начиная с переноса полного event-time context в capture request**.

Root gap:

- `WM_CLIPBOARDUPDATE` listener и bounded coalescing queue уже созданы;
- monitoring теперь включается только при доступе к protected data (`UNLOCKED`) и выключается при начале lock;
- но `ClipboardCaptureRequest` в `main` пока содержит только UTC timestamp и теряет уже существующую модель `EventTimeContext` (local offset, calendar date, Windows time-zone ID), необходимую требованиям архивной календарной принадлежности.

Подготовлен fix в `feat/capture-time-context` / `a93fe4d7...`:

- request несёт `EventTimeContext`;
- monitor захватывает `EventTimeContext.CaptureNow()` в момент clipboard update;
- tests обновлены для coalescing и сохранения time context.

Acceptance criteria ближайшего tranche:

1. Fresh проверить `main` после handoff docs commit и состояние `feat/capture-time-context`.
2. Убедиться, что единственное содержательное расхождение feature branch — три ожидаемых файла и один feature commit.
3. Перенести/cherry-pick/rebase этот feature commit поверх свежего `main` без force на `main`.
4. Запустить/получить managed Build evidence и считать tranche PASS только после x64 guard + Restore + Build + Test = SUCCESS.
5. После PASS перейти к следующему capture stage; не смешивать это с ещё не определённой history DB schema.

## E. What has been completed

### First-run / protected storage lifecycle

DONE:

- выбор DataRoot при первом запуске;
- storage-wide `storage-crypto.json`;
- Argon2id v1.3 production profile: 64 MiB, 3 iterations, 4 lanes, 16-byte salt, 32-byte MasterKey;
- MasterKey verifier;
- LOCKED / UNLOCKING / UNLOCKED lifecycle;
- password hint без persistence пароля;
- protected initial `Current/current.db` + `Current/storage-catalog.db`;
- atomic initial pair staging/publish;
- `DatabaseIdentity` validation;
- partial Current/Catalog fail-closed behavior;
- archive-presence recovery gate.

### SQLCipher production boundary

DONE / VERIFIED:

- `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.sqlcipher`;
- self-built `sqlcipher.dll`;
- raw 32-byte MasterKey через `sqlite3_key`;
- `cipher_compatibility=4`, `cipher_memory_security=ON`;
- gates: `cipher_version >= 4.12.0`, `cipher_status=1`, `sqlite_master`, `quick_check`, `DatabaseIdentity`;
- SQLCipher Community 4.17.0 commit `810db22f575ee7cf94ea96a3e91622b5fcece3dc`;
- OpenSSL 3.5.8 tarball SHA-256 `a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2`;
- x64-only source build, provenance and smoke evidence;
- unpackaged x64 publish staging exact verified native DLL + manifests/licenses.

### x64-only scope guard

DONE:

- ARM64 implementation removed;
- App/runtime/native workflow only x64/win-x64;
- managed Build contains permanent x64-only implementation guard;
- intentional ARM64 mentions остаются только в docs как `unsupported/out of product scope`.

### Clipboard listener boundary

DONE on `main`:

- commit `631e2dc56f0f1bdc03bfe1be56883b1dad9cd9ce` — shared `ResidentMessageWindow` receives `WM_HOTKEY` and `WM_CLIPBOARDUPDATE`;
- `ResidentWindowsHost` владеет одним message-only HWND, hotkey service, monitor и queue;
- `ClipboardUpdateMonitor` использует `AddClipboardFormatListener` / `RemoveClipboardFormatListener`;
- message handler не читает clipboard и не выполняет тяжёлую работу, а только enqueue capture request;
- `ClipboardCaptureQueue` — bounded capacity 1, `DropOldest`, single reader; множественные pending updates коалесцируются к последнему;
- Build #44 PASS.

### Protected-access binding

DONE on `main`:

- commit `d76e7ec699980cea006ebeb33d197ba877c6ab4b` — monitoring привязан к lifecycle protected access;
- listener стартует после успешного перехода в `UNLOCKED`;
- listener останавливается при уходе из protected-access state / начале lock;
- unit tests проверяют lifecycle access transition;
- Build #45 PASS.

## F. Current conclusions

1. Product platform: только Windows x64 (AMD64); ARM64 не возвращать.
2. App baseline: .NET 10 + WinUI 3 + Windows App SDK 2.4.0 Stable.
3. Distribution: verified unpackaged x64 staging есть; финальная MSIX/portable стратегия всё ещё отдельное решение.
4. Crypto: один storage-wide MasterKey; пароль не хранится; Argon2id profile v1 зафиксирован.
5. Database encryption: production boundary — self-built SQLCipher Community, не bundled/deprecated `e_sqlcipher`.
6. Metadata verifier сам по себе не unlock gate; нужен успешный protected DB open/identity validation.
7. Provenance подтверждает source/toolchain/run metadata и hashes, но **не** byte-for-byte reproducibility.
8. `WM_CLIPBOARDUPDATE` handler должен оставаться минимальным: signal/enqueue only; чтение форматов, source resolution, policy, normalization и persistence выполняются вне WndProc.
9. Clipboard monitoring запрещён в LOCKED state; запуск происходит только после protected data access.
10. Queue intentionally coalesces pending system notifications; это не history storage queue и не обещает по событию на каждое низкоуровневое WM сообщение.
11. `EventTimeContext` — утверждённая модель времени события: UTC timestamp + local offset + Windows time-zone ID + сохранённая calendar date semantics; capture boundary должен не терять её.
12. Полная history/catalog/archive schema ещё не согласована/реализована; нельзя выдумывать её в следующем atomic capture tranche.

## G. Important invariants

- Password никогда не persistится.
- MasterKey не хранится в открытом виде.
- Все protected DB одного storage используют один MasterKey.
- Любой crypto/provider/identity failure оставляет приложение LOCKED.
- `DatabaseIdentity` обязателен на protected open.
- Current/Catalog initial pair публикуется только после создания и повторной проверки обеих DB.
- Partial pair не auto-repair поверх неизвестного состояния.
- Архивные DB обычным journal path открываются read-only.
- Calendar day нельзя делить между archive owners.
- ARM64 не должен появляться в RuntimeIdentifiers, Platforms, native jobs/scripts или delivery artifacts.
- Нельзя считать managed Build/Test native encryption evidence.
- Нельзя объявлять CI/native/runtime gate PASS без run/step/artifact evidence.
- Нельзя выполнять тяжёлую clipboard работу внутри `WM_CLIPBOARDUPDATE` WndProc.
- Нельзя запускать clipboard capture monitoring до `UNLOCKED`.
- Не выполнять пустой polling Actions; правило полностью зафиксировано в `AGENTS.md`.
- Не force-update `main`; перед write fresh TOCTOU.

## H. Known risks and unresolved questions

ACTIVE / NOT READY:

- `feat/capture-time-context` подготовлена, но **UNVERIFIED by CI**.
- После docs-only handoff commit feature branch будет основана на предыдущем `main`; требуется аккуратный rebase/cherry-pick одного feature commit поверх fresh `main`.
- Capture worker, который dequeues requests и выполняет Source App Resolution → Policy Evaluation → Format Readers → Normalization → Deduplication → Persistence, ещё не реализован.
- SourceApplication / InvocationApplication runtime resolution ещё не реализованы.
- Clipboard format defaults/limits остаются open product questions.
- History tables, catalog index schema, archive schema — NOT READY.
- FTS/search — NOT READY.
- Current→Archive transfer/split/catalog rebuild — NOT READY.
- Password/MasterKey change, crypto metadata recovery, partial Current/Catalog recovery — NOT READY.
- Auto-lock end-to-end UI/handle teardown не закрыт как отдельный tranche, хотя lifecycle primitives и monitor stop boundary уже существуют.
- Final installed-app/native loading launch evidence — NOT READY.
- MSIX vs portable distribution — NOT READY.
- Byte-for-byte native reproducibility — NOT READY.
- Hot backup/snapshot protocol — intentionally deferred.
- No-op branch `__noop_should_not_create__` — harmless repo hygiene item; delete when a suitable GitHub ref-delete capability is available.

## I. Remaining work

Приоритет:

1. **Fresh state:** получить текущий `main` после этого handoff docs commit, текущий tip `feat/capture-time-context`, open runs и сравнение branch ↔ main.
2. Если handoff docs-only Build run ещё выполняется — соблюдать `AGENTS.md`: не polling; выполнять только независимую полезную работу либо остановиться и ждать команды.
3. Перенести один commit `feat: preserve clipboard event time context` на свежий `main`, сохранив только три ожидаемых файла; не merge старую базу поверх docs checkpoint.
4. Получить Build PASS на результате; при failure — exact first failed step/log и один минимальный fix.
5. После time-context PASS перейти к следующему capture pipeline boundary. Предпочтительный следующий предмет исследования/implementation: **Source Application Resolution** вне WndProc и перед policy evaluation, без persistence/schema guessing.
6. Затем реализовать format readers/normalization по уже утверждённым format principles и отдельно согласовать concrete defaults/limits, где они остаются open questions.
7. До persistence согласовать минимальную history schema boundary; не изобретать full archive/catalog schema попутно.
8. Позже отдельно закрыть installed-app native-load/launch evidence и distribution decision.
9. Опциональная hygiene: удалить merged/redundant branches и `__noop_should_not_create__`, не переписывая Git history.

## J. Exact resume point

**Следующий чат должен начать с fresh проверки `main`, `feat/capture-time-context` и GitHub Actions после handoff commit.**

Далее:

- подтвердить, что Build #45 (`33938504886`) на `d76e7ec...` остаётся `SUCCESS`;
- проверить новый docs-only handoff commit/run, если он появился;
- fresh сравнить `feat/capture-time-context` с новым `main`;
- перенести ровно feature commit `a93fe4d7ea93852d0064ee07075cc4c64a52338a` (`feat: preserve clipboard event time context`) поверх свежего `main` без изменения его логики;
- затем получить managed Build evidence;
- **не** возвращаться к Native SQLCipher/runtime staging без нового фактического failure: Native #10 уже PASS;
- **не** проектировать history DB schema в этом же tranche.

## K. First-turn bootstrap instructions

1. Прочитать полностью `AGENTS.md`, этот handoff и `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`; для crypto/native вопросов также `docs/CRYPTOGRAPHY.md` и `docs/NATIVE_SQLCIPHER_BUILD.md`; для capture — `docs/ARCHITECTURE.md` и `docs/REQUIREMENTS.md`.
2. Не доверять mutable SHA/CI status из handoff вместо GitHub — сначала fresh fetch.
3. Проверить текущий `main`, `feat/capture-time-context`, open Actions runs, open PR/issues и branch compare.
4. При расхождении source-of-truth имеет приоритет; исправить модель из handoff, а не подгонять repo под старый текст.
5. Не повторять ARM64/x64 research, SQLCipher source-selection research и runtime-delivery work без нового failure evidence.
6. Соблюдать запрет пустого ожидания GitHub Actions из `AGENTS.md`.
7. Продолжить строго с раздела J.
