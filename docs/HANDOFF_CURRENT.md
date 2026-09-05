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
- Не вводить конкретные clipboard format defaults/size limits без отдельного решения.
- Полную history/catalog/archive schema не придумывать внутри capture tranches.
- Если GitHub Actions выполняется, сначала делать независимую полезную работу. Если её нет — можно ожидать завершения run не более 2 минут. Если run не завершился за это окно — остановиться, дать сводку, точный run ID/SHA/gate и resume point.

## C. Current authoritative source state

Текущий функциональный `main` на момент этого docs checkpoint:

- `b25b4dac9a79f0530a299ba37e06554478f608e2`
- `feat: resolve journal invocation application`

Последний подтверждённый managed CI:

- **Build #65**
- run `33943886744`
- SHA `b25b4dac9a79f0530a299ba37e06554478f608e2`
- conclusion: `SUCCESS`
- обязательные шаги `Verify x64-only implementation scope`, `Restore`, `Build`, `Test` — `SUCCESS`.

При публикации этого handoff docs-only commit `main` изменится; следующий чат должен fresh проверить фактический SHA и соответствующий Build run.

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
- gates `cipher_version >= 4.12.0`, `cipher_status=1`, `sqlite_master`, `quick_check`, `DatabaseIdentity`;
- SQLCipher Community 4.17.0 commit `810db22f575ee7cf94ea96a3e91622b5fcece3dc`;
- OpenSSL 3.5.8 SHA-256 `a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2`.

Native evidence:

- Native SQLCipher #9 run `33906431371` — SUCCESS.
- Native SQLCipher #10 run `33935810021` — SUCCESS.
- artifact `clipensk-sqlcipher-win-x64` id `9960499242`.
- artifact `clipensk-app-win-x64-unpackaged` id `9960500027`.
- unpackaged runtime staging verified x64, exact `sqlcipher.dll`, no `e_sqlcipher.dll`, manifests/hashes/licenses present.

Не считать это финальным installer/installed-launch evidence.

## E. Clipboard capture pipeline — completed tranches

### Listener + protected access

- `631e2dc56f0f1bdc03bfe1be56883b1dad9cd9ce` — resident message window, WM_HOTKEY, WM_CLIPBOARDUPDATE, bounded capture queue capacity 1 / DropOldest / single reader, Add/RemoveClipboardFormatListener.
- Build #44 run `33937739276` — SUCCESS.
- `d76e7ec699980cea006ebeb33d197ba877c6ab4b` — monitoring starts only UNLOCKED and stops on protected-access exit.
- Build #45 run `33938504886` — SUCCESS.

### Event/source/policy/discovery

- `bc02cb58a5a9c7357358c549841cb772746128ec` — preserve `EventTimeContext`; Build #47 run `33939070986` — SUCCESS.
- `d60d461c35b6b4af329b800e6d55d837a42c57c7` — resolve clipboard SourceApplication; Build #48 run `33939545079` — SUCCESS.
- `1d4f9d07af177a2bdacab16c1c7c8dd99dcd707e` — capture policy boundary; Build #49 run `33939777749` — SUCCESS.
- `9c246d46d26ac2554777505a8b10a97ddf577488` — clipboard format discovery; Build #50 — SUCCESS.
- `6e85835460536440cbb1597d818d6a93b8697aef` — retain one clipboard content snapshot / one `DataPackageView`; Build #51 — SUCCESS.
- `ab04088d...` — format selection: only explicitly allowed available formats proceed; Build #52 — SUCCESS.
- `7817134c...` — policy-provider boundary; Build #53 — SUCCESS.
- `6fee56bb...` — single-shot capture pipeline; Build #54 — SUCCESS.

### Reader capabilities

- `106b8fa3...` — standard Text / HTML / RTF reader capability, using retained snapshot; Build #55 — SUCCESS.
- `134c209c...` — host composition with explicit policy provider; Build #56 — SUCCESS.
- `4096d387...` — policy repository boundary without hardcoded persistent application key/schema; Build #57 — SUCCESS.
- `3048e094...` — Bitmap → normalized PNG capability; Build #58 — SUCCESS.
- `f80fceb8471948a5d82147e1080b6e6d02e99325` — WebLink/ApplicationLink reader; Build #59 run `33941383433` — SUCCESS.
- `138d7a45a2f9777bb4440cb16b751bcb6d15c8c7` — StorageItems/CF_HDROP metadata reader; Build #60 run `33941909260` — SUCCESS.

### Planning, routing and invocation context

- `4d481223b8ed93acbeb7eb55e9c2cfc5f243024f` — route selected formats to reader capabilities; Build #61 run `33942004626` — SUCCESS.
- `362ff6ef3993f9392d437ce8339ce4effda91cfb` — create read plan without payload access; Build #62 run `33942111178` — SUCCESS.
- `beb089bcbd24fc04a2f6f4fe79d1de1b034e1fe7` — compose read planning in Windows host; Build #63 run `33942221332` — SUCCESS.
- `e20fc6006fa5e29deb5f4b1bc5c5a29e75f255cf` — single-shot capture → read-plan pipeline, still no payload execution; Build #64 run `33943770538` — SUCCESS.
- `b25b4dac9a79f0530a299ba37e06554478f608e2` — resolve InvocationApplication from foreground window on global-hotkey boundary; Build #65 run `33943886744` — SUCCESS.

InvocationApplication implementation details:

- отдельный `InvocationApplication`, не `ClipboardSourceApplication`;
- foreground HWND определяется через `GetForegroundWindow` при WM_HOTKEY handling, до активации журнала;
- PID определяется через `GetWindowThreadProcessId`;
- executable path — через `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName`;
- если path недоступен, PID сохраняется с `ExecutablePath = null`;
- hotkey event теперь несёт `JournalHotKeyPressedEventArgs` с InvocationApplication;
- App удерживает текущий journal invocation context.

## F. Current architecture facts

- Capture queue хранит signal requests, а не историю.
- `ClipboardCapturePipeline` выполняет queue → source resolution → policy resolution → format discovery → format selection.
- `ClipboardCaptureReadPlanningPipeline` добавляет routing/read-plan, но **не читает payload**.
- Один `DataPackageView` удерживается от format discovery до будущих reader calls; повторный `Clipboard.GetContent()` для payload не нужен.
- Format selection допускает только effective per-format `Allow`.
- Unknown/unroutable selected format явно остаётся unsupported в read-plan.
- Text/HTML/RTF, Bitmap→PNG, WebLink/ApplicationLink и StorageItems имеют capability readers.
- Reader routing fail-closed при неоднозначной поддержке одного format несколькими reader'ами.
- Actual background capture worker ещё не запущен автоматически.
- Policy persistence implementation ещё не выбрана: repository/provider contracts есть, persistent Application identity/schema ещё нет.

## G. Current blocker before actual payload execution

`ClipboardSelectedFormat` уже несёт nullable `MaxBytes`, но архитектура пока не определяет, **какие байты измеряются этим лимитом**.

Нужно отдельно решить семантику для каждого класса payload, например:

- raw clipboard representation bytes;
- decoded text bytes;
- UTF-8 bytes исходного text/HTML/RTF representation;
- normalized PNG bytes для изображений;
- сериализованная textual metadata для StorageItems;
- exact stored bytes для explicitly enabled custom binary.

Пока это не определено:

- не добавлять production read/enforcement stage, который молча игнорирует `MaxBytes`;
- не считать лимит по произвольно выбранному representation;
- не подключать automatic capture worker, который будет реально читать payload без корректного enforcement contract.

Отдельно: `docs/OPEN_QUESTIONS.md` уже оставляет конкретные default limits нерешёнными. Здесь дополнительно зафиксирован вопрос **семантики измерения**, а не только числовых default values.

## H. HTML / RTF search normalization

Архитектура требует:

- original HTML/RTF representation для повторного использования;
- normalized plain-text `SearchText` для поиска.

Точный extractor/parser algorithm пока не выбран. Не вводить случайный HTML/RTF parser только ради продвижения pipeline. После фиксации контракта payload/limits можно добавить normalization boundary и затем конкретную реализацию.

## I. Remaining major work

- определить `MaxBytes` measurement semantics;
- actual reader execution + enforcement;
- HTML/RTF SearchText extraction/normalization;
- policy persistence + persistent Application identity;
- real lock-aware capture worker / consumer loop;
- cancellation/teardown worker при lock;
- minimal history persistence boundary, затем history schema;
- FTS/search/query planning;
- Current→Archive transfer/split/catalog rebuild;
- password/MasterKey change;
- crypto metadata recovery;
- partial Current/Catalog recovery;
- auto-lock end-to-end teardown;
- installed-app native-load/launch evidence;
- MSIX vs portable distribution decision;
- byte-for-byte reproducibility/provenance hardening;
- hot backup/snapshot;
- branch hygiene.

## J. Exact resume instructions

На следующем `продолжай`:

1. Fresh fetch `main` и последний Build run, потому что этот handoff публикуется отдельным docs commit.
2. Если docs Build завершён — проверить x64 guard / Restore / Build / Test до объявления PASS.
3. Не возвращаться к native SQLCipher/runtime staging без фактического failure.
4. Не запускать actual payload execution до решения семантики `MaxBytes`.
5. Безопасные независимые направления: формализовать `MaxBytes` contract в docs/architecture, policy persistence identity design, HTML/RTF normalization contract — но только после проверки, что решение не придумывает незафиксированные product defaults.
6. Любой durable write в `main`: fresh TOCTOU → fast-forward only, `force:false`.
