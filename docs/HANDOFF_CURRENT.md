# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-07.

Этот файл — operational checkpoint. Mutable GitHub state всегда важнее текста handoff. Перед любой durable записью нужен fresh TOCTOU GitHub.

## A. Project identity

Clipensk — Open Source resident Windows clipboard-history manager.

- repository: `lukindv77/Clipensk`;
- canonical branch: `main`;
- platform: Windows x64/AMD64 only; ARM64 вне product scope;
- stack: C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected storage: SQLCipher, один MasterKey на storage;
- production schemas: **Current v6 / Catalog v3 / Archive v1**.

Ключевые документы: `AGENTS.md`, `docs/REQUIREMENTS.md`, `docs/ARCHITECTURE.md`, `docs/CLIPBOARD_HISTORY_SCHEMA.md`, `docs/STORAGE_CATALOG_SCHEMA.md`, `docs/EXTERNAL_PAYLOAD_CATALOG_REBUILD.md`, `docs/ARCHIVE_DATABASE_SCHEMA.md`, `docs/GLOBAL_CAPTURE_POLICY.md`, `docs/CUSTOM_BINARY_FORMAT_CONFIGURATION.md`, `docs/PROTECTED_CLIPBOARD_DELIVERY_COMPOSITION.md`, `docs/CLIPBOARD_WORKER_LIFECYCLE.md`, `docs/OPEN_QUESTIONS.md`.

## B. Workflow rules

- GitHub `main` + exact Actions evidence — source of truth.
- Code changes идут через feature branch.
- Перед `main` update: fresh main + exact feature, compare, `behind=0`, merge-base=current main; только fast-forward `force:false`.
- PASS только после exact-SHA official GitHub Actions evidence на `main`.
- Failure-driven fixes only.
- x64 only.
- Whole storage pair validation before migration mutation.
- Cancellation before COMMIT; committed success не превращать в late cancellation failure.
- Temporary feature Build trigger обязан быть восстановлен byte-for-byte до final compare.
- Policy mutation нельзя делать без cleanup semantics.
- Manual WinUI/real clipboard smoke остаётся UNVERIFIED без evidence.

## C. Canonical main before active external-Catalog tranche

Canonical main:

`99b4bed6fcb426366e919596d72488958dfd1f2d`

Этот main уже содержит:

- Archive v1 create/validate;
- resumable Current→Archive transfer;
- Catalog v3 `ArchiveSegmentIndex` + rebuild/derived sealing;
- unified read-only Current+Archive history repository;
- exact duplicate collapse with physical locations;
- Current-first unified read ordering relative to Archive-first transfer;
- Current v6 / Catalog v3 / Archive v1 docs.

Official exact-SHA evidence на `99b4bed6…`:

- Build #179 / run `34141616581` — SUCCESS;
- Native SQLCipher #44 / run `34141616571` — SUCCESS;
- Native #44 включает pinned SQLCipher x64 build, provenance, encrypted-storage smoke, unpackaged x64 publish, production runtime SQLCipher loading и artifact uploads.

## D. Active feature

Branch:

`feat/external-payload-catalog-rebuild`

Base:

`99b4bed6fcb426366e919596d72488958dfd1f2d`

Owner: **full rebuild содержимого external SHA Catalog projection из Current + Archive history + минимальная session-wide mutation coordination между capture reservation и rebuild**.

Temporary `.github/workflows/build.yml` сейчас включает feature branch для Windows feature CI. Canonical main-only blob:

`657f11356566b459dc46f1639b0d4ea7728083f2`

обязан быть восстановлен перед final compare.

Последний exact code/test SHA с полным green evidence до docs:

`a228295c090303477696bdf7fa316ec08a6411f2`

Build #181 / run `34143412283` — SUCCESS:

- x64-only scope — SUCCESS;
- Restore — SUCCESS;
- Release Build — SUCCESS;
- Test — SUCCESS.

После этого SHA добавлены authoritative docs; latest docs-inclusive head обязан получить отдельный exact Build/Test до promotion.

## E. Session mutation coordination implemented

Добавлен `ProtectedStorageMutationLease` и `ProtectedStorageSessionLease.AcquireMutationLeaseAsync`.

Properties:

- один async-exclusive mutation gate на exact protected storage session;
- wait объединяет caller token + protected-session token;
- lock/session dispose отменяет waiters;
- acquisition повторно проверяет active session;
- lease release idempotent;
- semaphore не dispose-ится отдельно от session object, поэтому поздний release после lock безопасен.

Сегодня gate используется только там, где доказана необходимость:

1. `SqliteClipboardHistorySink.StoreAsync` — lease берётся **до external SHA reservation** и удерживается до Current history COMMIT;
2. `ProtectedExternalPayloadCatalogRebuildService.RebuildAsync` — lease удерживается от physical scan до Catalog replacement COMMIT.

Это закрывает race `new SHA reserved in Catalog -> rebuild deletes reservation -> history row commits with now-unindexed address`.

Current→Archive transfer не обязан брать этот lease для external projection correctness: durable order `Archive COMMIT -> verify -> Current purge` вместе с rebuild order `Current scan -> Archive scan` гарантирует, что external reference будет виден хотя бы в одном source snapshot.

Будущие maintenance writes, удаляющие/переписывающие durable external references, должны использовать тот же gate либо иметь отдельно доказанный эквивалентный ordering contract.

## F. External payload Catalog rebuild implemented

Новый `ProtectedExternalPayloadCatalogRebuildService` полностью восстанавливает `ExternalPayloadAddressIndex` в существующем валидном Catalog v3.

Physical scan:

1. Current ReadOnly первым;
2. canonical top-level `Archive/archive_*.db` в ordinal filename order;
3. каждый Archive full-validates через `ProtectedArchiveDatabaseService`;
4. Archive DatabaseId проверяется вокруг scan;
5. archive filename set сверяется до/после scan;
6. Catalog открывается ReadWrite только после полного preflight.

Projection source of truth — persisted external metadata в `ClipboardHistoryPayload`:

```text
ExternalSha256 + ExternalRelativePath + ExternalSizeBytes
```

Rules:

- SHA lowercase 64-hex;
- non-negative size;
- `ExternalSizeBytes == CanonicalByteCount`;
- relative path обязан оставаться внутри `Files` root;
- exact same SHA/path/size duplicate collapses;
- same SHA with different path/size — fail-closed;
- same path for different SHA — fail-closed;
- collision/validation failure происходит до Catalog mutation.

## G. Physical Files semantics

Rebuild не требует наличия physical external file.

Это intentional recovery contract:

- durable history reference остаётся authoritative metadata даже при missing `Files/...` object;
- rebuild не читает file bytes и не recompute SHA;
- не перемещает файлы в Trash;
- не делает orphan-file GC;
- не изменяет `ArchiveSegmentIndex`.

Stale/orphan rows существующего `ExternalPayloadAddressIndex`, на которые нет history references, удаляются exact rebuild. Cleanup физического файла остаётся future last-reference/Trash GC tranche.

## H. Catalog replacement transaction

После full preflight:

1. валидируется Catalog identity/schema/user_version + v2/v3 tables;
2. одна transaction удаляет старую `ExternalPayloadAddressIndex` projection;
3. validated addresses вставляются целиком;
4. cancellation непосредственно перед COMMIT;
5. COMMIT.

`ArchiveSegmentIndex` не меняется. Ошибка/отмена до COMMIT оставляет прежнюю projection durable. После COMMIT late cancellation не демотирует success.

## I. Recovery scope that remains

Этот tranche восстанавливает **содержимое external projection внутри существующего валидного Catalog v3**.

Он ещё не реализует:

- recreation отсутствующего/повреждённого `storage-catalog.db` файла;
- recovery partial Current/Catalog pair;
- last-reference external cleanup;
- Trash move/retention GC.

Catalog v3 archive-segment projection rebuild и external-address projection rebuild теперь существуют отдельно; будущий full catalog-file recovery должен вызвать/переиспользовать их authoritative derivation contracts после создания нового валидного Catalog.

## J. Regression coverage

Green code/test SHA #181 покрывает:

- Current-only external projection rebuild;
- Archive-only recovery;
- missing physical file не блокирует rebuild;
- exact Current+Archive duplicate collapse;
- same-SHA metadata conflict до Catalog mutation;
- relative-path collision между разными SHA до Catalog mutation;
- `ArchiveSegmentIndex` preservation;
- rebuild waits for existing mutation lease;
- capture sink holds mutation lease before external resolution through Current COMMIT;
- mutation lease serialization;
- caller cancellation waiter;
- protected lock cancellation waiter + safe late release.

## K. Invariants to preserve

- `WM_CLIPBOARDUPDATE` signal/enqueue only.
- Capture only under active unlocked protected access.
- Password never persisted.
- Current/Catalog/Archive share one MasterKey.
- no silent identity merge or invented custom extension/size defaults.
- external payload addresses resolved before history transaction.
- Catalog first stored path wins for exact SHA.
- external file bytes themselves are not encrypted with MasterKey.
- archive ownership is whole calendar days; assigned coverage does not overlap.
- Current→Archive ordering: Archive write -> verify -> Current purge.
- temporary duplicate allowed; missing both copies forbidden.
- Catalog is rebuildable accelerator, not sole authority.
- committed success is not demoted by late cancellation.

## L. Immediate resume steps

1. Fetch latest feature head after docs.
2. Wait for exact docs-inclusive feature Build; require x64/Restore/Build/Test all SUCCESS.
3. Restore `.github/workflows/build.yml` byte-for-byte to canonical main-only blob `657f11356566b459dc46f1639b0d4ea7728083f2`.
4. Fresh fetch `main` immediately before promotion.
5. Compare main→feature; require `behind=0`, merge-base=current main and no workflow diff.
6. Fast-forward main with `force:false` only.
7. Require official Build on exact promoted SHA.
8. Feature touches `src/Clipensk.Storage/**` and `src/Clipensk.Core/Storage/**`, поэтому Native SQLCipher должен trigger на promoted main; require exact promoted-SHA Native SUCCESS before PASS.
9. Manual real WinUI/clipboard smoke remains UNVERIFIED.

## M. Recommended next work after this tranche

Architecture-safe order:

1. full `storage-catalog.db` file recreation / partial Current+Catalog recovery;
2. external-reference last-reference cleanup + Trash GC;
3. policy mutation with required Current/Archive cleanup;
4. archive split/repair/migration;
5. unified JournalWindow UI/search/FTS;
6. manual real clipboard/WinUI smoke;
7. installer/packaging and final license decisions.

Do not implement policy UPDATE as a Current-only shortcut: requirements demand cleanup across Current/Archive and external-reference last-reference semantics.

## Resume rule

Mutable GitHub state supersedes this file. Never repeat completed Archive foundation, Current→Archive transfer, Catalog v3 archive projection, unified history read or external SHA projection rebuild if canonical main already contains a newer verified tranche.
