# NEW CHAT HANDOFF — Clipensk

Дата checkpoint: 2026-09-04.

Этот файл создан по обязательному правилу `docs/WORKFLOW_NEW_CHAT_HANDOFF.md` и является durable checkpoint для перехода в новый чат. Изменяемое состояние GitHub всегда имеет приоритет; перед следующей записью обязательна fresh TOCTOU-проверка.

## A. Project identity

- Проект: **Clipensk**.
- Назначение: резидентное Windows-приложение для долговременного журналирования буфера обмена, быстрого поиска и повторного использования истории с контекстом приложений.
- Репозиторий: `lukindv77/Clipensk` (public GitHub repository).
- Основная ветка: `main`.
- Source state, на котором подготовлен этот checkpoint: `933bfaf598eef991364ea1a378d0d519306b45d8`.
- CI для этого source state: GitHub Actions run `33868026948` (#23) — `SUCCESS`; Restore, Build и Test завершены успешно.
- Предыдущий подтверждённый green baseline: commit `82797e4b913c05f48cc949a9b794f2acc292968a`, run `33867302078` (#20) — `SUCCESS`.
- Открытые PR на момент проверки: 0.
- Открытые Issues на момент проверки: 0.
- Текущей версии продукта/release пока нет.

Authoritative документы:

- `AGENTS.md` — обязательные правила работы;
- `docs/WORKFLOW_NEW_CHAT_HANDOFF.md` — полное правило handoff;
- `docs/REQUIREMENTS.md` — утверждённые требования;
- `docs/ARCHITECTURE.md` — актуальная техническая архитектура;
- `docs/OPEN_QUESTIONS.md` — только нерешённые вопросы;
- `README.md` — обзор проекта.

Основные проекты solution:

- `src/Clipensk.App` — WinUI 3 UI, lifecycle, Journal Shell;
- `src/Clipensk.Core` — domain/application contracts and rules;
- `src/Clipensk.Windows` — Win32/Windows integration;
- `src/Clipensk.Storage` — storage/file layer groundwork;
- `src/Clipensk.Infrastructure` — settings/localization/infrastructure;
- `tests/Clipensk.Core.Tests`;
- `tests/Clipensk.Storage.Tests`.

Технологический baseline:

- C# / .NET 10;
- WinUI 3;
- Windows App SDK `2.4.0` Stable;
- development host сейчас unpackaged;
- точная финальная схема распространения ещё не выбрана.

## B. User intent

Конечная цель пользователя: разработать **Clipensk** как Open Source Windows clipboard-history manager с долговременной историей, быстрым доступом, контекстом приложений, актуальной и сегментированными архивными БД, поиском по периоду и управляемыми политиками сбора.

Постоянные требования к стилю работы:

- authoritative source of truth — прежде всего текущий GitHub repository state;
- не повторять уже завершённое исследование без причины;
- не объявлять build/test/release/merge как PASS без evidence;
- перед записью в изменяемый внешний source of truth делать fresh TOCTOU-проверку;
- при переходе в новый чат строго выполнять `AGENTS.md` и `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`;
- UI Clipensk по умолчанию только русский, но архитектура обязана поддерживать внешние файлы переводов;
- продукт полностью Open Source; конкретная лицензия ещё не выбрана;
- пользователь не хочет внешних изменений/обсуждений в canonical repository. `CONTRIBUTING.md` это декларирует; точные admin moderation settings GitHub через доступный connector не подтверждаются, поэтому при необходимости нужна отдельная fresh проверка в UI/API с admin permission.

## C. Current authoritative state

### DONE / PASS

- `main` source state до handoff checkpoint: `933bfaf598eef991364ea1a378d0d519306b45d8`.
- GitHub Actions run `33868026948` (#23) для этого commit: `SUCCESS`.
- Restore: PASS.
- Build: PASS.
- Test: PASS.
- Open PR: 0.
- Open Issues: 0.

### Реально присутствующий код

- базовый WinUI 3 Journal Shell;
- журнал является главным окном навигации;
- разделы shell: Журнал, Приложения, Обслуживание, Настройки, О программе;
- закрытие Journal Window скрывает его вместо немедленного завершения резидентного процесса;
- `ApplicationLockStateMachine` и состояния блокировки как domain groundwork;
- `StorageQueryPlanner`, выбирающий только Current/Archive, пересекающиеся с периодом, и отвергающий пересекающиеся archive coverage ranges;
- `EventTimeContext`: UTC timestamp + local offset + Windows time-zone ID + CalendarDate;
- `ArchiveFileName`: `archive_000025.db`, `archive_000025_0001.db`, ... без вложенных суффиксов;
- пользовательская глобальная hotkey-модель;
- Win32 `ResidentMessageWindow`/`RegisterHotKey` infrastructure;
- смена hotkey не снимает старую комбинацию до успешной регистрации новой; повторная регистрация той же комбинации — no-op;
- JSON settings store с записью через temp-file + move;
- settings model содержит `DataRootPath`, `JournalHotKey`, `AutoLockEnabled=false`, `TrashRetentionDays=30`, `PasswordHint`;
- встроенный русский localization service groundwork;
- `ExternalPayloadAddressFactory`: SHA-256 addressing, путь `YYYY-MM-DD/<hash>.png` для уже нормализованных PNG bytes; custom binary address support;
- unit tests для Core storage rules и external payload address rules.

### NOT READY / не реализовано

- реальные SQLite БД;
- SQLCipher;
- Argon2id/MasterKey derivation;
- password unlock window;
- first-run data-root selection;
- реальное состояние LOCKED/UNLOCKED в App lifecycle;
- clipboard listener `AddClipboardFormatListener`;
- capture queue/pipeline;
- SourceApplication/InvocationApplication capture;
- реальный `storage-catalog.db`;
- rebuild каталога;
- archive transfer/split/maintenance implementation;
- FTS5/search implementation;
- external image decode + **реальная PNG normalization** (сейчас factory принимает bytes, которые уже должны быть normalized PNG);
- actual external-file store / dedup / Trash processing;
- application capture policies;
- JSON external localization file loader;
- tray implementation;
- single-instance implementation;
- actual DB/file relocation;
- backup/snapshot protocol;
- release/install packaging.

## D. Current owner / active task

Текущий owner: продолжение разработки первого рабочего protected lifecycle tranche.

Непосредственная задача после handoff:

1. Реализовать **первый запуск**: если `DataRootPath` ещё не задан, пользователь обязан выбрать папку хранения данных до создания БД и до clipboard logging. Это особенно обязательно для будущего MSIX.
2. После первоначальной настройки запускать Clipensk в `LOCKED`.
3. Реализовать окно ввода пароля с `PasswordHint`.
4. До успешной разблокировки не запускать clipboard capture и не давать доступ к фактической истории.
5. В `LOCKED` оставить доступными только безопасные настройки/обслуживание, не требующие чтения clipboard data.
6. Интегрировать уже существующий `ApplicationLockStateMachine` в App lifecycle.
7. Не притворяться, что шифрование готово: crypto configuration остаётся открытым вопросом. На этом tranche допустим abstraction boundary для unlock/key provider, но финальные Argon2id/SQLCipher параметры нельзя изобретать без фиксации решения.

Root cause/gap: текущий `App.OnLaunched` сразу загружает settings, создаёт JournalWindow и активирует его. First-run wizard, password gate и protected storage initialization отсутствуют.

Acceptance criteria текущего tranche:

- на чистой конфигурации Journal/history не открывается до выбора DataRoot;
- после выбора DataRoot приложение переходит в LOCKED, а не в рабочий journal state;
- password hint виден до unlock;
- password не сохраняется;
- clipboard capture остаётся выключенным до unlock;
- safe settings доступны в LOCKED;
- existing hotkey/settings behavior не ломается;
- build/tests остаются green;
- документация обновляется только по реально реализованному/утверждённому состоянию.

Соседние задачи, которые НЕ считать закрытыми этим tranche:

- actual cryptography;
- actual SQLite schema;
- clipboard capture;
- FTS/search;
- archive maintenance;
- packaging/release.

## E. What has been completed

### Research / requirements

Зафиксирована продуктовая и storage-архитектура в `docs/REQUIREMENTS.md` и `docs/ARCHITECTURE.md`, включая:

- WinUI 3 + Win32 interop;
- Current + multiple Archive + rebuildable catalog;
- non-overlapping calendar-day archive ownership;
- configurable journal period used before physical DB queries;
- archive read-only by default;
- safe Current→Archive transfer protocol;
- archive split naming;
- one MasterKey for protected DBs;
- password never persisted;
- external PNG/custom-binary storage model;
- text-only clipboard payload inside DB;
- per-application capture policies;
- Russian built-in UI + external localization;
- Open Source product model.

### Durable code checkpoints

Important implementation commits from this tranche include:

- `c12e1f9b96f916d4305aa1a3c4a4b97a3ab4c913` — hotkey edge-case fix;
- `82797e4b913c05f48cc949a9b794f2acc292968a` — CI opt-in to .NET 10 Microsoft Testing Platform; green run #20;
- `933bfaf598eef991364ea1a378d0d519306b45d8` — mandatory handoff-rule links; green run #23.

### Workflow rule

- Full mandatory handoff rule: `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`.
- Root entry point: `AGENTS.md`.
- README links both.

## F. Current conclusions

1. **Platform**: WinUI 3 / Windows App SDK 2.4.0 Stable / .NET 10. WinUI — UI layer; Win32 handles clipboard/hotkey/tray/HWND/system integration.
2. **Main UX**: Journal is the primary shell. All management is reachable from it. One configurable global hotkey opens/activates Journal.
3. **Resident model**: per-user process; closing Journal hides it. Windows Service is not used for core resident behavior.
4. **Locking**: app must require password at startup before history/capture. Password is never saved. Password hint is unprotected setting. Auto-lock is optional and default OFF.
5. **Crypto**: all protected DBs share one MasterKey. Argon2id + SQLCipher are candidates, not final implemented configuration.
6. **Storage**: `Current/current.db` + `Current/storage-catalog.db`; all archives in separate Archive folder. Paths are configurable and moving a path must physically relocate data safely.
7. **Catalog**: derived/rebuildable. No unique critical fact may exist only in `storage-catalog.db`; each DB is self-describing.
8. **Archive ownership**: calendar days are atomic. Two archives may not own the same day. Stored event includes UTC + offset + Windows zone identity + frozen CalendarDate from local Windows zone at event time.
9. **Archive split**: original keeps `archive_XXXXXX.db`; extra pieces use `_0001`, `_0002`, ... in the same base family.
10. **Current→Archive**: copy → verify → purge source. Temporary duplicate allowed; missing from both forbidden.
11. **Journal queries**: default/current period is part of physical query plan; archives outside the period are not opened needlessly. Period change cancels/replans/reloads.
12. **Archive access**: read-only in ordinary history; write only for maintenance/transfer/split/etc.
13. **DB payload principle**: actual clipboard payload stored directly in DB must be textual/searchable. Technical metadata may be numeric/IDs.
14. **HTML/RTF**: only DB, with original textual representation plus searchable plain-text representation; size limits configurable.
15. **Images**: external unencrypted files only; normalize to PNG first, then SHA-256 exact PNG bytes, filename `<SHA256>.png` under first-store date folder.
16. **Custom registered/private binary**: unknown format name may be recorded in app capability registry, but payload default OFF; if user enables, payload is external file.
17. **File selections (`CF_HDROP`)**: do not copy actual user files; store searchable path/filename/operation metadata in DB. Preferred action Copy/Move/Link is not assumed to be actually performed.
18. **Never store**: CF_WAVE, CF_RIFF, virtual file contents.
19. **Trash**: external file moves to Trash only after no valid references remain; default retention 30 days.
20. **Per-app policy**: global defaults inherited/overridden per app. Disabling DB-resident format clears matching Current data but not ordinary archived DB content; disabling external-file format may require deleting references in Current and Archive and moving now-unreferenced file to Trash.
21. **Localization**: Russian embedded fallback is mandatory. External language files may be imported or placed in Languages folder. Internal enum/state identifiers are not localized.
22. **Open Source**: product fully Open Source; exact license is unresolved.
23. **Backup**: snapshot protocol intentionally deferred. Requirement for read/copy access to files while app runs remains, but consistent hot-backup mechanism is NOT designed.

Superseded/clarified conclusions:

- Earlier idea of WPF is superseded by WinUI 3.
- Earlier idea of per-database derived keys is superseded: all protected DBs use one MasterKey.
- Earlier idea to store rejected-format audit markers in journal is rejected: do not store them.
- Earlier idea to store audio/virtual files externally is rejected: do not store them anywhere.
- Earlier idea to copy files selected in Explorer is rejected: store only file-selection metadata.
- Earlier assumption that PNG storage is already fully implemented is false: only addressing/hash helper exists; image normalization pipeline itself is NOT READY.

## G. Important invariants

- `docs/WORKFLOW_NEW_CHAT_HANDOFF.md` is mandatory for future handoffs.
- Fresh external state outranks this handoff.
- No PASS without evidence.
- Password never persists anywhere.
- No clipboard logging/history access before unlock.
- One MasterKey for all protected DBs.
- Archive DBs read-only during ordinary journal use.
- A calendar day cannot be split across archive DBs and cannot be owned by two archives.
- Existing event CalendarDate is never recomputed because Windows time zone later changes.
- Catalog must be rebuildable without the original catalog file.
- Current→Archive cannot delete source before verified archive copy.
- State `Current=no && Archive=no` is forbidden during transfer.
- Temporary Current+Archive duplicate is allowed and must dedupe in UI.
- Journal period constrains physical storage query planning.
- Binary clipboard payload never goes directly into DB.
- Images/custom binary external files are deliberately unencrypted.
- Image SHA-256 is calculated **after** PNG normalization.
- Real files from CF_HDROP are never archived/copied by Clipensk.
- CF_WAVE/CF_RIFF/virtual file contents are ignored completely.
- Unknown private binary payload is disabled by default.
- AutoLock default is OFF.
- External-file Trash default retention is 30 days.
- Russian embedded localization is fallback and must allow application operation with no external language files.

## H. Known risks and unresolved questions

Authoritative open questions are in `docs/OPEN_QUESTIONS.md`:

1. Exact Open Source license.
2. Official minimum/supported Windows versions.
3. Final distribution model: MSIX/unpackaged/both.
4. Exact crypto configuration: Argon2id params, raw MasterKey→SQLCipher, SQLCipher params, memory clearing, password/MasterKey change procedure.
5. Hot backup/snapshot protocol.
6. Default format set and per-format size/count limits.
7. Default journal period.
8. Default archive rotation thresholds and ANY/ALL semantics.
9. Localization file schema/versioning/signature/template export.
10. Recall/paste behavior: copy-back, auto-paste, focus restoration, plain-text paste, foreground restrictions.

Additional implementation risks/gaps:

- no single-instance protection yet;
- no tray yet;
- no real clipboard message capture;
- no database schema/migrations;
- no catalog rebuild implementation;
- no actual external PNG normalization;
- no integration tests;
- no release artifact;
- branch protection/admin interaction settings are not verifiable with current GitHub App admin permissions; do not infer them from policy documents.

## I. Remaining work

### Mandatory next actions

1. Fresh-check `main` HEAD, CI, `AGENTS.md`, `REQUIREMENTS.md`, `ARCHITECTURE.md`, `OPEN_QUESTIONS.md`.
2. Implement first-run data-root selection and persist `DataRootPath` only after successful validation.
3. Integrate `ApplicationLockStateMachine` into actual App lifecycle.
4. Implement LOCKED UI/password prompt with visible `PasswordHint` and no password persistence.
5. Gate Journal/history/capture services behind successful unlock.
6. Add tests for first-run and lock lifecycle behavior.
7. Keep CI green.
8. Update docs only for behavior actually implemented.

### Next logical tranche after protected shell

9. Design/confirm crypto parameters, then implement key derivation/SQLCipher integration.
10. Implement `AddClipboardFormatListener` + lightweight capture enqueue.
11. Build capture pipeline and application context resolution.
12. Define SQLite schema/current/catalog/archive persistence and FTS.

### Later

- archive transfer/split/maintenance;
- catalog rebuild;
- external file store + PNG normalizer + Trash;
- per-application policies;
- localization loader;
- tray/single-instance;
- packaging/release;
- backup/snapshot design when user returns to it.

## J. Exact resume point

**Следующий чат должен начать с fresh проверки `main` HEAD и CI, затем прочитать `AGENTS.md`, `docs/REQUIREMENTS.md`, `docs/ARCHITECTURE.md`, `docs/OPEN_QUESTIONS.md`. После сверки начать реализацию first-run DataRoot selection + фактического LOCKED lifecycle/password prompt. Реальную криптографию и clipboard capture до фиксации соответствующих параметров не объявлять готовыми и не смешивать с этим tranche.**

## K. First-turn bootstrap instructions

Новый чат обязан:

1. Прочитать этот handoff полностью.
2. Прочитать `AGENTS.md` и полный `docs/WORKFLOW_NEW_CHAT_HANDOFF.md`.
3. Проверить текущий `main` branch/head и последний GitHub Actions status.
4. Перечитать authoritative `REQUIREMENTS.md`, `ARCHITECTURE.md`, `OPEN_QUESTIONS.md`.
5. Если repository state отличается от этого handoff, repository state имеет приоритет.
6. Не пытаться «подогнать» repo под этот checkpoint; исправить assumption handoff-а.
7. Не повторять уже сделанное исследование/код без причины.
8. Не считать existence модели/интерфейса фактом работающего runtime implementation.
9. Не объявлять PASS без fresh evidence.
10. Продолжить именно с `Exact resume point`.
