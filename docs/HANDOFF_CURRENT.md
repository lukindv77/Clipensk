# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-08.

Mutable GitHub state supersedes this file. Перед любой durable записью нужен fresh GitHub TOCTOU.

## A. Project identity

Clipensk — Open Source resident Windows clipboard-history manager.

- repository: `lukindv77/Clipensk`;
- canonical branch: `main`;
- platform: Windows x64/AMD64 only; ARM64 вне product scope;
- stack: C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected storage: SQLCipher, один MasterKey на storage;
- production schemas: **Current v6 / Catalog v3 / Archive v1**.

Ключевые storage docs: `CURRENT_DATABASE_SCHEMA.md`, `CLIPBOARD_HISTORY_SCHEMA.md`, `STORAGE_CATALOG_SCHEMA.md`, `EXTERNAL_PAYLOAD_CATALOG_REBUILD.md`, `EXTERNAL_PAYLOAD_TRASH_GC.md`, `STORAGE_CATALOG_RECOVERY.md`, `ARCHIVE_DATABASE_SCHEMA.md`.

## B. Workflow rules

- GitHub `main` + exact Actions evidence — source of truth.
- Code changes идут через feature branch.
- Перед `main` update: fresh main + exact feature, compare, `behind=0`, merge-base=current main; fast-forward только `force:false`.
- PASS только после exact-SHA official Build и, для storage/runtime changes, Native SQLCipher evidence на `main`.
- Failure-driven fixes only.
- Temporary feature Build trigger обязан быть восстановлен byte-for-byte до final compare.
- Whole storage pair validation before migration mutation.
- Cancellation before COMMIT/publication; committed durable success не демотируется late cancellation.
- Manual WinUI/real clipboard/recovery smoke остаётся UNVERIFIED без отдельного evidence.

Canonical main-only `build.yml` blob:

`657f11356566b459dc46f1639b0d4ea7728083f2`

## C. Canonical main

Canonical main перед текущим feature:

`4c438cd0697861d8366c34936521af940df988cf`

Этот main уже содержит:

- Archive v1 create/validate;
- resumable Current→Archive transfer;
- Catalog v3 `ArchiveSegmentIndex` + derived sealing;
- unified read-only Current+Archive history;
- full external SHA Catalog projection rebuild;
- session-wide mutation lease между capture SHA reservation→Current COMMIT и external-Catalog rebuild;
- explicit pre-session recreation отсутствующего Catalog;
- explicit pre-session replacement существующего damaged/stale Catalog с quarantine backup прежних exact bytes.

Exact official evidence на `4c438cd0…`:

- Build #200 — SUCCESS;
- Native SQLCipher #47 / run `34188687273` — SUCCESS;
- Native #47 завершил pinned SQLCipher x64 build, provenance, encrypted-storage verification, unpackaged publish, production runtime SQLCipher loading и artifact publication.

Следовательно damaged-Catalog replacement полностью PASS на canonical main.

## D. Active feature

Branch:

`feat/external-payload-trash-gc`

Base:

`4c438cd0697861d8366c34936521af940df988cf`

Owner: **history-unreferenced external payload Files→Trash collector / last-reference physical cleanup foundation**.

Temporary `.github/workflows/build.yml` сейчас включает feature branch. Перед final compare он обязан быть восстановлен к canonical blob `657f1135…`.

Последний exact runtime/test SHA:

`a50960f8b0df300e692783921cdc320e4a4d3f86`

Build #204 / run `34190036583` — SUCCESS:

- x64-only scope — SUCCESS;
- Restore — SUCCESS;
- Release Build — SUCCESS;
- Test — SUCCESS.

После `a50960f8…` меняются только authoritative docs. Docs-inclusive head обязан получить отдельный exact Build/Test перед promotion.

## E. Trash GC implementation

`ProtectedExternalPayloadTrashCollector.CollectAsync` работает только с active `ProtectedStorageSessionLease`.

Collector не удаляет history rows и не меняет policy. Его input — explicit `deletionDate` + cancellation token.

Основной contract:

1. запускается `ProtectedExternalPayloadCatalogRebuildService.RebuildAsync`, чтобы вывести projection только из authoritative Current+Archive history и убрать stale capture reservations;
2. после rebuild collector получает session mutation lease;
3. под lease Catalog перечитывается только как conservative keep-set;
4. live Current/Archive references сохраняют physical object;
5. только history-unreferenced canonical managed objects становятся Trash candidates.

Catalog остаётся rebuildable accelerator и сам по себе не является durable reference.

## F. Capture race safety

Capture резервирует external SHA/address и завершает Current history COMMIT, удерживая тот же session mutation lease.

Поэтому capture между rebuild и collector lease не теряется: к моменту post-lease keep-set read его reservation уже видна.

Capture crash после reservation и до history COMMIT может временно оставить stale reservation и сохранить orphan object ещё на один GC pass. Это допустимый safe leak, не data loss.

Current→Archive transfer не создаёт новый content address и сохраняет archive-first durable order, поэтому не создаёт отдельный last-reference race.

## G. Managed Files and Trash layout

Collector рассматривает только exact canonical objects:

```text
Files/<yyyy-MM-dd>/<64 lowercase hex SHA-256>.<lowercase extension>
```

Source bytes перед publication exact SHA-256 проверяются против filename. Canonical-looking object с другими bytes завершает operation fail-closed. Noncanonical/foreign files игнорируются.

Trash publication:

```text
Trash/<deletion-date>/<original Files relative path>
```

Например:

```text
Trash/2026-09-08/2026-08-31/<sha>.png
```

Если exact destination уже существует, source удаляется только после повторной SHA/size validation destination. Иначе используется `File.Move`.

Retention purge самого Trash — отдельный future maintenance boundary.

## H. Reparse-point containment

Collector fail-closed запрещает reparse traversal в managed paths:

- configured `Files` root;
- source date directory;
- source file;
- существующие `Trash` ancestors/destination;
- newly created Trash directories после создания.

Это закрывает обычные symlink/junction escapes из data root. Hostile kernel-level TOCTOU между userspace check и syscall не заявлен как решённый; для такого threat model нужен отдельный handle-based Windows filesystem contract.

## I. Cancellation/crash semantics

Полный candidate preflight выполняется до filesystem publication.

Для каждого planned object непосредственно перед publication source fingerprint пересчитывается. Cancellation проверяется до каждого individual move/delete.

Каждый успешный `File.Move` или duplicate-source `File.Delete` — отдельная durable publication boundary. Late cancellation уже выполненную publication не демотирует и не откатывает.

Batch поэтому может быть partially progressed между objects. Повторный collector pass безопасно продолжает с оставшимися Files objects.

## J. Regression history

Build #201:

- x64/Restore/Release Build SUCCESS;
- Test failure был только из-за неверного test assumption, что bootstrap не создаёт корневой `Trash`;
- product implementation не менялся для этого failure.

Build #202 после test-only assertion fix — полный SUCCESS.

Build #203 после reparse-point product hardening — полный SUCCESS.

Build #204 на final runtime/test SHA `a50960f8…` — полный SUCCESS, включая targeted Windows directory-symlink regression, когда runner/OS разрешает symbolic-link creation.

Coverage включает:

- stale Catalog reservation + orphan object;
- live Current reference;
- live Archive reference;
- wrong canonical bytes fail-closed;
- noncanonical file ignore;
- existing exact Trash copy;
- cancellation before publication;
- mutation lease serialization;
- reparse-point containment.

## K. Explicit non-goals

Текущий tranche не реализует:

- удаление history references;
- global/per-app policy mutation;
- required Current+Archive cleanup при policy disable;
- Trash retention purge;
- missing external payload byte recovery;
- maintenance/recovery UI.

Особенно запрещён Current-only policy UPDATE shortcut: external-format disable должен сначала удалить соответствующие durable references из Current и Archive, затем выполнить last-reference physical cleanup через этот foundation.

## L. Immediate resume steps

1. Fetch latest docs-inclusive feature head.
2. Require exact docs-inclusive feature Build: x64/Restore/Build/Test SUCCESS.
3. Restore `.github/workflows/build.yml` byte-for-byte to blob `657f11356566b459dc46f1639b0d4ea7728083f2`.
4. Fresh fetch current `main` immediately before promotion.
5. Compare main→feature; require `behind=0`, merge-base=current main, no workflow diff and only intended product/tests/docs files.
6. Fast-forward main with `force:false`.
7. Require official Build on exact promoted SHA.
8. Feature changes `src/Clipensk.Storage/**`, поэтому require exact promoted-SHA Native SQLCipher SUCCESS before full PASS.
9. Manual real WinUI/clipboard smoke remains UNVERIFIED.

## M. Recommended next work after Trash GC

Architecture-safe order:

1. multi-DB policy cleanup/mutation boundary across Current + Archive;
2. invoke last-reference Trash collection only after durable forbidden references are removed;
3. Trash retention purge (default requirement currently 30 days, product configuration still separate);
4. quarantine retention/user recovery UI;
5. archive split/repair/migration;
6. unified JournalWindow UI/search/FTS;
7. manual real clipboard/recovery smoke;
8. installer/packaging and final license decisions.

## Resume rule

Never repeat completed Archive foundation, Current→Archive transfer, Catalog v3, unified history read, external SHA projection rebuild, missing-Catalog recreation or existing-Catalog replacement if canonical `main` already contains the verified tranche.
