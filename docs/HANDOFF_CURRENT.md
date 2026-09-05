# NEW CHAT HANDOFF — Clipensk

Дата checkpoint: 2026-09-05.

Этот файл — durable operational checkpoint. Изменяемое состояние GitHub и GitHub Actions всегда является source of truth; перед любой durable записью в `main` нужна fresh TOCTOU-проверка.

## A. Project identity

- Проект: **Clipensk** — Open Source Windows clipboard-history manager.
- Репозиторий: `lukindv77/Clipensk`.
- Product architecture: **только Windows x64 (AMD64)**. ARM64 полностью вне scope.
- Runtime contract: `Platforms=x64`, `RuntimeIdentifiers=win-x64`.
- Текущий development host: unpackaged (`WindowsPackageType=None`).
- Финальная схема распространения MSIX / portable / оба — не выбрана.

Authoritative документы:

- `AGENTS.md`;
- `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`;
- `docs/REQUIREMENTS.md`;
- `docs/ARCHITECTURE.md`;
- `docs/CLIPBOARD_CAPTURE_SIZE_LIMITS.md`;
- `docs/CRYPTOGRAPHY.md`;
- `docs/NATIVE_SQLCIPHER_BUILD.md`;
- `docs/OPEN_QUESTIONS.md`;
- этот `docs/HANDOFF_CURRENT.md`.

## B. Mandatory workflow rules

- GitHub repository и GitHub Actions — source of truth для mutable state.
- Перед durable write в `main` — fresh TOCTOU.
- `main` никогда не force-update.
- Работа failure-driven; не делать speculative fixes.
- Не объявлять PASS/build/release/encryption gate без точного evidence.
- `WM_CLIPBOARDUPDATE` WndProc остаётся signal/enqueue only: никаких clipboard reads, process resolution, DB/persistence или другой тяжёлой работы.
- Clipboard monitoring разрешён только при protected data access = `UNLOCKED`.
- SourceApplication и InvocationApplication — разные понятия и разные runtime boundaries.
- Event time сохраняется как `EventTimeContext`: UTC timestamp + local offset + Windows time-zone ID + calendar date.
- Не вводить конкретные clipboard format defaults/size-limit values без отдельного решения.
- Полную history/catalog/archive schema не придумывать внутри capture tranches.
- Если GitHub Actions выполняется, сначала делать независимую полезную работу. Если её нет — можно ожидать завершения run не более 2 минут. Если run не завершился за это окно — остановиться, дать сводку, точный run ID/SHA/gate и resume point.

## C. Current authoritative source state

Текущий `main` непосредственно перед этим docs-only checkpoint:

- `02d9a63cf58311a20a93ef47db182f6729fbd726`
- `feat: stop clipboard read execution after cancellation`

Текущий CI gate:

- **Build #73**
- run `33949362477`
- SHA `02d9a63cf58311a20a93ef47db182f6729fbd726`
- на момент подготовки handoff: `queued` / **UNVERIFIED**.

Последний подтверждённый PASS:

- **Build #72**
- run `33949273923`
- SHA `6bca08f3e9721fa5cf6d35c56645d0a0b0f528b8`
- `Verify x64-only implementation scope`, `Restore`, `Build`, `Test` — SUCCESS.

После публикации этого handoff docs commit `main` изменится; следующий чат обязан fresh проверить фактический SHA и соответствующий Build run.

## D. Protected storage / SQLCipher status

DONE / VERIFIED:

- first-run DataRoot;
- `storage-crypto.json`;
- Argon2id v1.3 production profile: 64 MiB / 3 iterations / 4 lanes / 16-byte salt / 32-byte MasterKey;
- MasterKey verifier;
- LOCKED / UNLOCKING / UNLOCKED;
- password не persistится;
- protected `Current/current.db` и `Current/storage-catalog.db`;
- atomic initial Current/Catalog pair;
- `DatabaseIdentity` validation;
- partial Current/Catalog fail-closed;
- archive-presence recovery gate;
- production SQLCipher boundary: `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.sqlcipher` + self-built `sqlcipher.dll`;
- raw 32-byte key через `sqlite3_key`;
- `cipher_compatibility=4`, `cipher_memory_security=ON`;
- gates `cipher_version >= 4.12.0`, `cipher_status=1`, `sqlite_master`, `quick_check`, `DatabaseIdentity`.

Native evidence:

- Native SQLCipher #9 run `33906431371` — SUCCESS.
- Native SQLCipher #10 run `33935810021` — SUCCESS.
- artifact `clipensk-sqlcipher-win-x64` id `9960499242`.
- artifact `clipensk-app-win-x64-unpackaged` id `9960500027`.
- unpackaged runtime staging verified x64, exact `sqlcipher.dll`, no `e_sqlcipher.dll`, manifests/hashes/licenses present.

Не считать это финальным installer/installed-launch evidence. Native/runtime path не трогать без фактического failure.

## E. Clipboard capture pipeline — verified tranches

### Listener / context / policy / discovery

- `631e2dc56f0f1bdc03bfe1be56883b1dad9cd9ce` — resident message window + clipboard listener; Build #44 PASS.
- `d76e7ec699980cea006ebeb33d197ba877c6ab4b` — monitoring только при protected access; Build #45 PASS.
- `bc02cb58a5a9c7357358c549841cb772746128ec` — preserve `EventTimeContext`; Build #47 PASS.
- `d60d461c35b6b4af329b800e6d55d837a42c57c7` — SourceApplication resolution; Build #48 PASS.
- `1d4f9d07af177a2bdacab16c1c7c8dd99dcd707e` — capture policy model/evaluator; Build #49 PASS.
- `9c246d46d26ac2554777505a8b10a97ddf577488` — format discovery; Build #50 PASS.
- `6e85835460536440cbb1597d818d6a93b8697aef` — retain one clipboard `DataPackageView` snapshot; Build #51 PASS.
- `ab04088d...` — format selection: только available + effective per-format Allow; Build #52 PASS.
- `7817134c...` — policy-provider boundary; Build #53 PASS.
- `6fee56bb...` — single-shot capture pipeline; Build #54 PASS.

### Reader capabilities / routing / planning

- `106b8fa3...` — Text / HTML / RTF reader; Build #55 PASS.
- `134c209c...` — host composition with explicit policy provider; Build #56 PASS.
- `4096d387...` — policy repository boundary без hardcoded persistent application key; Build #57 PASS.
- `3048e094...` — Bitmap → normalized PNG reader; Build #58 PASS.
- `f80fceb8471948a5d82147e1080b6e6d02e99325` — WebLink/ApplicationLink reader; Build #59 PASS.
- `138d7a45a2f9777bb4440cb16b751bcb6d15c8c7` — StorageItems/CF_HDROP metadata reader; Build #60 PASS.
- `4d481223b8ed93acbeb7eb55e9c2cfc5f243024f` — reader routing; Build #61 PASS.
- `362ff6ef3993f9392d437ce8339ce4effda91cfb` — read-plan; Build #62 PASS.
- `beb089bcbd24fc04a2f6f4fe79d1de1b034e1fe7` — host read-plan composition; Build #63 PASS.
- `e20fc6006fa5e29deb5f4b1bc5c5a29e75f255cf` — capture → read-plan wrapper; Build #64 PASS.
- `b25b4dac9a79f0530a299ba37e06554478f608e2` — InvocationApplication resolution on hotkey boundary; Build #65 PASS.

### MaxBytes semantics / actual payload execution

`MaxBytes` measurement semantics зафиксирована в `docs/CLIPBOARD_CAPTURE_SIZE_LIMITS.md`:

- лимит относится к canonical capture representation, а не к DB/FTS/filesystem overhead;
- Text/HTML/RTF: UTF-8 byte count точной строки representation от reader;
- WebLink/ApplicationLink: UTF-8 byte count `Uri.OriginalString`;
- image: exact normalized PNG bytes;
- future explicitly enabled custom binary: exact bytes, предназначенные для external persistence;
- `CF_HDROP` с configured `MaxBytes` остаётся fail-closed/deferred, пока не утверждено canonical persisted metadata representation;
- check inclusive: `CanonicalByteCount <= MaxBytes`;
- конкретные числовые default limits по-прежнему не выбраны.

Commits / CI:

- `1a59b22c758558ffeac0c73c0f6883580a49040f` — canonical size semantics/helper/tests. Build #68 run `33948811582` FAILED только на xUnit `InlineData` Int32→Nullable<Int64> binding; Restore/Build были SUCCESS.
- `eec705056a68e5756249c8cbb0b86ff446767ec2` — минимальный test-data fix, production API не менялся. Build #69 run `33948950769` — PASS по x64 guard / Restore / Build / Test.
- `0e3f149b2cf903afbd2f88e5e9a8ea5f76ed22b9` — actual read execution + canonical size enforcement. Text/PNG/Link oversize payload не попадает в accepted content; StorageItems + configured MaxBytes deferred без чтения. Build #70 run `33949077346` — PASS.
- `6cd0a6a2841bd3fb94d8d6be2dff7b12581858de` — compose execution stage in `ResidentWindowsHost`. Build #71 run `33949171614` — PASS.
- `6bca08f3e9721fa5cf6d35c56645d0a0b0f528b8` — single-shot `capture → read-plan → execute` pipeline. Build #72 run `33949273923` — PASS.
- `02d9a63cf58311a20a93ef47db182f6729fbd726` — post-await cancellation checks in execution stage + regression test. Build #73 run `33949362477` — UNVERIFIED at this checkpoint.

## F. Current runtime architecture

- `ClipboardCaptureQueue` — bounded signal queue, не history queue.
- `ClipboardCapturePipeline`: queue → SourceApplication → policy → format discovery → format selection.
- `ClipboardCaptureReadPlanningPipeline`: capture → reader routing/read-plan.
- `ClipboardCaptureReadExecutionPipeline`: planning → actual reader execution + canonical size enforcement.
- Один `DataPackageView` удерживается от format discovery до payload reader; повторный `Clipboard.GetContent()` для payload не используется.
- Reader capabilities: Text/HTML/RTF, normalized PNG, WebLink/ApplicationLink, StorageItems metadata.
- Unsupported selected formats остаются explicit unsupported в execution plan/result.
- Oversize payload возвращается как size-rejected внутри transient execution result и не должен передаваться persistence.
- StorageItems с configured `MaxBytes` возвращается deferred и reader не вызывается.
- Execution stage проверяет cancellation перед route и повторно после каждого async reader await; отменённый read не публикуется как accepted content.
- Важно: reader interfaces пока не принимают `CancellationToken`, поэтому это **не** доказательство отмены самого in-flight WinRT operation; гарантируется остановка дальнейшей обработки после await.
- Automatic background consumer/worker в App ещё не запущен.
- `App` по-прежнему только включает/выключает clipboard listener вместе с protected access.

## G. Remaining blockers before automatic capture persistence

### 1. Policy persistence / stable Application identity

Provider/repository contracts есть, но persistent implementation отсутствует. Требования не фиксируют stable Application key. Нельзя молча использовать PID как persistent identity; executable path также не зафиксирован как окончательный key.

До решения identity/storage нельзя запускать worker с placeholder policy, иначе queue будет потребляться без корректной policy source.

### 2. Capture persistence / minimal history schema

Actual payload execution теперь есть, но sink/history persistence ещё нет. Следующий persistence tranche должен отдельно определить минимальную journal record boundary и не разворачивать сразу полную Current/Archive schema.

### 3. HTML / RTF SearchText

Архитектура требует original HTML/RTF + normalized plain-text `SearchText`. Точный extractor/parser algorithm пока не выбран; не вводить случайный parser.

### 4. External payload dedup/location

`ExternalPayloadAddressFactory` уже строит SHA-256 address. Физический writer нельзя считать complete dedup store без persistent mapping SHA→первоначальное размещение: одинаковый PNG в разные дни должен физически храниться один раз в дате первого сохранения.

### 5. CF_HDROP canonical size representation

Metadata fields зафиксированы, но canonical persisted textual representation ещё нет. `StorageItems + MaxBytes` поэтому намеренно deferred.

## H. Next recommended sequence

1. Fresh проверить Build #73 / run `33949362477` и обязательные steps.
2. Если #73 PASS — cancellation tranche закрыт.
3. После этого выбрать и зафиксировать stable Application identity + policy persistence boundary **или** минимальный accepted-capture persistence contract; не запускать background worker раньше policy source + sink.
4. Затем реализовать lock-aware worker lifecycle: запуск только UNLOCKED, cancellation/stop на protected-access exit, не persistить результаты после cancellation.
5. Отдельными tranches: HTML/RTF SearchText, external file dedup store, CF_HDROP canonical representation/limits, history schema/FTS, Current→Archive.
6. Любой durable write в `main`: fresh TOCTOU → fast-forward only, `force:false`.

## I. Still remaining major work

- stable Application identity + policy persistence;
- automatic lock-aware capture worker;
- capture sink / minimal history persistence;
- HTML/RTF SearchText normalization;
- external image/custom-binary persistence + global dedup + Trash integration;
- CF_HDROP canonical persisted representation/limits;
- full history/catalog/archive schema;
- FTS/search/query planning;
- Current→Archive transfer/split/catalog rebuild;
- password/MasterKey change;
- crypto metadata recovery;
- partial Current/Catalog recovery;
- auto-lock end-to-end teardown;
- installed-app native-load/launch evidence;
- MSIX vs portable decision;
- byte-for-byte reproducibility/provenance hardening;
- hot backup/snapshot;
- branch hygiene.
