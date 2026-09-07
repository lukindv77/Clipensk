# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-07.

Этот файл — operational checkpoint. Mutable GitHub state всегда важнее текста handoff. После публикации этого файла exact `main` SHA изменится, поэтому перед любой новой записью следующий чат обязан сделать fresh TOCTOU-проверку GitHub.

## A. Project identity

Clipensk — Open Source резидентный Windows clipboard-history manager.

- repository: `lukindv77/Clipensk`;
- canonical branch: `main`;
- platform: Windows x64/AMD64 only; ARM64 не поддерживается;
- stack: C# / .NET 10 / WinUI 3 / Windows App SDK;
- development/runtime host: unpackaged;
- protected storage: SQLCipher, один MasterKey на storage;
- production schema на verified functional baseline: Current v6 / Catalog v2.

Ключевые документы: `AGENTS.md`, `docs/REQUIREMENTS.md`, `docs/ARCHITECTURE.md`, `docs/CURRENT_DATABASE_SCHEMA.md`, `docs/CLIPBOARD_HISTORY_SCHEMA.md`, `docs/STORAGE_CATALOG_SCHEMA.md`, `docs/GLOBAL_CAPTURE_POLICY.md`, `docs/CUSTOM_BINARY_FORMAT_CONFIGURATION.md`, `docs/PROTECTED_CLIPBOARD_DELIVERY_COMPOSITION.md`, `docs/CLIPBOARD_WORKER_LIFECYCLE.md`, `docs/OPEN_QUESTIONS.md`.

## B. User intent and working style

Пользователь последовательно просит продолжать разработку Clipensk, не повторяя уже завершённые этапы.

Рабочие правила:

- GitHub `main` и exact Actions evidence — source of truth;
- перед durable write делать fresh TOCTOU;
- работать через feature/docs branches;
- перед `main` update требуются fresh main, compare, `behind=0`, merge-base=current main и только fast-forward `force:false`;
- PASS нельзя объявлять без exact-SHA evidence обязательных workflow/steps;
- failure-driven fixes only, без speculative unrelated изменений;
- не запускать тяжёлую работу из `WM_CLIPBOARDUPDATE`;
- не подключать policy mutation без обязательной cleanup semantics;
- пользователь выбирает смену model/reasoning level; автоматически модель не переключать.

## C. Current authoritative functional baseline

Последний подтверждённый **functional** `main` до этого docs-only checkpoint:

`797d736bf1ae22d329f46a4b293a6aa76b00f3ed`

Этот SHA включает initial custom-binary policy setup UI и atomic aggregate persistence. Он был fresh-проверен 2026-09-07 перед docs-sync.

Exact Actions на этом SHA:

- Build #158 — run `34102717624`, completed / SUCCESS, `head_sha=797d736bf1ae22d329f46a4b293a6aa76b00f3ed`;
- Native SQLCipher #40 — run `34102717645`, completed / SUCCESS, тот же exact head SHA.

Build #158 подтвердил x64 scope, Restore, Build и Test. Native #40 подтвердил pinned SQLCipher x64 chain и published runtime checks по существующему workflow contract.

Этот handoff публикуется отдельным docs commit после functional baseline, поэтому **не считать `797d...` текущим main без fresh fetch**. После publication нужно определить новый exact `main` и exact Actions для него.

Manual real-clipboard / WinUI smoke остаётся UNVERIFIED.

## D. Current owner / active task

Текущий следующий кодовый owner — **Archive/Maintenance foundation**, а не direct policy UPDATE.

Root cause: REQUIREMENTS §18 требует cleanup при последующих изменениях policy. Для external-file formats ссылки должны удаляться не только из Current, но и из Archive; physical file отправляется в Trash только после исчезновения последней допустимой ссылки. Текущий Storage runtime не имеет archive database implementation/maintenance coordinator, поэтому безопасный policy mutation пока невозможно реализовать честно.

Acceptance direction следующего tranche:

1. отдельный archive database boundary;
2. self-describing encrypted Archive identity;
3. exact filename/base/split/coverage validation;
4. history-compatible archive schema без ослабления referential contract;
5. ReadOnly normal access, ReadWrite только maintenance;
6. tests для corruption/cancellation/coverage mismatch;
7. только после этого — resumable Current→Archive transfer;
8. policy cleanup/update ещё позже.

## E. What has been completed

### Protected capture pipeline

- `WM_CLIPBOARDUPDATE` остаётся signal/enqueue only.
- Один clipboard snapshot проходит discovery/read pipeline.
- standard readers: text/HTML/RTF, links, PNG-normalized bitmap, StorageItems.
- registered/private custom binary reader поддержан отдельно.
- prohibited: CF_WAVE, CF_RIFF, virtual-file contents; CF_HDROP не копирует file contents.
- exact MaxBytes semantics зафиксированы per canonical representation.

### Durable application identity

- Clipensk-owned ApplicationId;
- PID/HWND/path не являются durable identity;
- identity conflict fail-closed;
- source runtime snapshot сохраняется отдельно от durable identity.

### Current history

- history schema введена в Current v4 и остаётся совместимой в Current v6;
- ordered payloads, exact canonical byte counts, external references;
- external address resolved до history SQL transaction;
- cancellation перед COMMIT, committed success остаётся success;
- bounded Current read + keyset continuation реализованы.

### Catalog v2 / external payload addresses

- `SHA-256(exact stored bytes) -> RelativePath + SizeBytes`;
- first stored path wins;
- same-SHA size conflict/path collision fail-closed;
- existing file проверяется exact size+SHA;
- PNG extension `.png`;
- existing custom SHA не требует extension provider.

### Global capture policy

- storage-scoped encrypted Current state;
- absent policy отличается от explicit Deny;
- initial setup требует явных Allow/Deny и explicit limited/unlimited;
- no preselected formats/sizes;
- global Deny — inherited baseline, не kill switch для app override model;
- policy read errors не превращаются в null/Allow/Deny.

### Custom binary extension configuration

Current v6 добавляет storage-scoped exact mapping `FormatName -> FileExtension`.

- exact BINARY format-name keying;
- canonical extension normalization;
- first-write-only, no rebind/update API;
- new custom SHA требует explicit mapping/provider;
- missing mapping fail-closed;
- existing Catalog SHA bypasses provider;
- production provider: `RepositoryClipboardCustomBinaryFileExtensionProvider`.

### Initial aggregate capture configuration

На functional baseline `797d...` добавлен `SqliteInitialClipboardCaptureConfigurationService`.

- initial global policy + custom extension mappings записываются одной Current transaction;
- partial policy/mapping durable state при failure/cancellation не допускается;
- cancellation проверяется до COMMIT;
- late cancellation после successful COMMIT не демотирует успех;
- partial pre-existing initial configuration отклоняется fail-closed.

WinUI initial setup теперь позволяет явно добавить custom binary row с exact format name, Allow/Deny, extension и MaxBytes/unlimited. Custom format не добавляется автоматически.

### App protected delivery and worker

App уже:

- получает active `ProtectedStorageSessionLease`;
- создаёт storage-backed custom extension repository/provider;
- вызывает protected delivery composition после active session/policy readiness;
- запускает `ClipboardAcceptedCaptureWorker` только при готовом protected graph;
- останавливает monitoring/worker и инвалидирует старый graph при lock/dispose/reopen;
- не запускает worker, если initial global policy отсутствует.

Worker lifecycle contract находится в `docs/CLIPBOARD_WORKER_LIFECYCLE.md`.

## F. Current conclusions

1. **Production Current = v6, Catalog = v2.** History tables сами по себе остаются v4-compatible contract внутри v6.
2. **Custom extension source больше не неизвестен.** Он хранится в encrypted Current v6 и читается repository-backed provider-ом.
3. **Initial custom policy setup должен быть aggregate/atomic.** Separate first-write repository calls недостаточны для product path.
4. **Policy mutation нельзя реализовывать как простой UPDATE.** Cleanup semantics обязательна.
5. **Archive foundation — prerequisite для policy cleanup external formats.** Current-only cleanup нарушит requirements.
6. **Archive schema не должна молча ослаблять history FK.** Текущий `ClipboardHistoryEvent.SourceApplicationId` ссылается на `ApplicationIdentity`; archive должен оставаться self-contained и referentially valid.
7. Наиболее совместимое направление — archive содержит необходимый ApplicationIdentity слой вместе с history rows; перенос всех mutable Current overlays автоматически не предполагается.
8. `storage-catalog.db` остаётся rebuildable accelerator; критическая archive metadata должна жить в самих archive DB.

## G. Important invariants

- x64 only.
- Password не сохраняется.
- Monitoring/capture только при unlocked protected access.
- Lock/close очищает protected UI/session-derived state.
- One snapshot from discovery through readers.
- Full `EventTimeContext` durable.
- No silent durable identity merge.
- No invented custom format/extension/size defaults.
- HTML SearchText после size gate; RTF SearchText сейчас null.
- external files не шифруются MasterKey.
- external payload address resolved before history transaction.
- Catalog first stored path wins globally per SHA.
- whole storage pair validation before migration mutation.
- archive ownership = whole calendar days; archive coverages must not overlap.
- archive normal mode ReadOnly; writes только maintenance/transfer/cleanup/split/migration/recovery.
- Current→Archive: write → verify → only then purge Current; temporary duplicate допустим, missing both copies запрещён.
- Archive coverage не сужается автоматически из-за cleanup rows.

## H. Known risks and unresolved questions

- Archive SQLCipher database implementation отсутствует.
- Current→Archive transfer отсутствует.
- Unified Current+Archive journal query отсутствует.
- Catalog rebuild по Current+Archive отсутствует.
- Policy mutation/cleanup отсутствует.
- Trash reference tracking/GC across Current+Archive отсутствует.
- FTS/unified search не завершены.
- Archive application-identity copy/minimization contract нужно окончательно оформить до implementation.
- финальная packaging scheme MSIX/portable не выбрана;
- лицензия Open Source ещё не выбрана;
- manual real clipboard/WinUI smoke не выполнен.

Низкоуровневые optional `.bin` defaults в `ExternalPayloadAddressFactory.ForCustomBinary` / `ExternalPayloadStore.StoreCustomBinaryAsync` исторически могут существовать; production new-custom resolver path на них не полагается. Удаление этих defaults — отдельный hardening change с новым CI evidence, не смешивать с archive tranche.

## I. Remaining work — priority

1. После публикации этого docs checkpoint сделать fresh `main` fetch и exact Build/Native verification для docs SHA.
2. Создать feature branch для archive foundation от fresh exact main.
3. Зафиксировать archive schema/identity contract:
   - DatabaseRole.Archive;
   - filename `archive_######[_####].db` ↔ identity base/split;
   - mandatory coverage start/end;
   - coverage validity and whole-day ownership;
   - ApplicationIdentity + ClipboardHistory referential compatibility;
   - ReadOnly validator/open boundary.
4. Реализовать archive create/validate tests, включая wrong key/storage/role/version/filename/coverage/schema corruption/cancellation.
5. Затем отдельным tranche реализовать resumable Current→Archive transfer с write/verify/purge ordering.
6. Затем Catalog archive metadata/rebuild/query composition.
7. Только после archive cleanup foundation — policy mutation + Current/Archive cleanup + Trash last-reference handling.
8. Выполнить manual WinUI/real clipboard smoke перед заявлениями о product readiness.

## Resume rule

При новом чате сначала:

1. прочитать этот handoff и `AGENTS.md`;
2. fresh fetch `main`;
3. проверить open PR/issues и последние Actions по exact main SHA;
4. считать GitHub state authoritative над любым SHA/status из этого файла;
5. не повторять completed custom-policy/delivery/worker работу;
6. продолжать с archive foundation, если mutable state не показывает более новый superseding tranche.
