# NEW CHAT HANDOFF — Clipensk

Дата checkpoint: 2026-09-04.

Этот файл является durable operational checkpoint по правилу `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`. Изменяемое состояние GitHub всегда имеет приоритет; перед следующей durable записью обязательна fresh TOCTOU-проверка.

## A. Project identity

- Проект: **Clipensk** — Open Source Windows clipboard-history manager.
- Репозиторий: `lukindv77/Clipensk`.
- Основная ветка: `main`.
- Source state перед этим docs-only checkpoint: `9b8f8aef6080d858d2433be022127b157758ec9a` (`ci: record x64 native SQLCipher provenance`).
- Поддерживаемая архитектура продукта: **только Windows x64 (AMD64)**.
- `src/Clipensk.App/Clipensk.App.csproj`: `Platforms=x64`, `RuntimeIdentifiers=win-x64`.
- Development host сейчас unpackaged (`WindowsPackageType=None`); финальная схема MSIX/portable ещё не выбрана.
- Open PR на момент fresh проверки: 0.
- Open Issues на момент fresh проверки: 0.

Authoritative документы:

- `AGENTS.md`;
- `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`;
- `docs/REQUIREMENTS.md`;
- `docs/ARCHITECTURE.md`;
- `docs/CRYPTOGRAPHY.md`;
- `docs/NATIVE_SQLCIPHER_BUILD.md`;
- `docs/OPEN_QUESTIONS.md`.

## B. User intent

Цель: довести Clipensk до рабочего защищённого Windows x64 приложения с журналированием clipboard, поиском, Current/Archive storage, обслуживанием и безопасным password → MasterKey lifecycle.

Постоянные правила работы:

- GitHub repository/Actions — primary source of truth для изменяемого состояния;
- перед durable write делать fresh TOCTOU-проверку `main`;
- работать failure-driven, не вносить speculative fixes;
- не объявлять PASS/build/release/encryption gate без фактического evidence;
- не возвращать ARM64: пользователь явно зафиксировал поддержку только обычных Windows x64 ПК;
- не выбирать заново уже утверждённую crypto/storage архитектуру без подтверждённой причины;
- не считать managed unit tests доказательством SQLCipher encryption.

## C. Current authoritative state

### Source

- `main` перед этим handoff update: `9b8f8aef6080d858d2433be022127b157758ec9a`.
- Parent `a167180a43ecb54d9a9988731b396d3ed770cf90` фиксирует x64-only product scope.
- Parent `174b28ba9a472845f889d3ad2a69027d7f9d7ebe` удалил ARM64 target/job/build script.
- Recursive Git tree и platform-defining project/workflow audit не обнаружили ARM64 implementation code. Намеренные упоминания ARM64 остаются только в документации как явное `out of product scope`.

### Managed CI

- Build #37, run `33905627716`, SHA `a167180a43ecb54d9a9988731b396d3ed770cf90`: `SUCCESS` (Restore/Build/Test).
- Build #38, run `33906431125`, SHA `9b8f8aef6080d858d2433be022127b157758ec9a`: на момент checkpoint выполнялся; его итог нужно получить fresh через GitHub Actions API.

### Native SQLCipher CI

- Подтверждённый x64 native PASS: Native SQLCipher #6, run `33900341964`, SHA `38bb51b135506a7bb6551e48652ab87bcde10672`.
- Evidence #6 включало pinned SQLCipher/OpenSSL build, x64 PE/export verification, production storage smoke, encrypted headers, correct-key reopen, wrong-key rejection, `cipher_version` и `cipher_status=1`.
- Native #7 (ARM64 experiment) был отменён после решения x64-only и не является product evidence.
- Native #8, run `33905283832`, был отменён новым provenance push до smoke и не является PASS evidence.
- Native #9, run `33906431371`, SHA `9b8f8aef6080d858d2433be022127b157758ec9a`: создан для fresh x64 build + provenance + smoke; на момент checkpoint ещё не завершён.

## D. Current owner / active task

Текущий owner: **закрыть x64 native provenance tranche и затем перейти к x64 runtime delivery**.

Root gap:

- production x64 SQLCipher integration уже фактически доказана run #6;
- build manifest фиксировал source pins и DLL SHA-256, но не полный repository/toolchain/runner provenance;
- verified `sqlcipher.dll` ещё не доставляется конечным publish/package layout приложения.

Acceptance criteria текущего tranche:

1. Native #9 успешно собирает pinned x64 SQLCipher/OpenSSL.
2. Provenance collector успешно дополняет manifest и проверяет DLL SHA-256.
3. Smoke на том же run проходит production storage path.
4. Artifact содержит ожидаемый x64 DLL, manifest и provenance fields.
5. После этого provenance считается implemented/verified, но **не** byte-for-byte reproducibility proof.
6. Следующий delivery tranche остаётся x64-only и не фиксирует преждевременно MSIX vs portable.

## E. What has been completed

### First-run / protected lifecycle

Реализованы:

- выбор DataRoot на первом запуске;
- storage-wide `storage-crypto.json`;
- Argon2id v1.3 profile для новых storage: 64 MiB, 3 iterations, 4 lanes, 16-byte salt, 32-byte MasterKey;
- MasterKey verifier;
- LOCKED/UNLOCKING/UNLOCKED lifecycle integration;
- password hint и отсутствие persistence пароля;
- protected initial `Current/current.db` + `Current/storage-catalog.db`;
- atomic staging/publish initial pair;
- `DatabaseIdentity` validation;
- partial Current/Catalog fail-closed behavior;
- archive-presence gate, запрещающий ошибочную auto-initialization поверх recovery case.

### SQLCipher production boundary

Зафиксировано и реализовано:

- `Microsoft.Data.Sqlite.Core`;
- `SQLitePCLRaw.provider.sqlcipher`;
- самостоятельно собираемый `sqlcipher.dll`;
- raw 32-byte MasterKey через `sqlite3_key`;
- `cipher_compatibility=4`;
- `cipher_memory_security=ON`;
- runtime gates: `cipher_version >= 4.12.0`, `cipher_status=1`, `sqlite_master`, `quick_check`, `DatabaseIdentity`.

Pinned native inputs:

- SQLCipher Community `4.17.0`;
- commit `810db22f575ee7cf94ea96a3e91622b5fcece3dc`;
- OpenSSL `3.5.8`;
- OpenSSL tarball SHA-256 `a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2`.

### x64-only cleanup

- App project: только `x64` / `win-x64`.
- Native workflow: только job `win-x64` на `windows-2022`.
- `eng/build-sqlcipher-windows-arm64.ps1` удалён.
- `eng/` до provenance содержит только x64 build script.
- ARM64 удалён из backlog как requirement; документация явно помечает его вне product scope.
- Managed Build #37 после x64-only cleanup: `SUCCESS`.

### Provenance implementation

Commit `9b8f8aef6080d858d2433be022127b157758ec9a` добавляет:

- `eng/add-native-provenance.ps1` (только `win-x64`);
- post-build provenance step в `.github/workflows/sqlcipher-native.yml`.

Collector проверяет manifest architecture и DLL SHA-256, затем записывает:

- repository commit/tree;
- build script path/SHA-256;
- workflow path/SHA-256;
- Visual Studio installation version;
- SQLCipher/OpenSSL source locations;
- GitHub Actions repository/SHA/run/attempt/workflow/runner/image metadata.

## F. Current conclusions

1. **Architecture scope**: только Windows x64 (AMD64). ARM64 implementation не нужен и не должен возвращаться.
2. **App platform**: .NET 10 + WinUI 3 + Windows App SDK 2.4.0 Stable.
3. **Distribution**: development host unpackaged; MSIX vs portable остаётся отдельным product decision.
4. **Crypto**: один storage-wide MasterKey; пароль не хранится; Argon2id profile v1 зафиксирован.
5. **Database encryption**: production uses self-built SQLCipher Community, не deprecated bundled `e_sqlcipher` binaries.
6. **Unlock gate**: metadata verifier недостаточен; нужен успешный protected DB open/identity validation.
7. **Native evidence**: managed Build/Test не заменяет native SQLCipher smoke.
8. **Provenance**: source pinning + artifact hash + toolchain/run metadata — groundwork; byte-for-byte reproducibility остаётся отдельной задачей.
9. **Packaging**: native build PASS сам по себе не означает production delivery DLL.
10. **History schema**: фактические clipboard history tables, catalog index schema и archive schema ещё не реализованы; их нельзя выдумывать без согласованной schema boundary.

## G. Important invariants

- Password никогда не persistится.
- MasterKey не хранится в открытом виде.
- Все protected DB одного storage используют один MasterKey.
- Любой crypto/provider/identity failure оставляет приложение LOCKED.
- `DatabaseIdentity` обязателен на protected open.
- Current/Catalog initial pair публикуется только после создания и повторной проверки обеих БД.
- Partial pair не auto-repair поверх неизвестного состояния.
- Архивные БД обычным journal path открываются read-only.
- Calendar day нельзя делить между archive owners.
- ARM64 не поддерживается и не должен появляться в RuntimeIdentifiers, Platforms, native workflow/jobs/scripts или delivery artifacts.
- Не объявлять CI/native gate PASS без конкретного run/step/artifact evidence.

## H. Known risks and unresolved questions

- Native #9 ещё должен подтвердить новый provenance step.
- Production package/runtime delivery verified `sqlcipher.dll` — NOT READY.
- Byte-for-byte reproducibility — NOT READY.
- Финальная схема MSIX/portable — не выбрана.
- Clipboard history schema/catalog index/archive schema — NOT READY.
- Clipboard capture pipeline, FTS/search, archive transfer/split, catalog rebuild — NOT READY.
- Password/MasterKey change и crypto metadata recovery — NOT READY.
- Partial Current/Catalog recovery/catalog rebuild — NOT READY.
- Auto-lock handle teardown — NOT READY.
- Hot backup/snapshot protocol — отложен.

## I. Remaining work

Приоритет:

1. Fresh проверить Build #38 (`33906431125`) и Native #9 (`33906431371`).
2. Если Native #9 failed — получить exact job log, исправить только первый подтверждённый failure и перезапустить evidence cycle.
3. Если Native #9 success — скачать x64 artifact, проверить manifest/provenance, PE x64, required exports и зафиксировать provenance PASS.
4. Реализовать scheme-neutral x64 runtime delivery/staging verified `sqlcipher.dll` в app publish output; не объявлять installer готовым без runtime loading evidence.
5. После native delivery вернуться к согласованию/реализации history/catalog/archive schema и capture pipeline.
6. Отдельно решить MSIX vs portable и release packaging.

## J. Exact resume point

**Следующий чат должен начать с fresh проверки `main`, Build #38 (`33906431125`) и Native SQLCipher #9 (`33906431371`).**

Если #9 завершён успешно — сразу получить artifact/evidence и проверить provenance manifest перед любым новым native write. Если #9 failed — начать с exact failed step/log и сделать один минимальный failure-driven fix. ARM64 не исследовать и не реализовывать.

## K. First-turn bootstrap instructions

1. Прочитать `AGENTS.md`, этот handoff, `CRYPTOGRAPHY.md` и `NATIVE_SQLCIPHER_BUILD.md`.
2. Не доверять SHA/CI status из handoff без fresh GitHub check.
3. Сначала получить текущий `main` head и statuses run #38/#9.
4. Source-of-truth имеет приоритет над handoff при любом расхождении.
5. Не повторять завершённый ARM64/x64 architecture research.
6. Не менять production native recipe без подтверждённого CI failure.
7. Продолжить строго с раздела J.
