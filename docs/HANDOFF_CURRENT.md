# NEW CHAT HANDOFF — Clipensk

Checkpoint prepared: 2026-09-08.

Mutable GitHub state supersedes this file. Перед любой durable записью нужен fresh GitHub TOCTOU.

## A. Project identity

Clipensk — Open Source resident Windows clipboard-history manager.

- repository: `lukindv77/Clipensk`;
- canonical branch: `main`;
- Windows x64/AMD64 only; ARM64 вне scope;
- C# / .NET 10 / WinUI 3 / Windows App SDK;
- protected SQLite: SQLCipher, один MasterKey на storage;
- production schemas на этом checkpoint: **Current v6 / Catalog v3 / Archive v1**.

## B. Workflow invariants

- GitHub `main` + exact Actions evidence — source of truth.
- Code changes идут через feature branch.
- Перед main update: fresh main + exact feature + compare; require `behind=0`, merge-base exactly current main, затем `update_ref(force:false)`.
- Build/PASS не заявлять без exact SHA evidence.
- Temporary feature Build trigger восстановить byte-for-byte перед final compare.
- Canonical main-only `.github/workflows/build.yml` blob: `657f11356566b459dc46f1639b0d4ea7728083f2`.
- Storage/runtime changes, попадающие в Native SQLCipher workflow paths, требуют exact promoted-SHA Native PASS.
- Manual WinUI/real clipboard/recovery smoke остаётся UNVERIFIED без отдельного evidence.

## C. Canonical main before active feature

Fresh main перед `policy-runtime-quiescence` feature:

`d7918b90be474aa653f3c99b87fda13f210132a5`

Этот main содержит completed external-payload Trash GC foundation вместе с ранее завершёнными Archive/Catalog/unified-history tranches.

Exact official evidence на `d7918b90…`:

- Build #209 / run `34194079867` — SUCCESS;
  - x64 scope SUCCESS;
  - Restore SUCCESS;
  - Release Build SUCCESS;
  - Test SUCCESS.
- Native SQLCipher #48 / run `34194079912` — SUCCESS;
  - pinned SQLCipher x64 build SUCCESS;
  - provenance SUCCESS;
  - SQLCipher smoke publish SUCCESS;
  - encrypted storage x64 verification SUCCESS;
  - unpackaged Clipensk x64 publish SUCCESS;
  - published runtime SQLCipher loading SUCCESS;
  - native/runtime artifacts SUCCESS.

Следовательно external-payload Trash GC tranche полностью automated PASS на canonical main.

## D. Active feature — policy runtime quiescence

Branch:

`feat/policy-runtime-quiescence`

Base:

`d7918b90be474aa653f3c99b87fda13f210132a5`

Owner: **App-level stop/drain/resume boundary, обязательный до destructive policy cleanup/mutation**.

Temporary `build.yml` feature trigger включён до final validation и обязан исчезнуть из final main diff.

Последний exact runtime/test SHA до docs:

`589e786f77d9a78f71098d2ea67d5316abd0ce20`

Build #216 / run `34204882406` — SUCCESS:

- x64-only scope SUCCESS;
- Restore SUCCESS;
- Release Build SUCCESS;
- Test SUCCESS.

После `589e786f…` меняются только authoritative docs. Docs-inclusive feature tip обязан получить отдельный exact Build/Test перед promotion.

Если fresh GitHub уже показывает `main` равным final feature SHA, считать promotion выполненным и проверять official main Build вместо повторного merge.

## E. Quiescence contract

App runtime теперь имеет in-memory maintenance suspension, участвующую в:

- `RequestClipboardDeliveryComposition`;
- composition publication validation;
- `RequestClipboardWorkerStart`;
- `IsCurrentClipboardWorker`;
- `IsCurrentClipboardRuntimeSession` до и после listener Start.

`TryQuiesceClipboardRuntimeAsync` работает только для exact current window/host/lifecycle/session.

Порядок:

1. проверить caller cancellation и exact protected session;
2. атомарно получить unique non-zero suspension owner token;
3. повторно проверить exact session;
4. invalidate composition generation;
5. `StopClipboardMonitoring`, что инвалидирует capture epoch;
6. invalidate worker generation и cancel exact App-owned worker CTS;
7. **безусловно дождаться exact previous worker task completion**;
8. только затем вернуть owner token.

До завершения worker task caller не получает quiesced state.

## F. Why owner token, not bool

Boolean suspension имеет ABA race:

`old owner=1 → lock resets 0 → new session acquires 1 → stale old caller clears 1`.

Текущий contract использует monotonic unique owner token. Release выполняется CAS только против exact token, поэтому stale old-session caller не может снять suspension новой session.

Lock/revoke сбрасывает in-memory owner, чтобы новая protected session не наследовала transient App state. Это **не** recovery contract для будущей durable maintenance: незавершённая destructive operation должна отдельно блокироваться Current pending-operation marker после reopen.

Window close suspension не снимает и resume не вызывает.

## G. Cancellation and stale-capture safety

После owner acquisition quiesce не возвращается по caller cancellation, пока old worker не завершён. Worker уже cancelled, поэтому drain остаётся обязательной safety boundary.

Если caller token отменён во время drain, destructive maintenance ещё не начиналась. После worker completion exact suspension owner освобождается, и только для той же active session запрашивается fresh composition; затем cancellation возвращается caller'у.

После successful quiesce никакого auto-resume нет. Future maintenance exception/cancellation может оставить capture suspended fail-closed.

`TryResumeClipboardRuntimeAfterMaintenance`:

- снимает только exact owner token;
- требует original protected session всё ещё current для restart;
- создаёт **fresh composition** и заново читает persisted global policy;
- старый `ProtectedClipboardDeliveryServices` не переиспользуется.

History sink уже имеет cancellation check непосредственно перед SQLite COMMIT. Поэтому после worker drain already-dequeued capture либо отменён до COMMIT, либо его COMMIT уже durable завершился и будущий cleanup должен увидеть/удалить запись по новой policy. Successful COMMIT не демотируется late cancellation.

## H. Scope / non-goals

Quiescence feature не меняет:

- Current/Catalog/Archive schema;
- global/application policy rows;
- history rows;
- external files/Trash;
- Catalog projections;
- custom-binary mappings;
- UI policy editor.

Обычные `src/Clipensk.App/*.cs` не входят в текущий Native SQLCipher workflow path filter, поэтому этот App-only tranche ожидает official Build после promotion, но сам по себе не должен запускать новый Native workflow. Не заявлять это как PASS до фактического official Build на promoted SHA.

Manual quiesce во время реального `WM_CLIPBOARDUPDATE`/WinRT read и lock/reopen dispatcher races остаётся UNVERIFIED.

## I. Finalize current feature

1. Получить exact docs-inclusive feature head.
2. Require feature Build: x64/Restore/Build/Test SUCCESS.
3. Fresh read `.github/workflows/build.yml`, затем restore canonical bytes/blob `657f1135…`.
4. Fresh read `main` и feature head.
5. Compare main→feature; require `behind=0`, merge-base=current main, no workflow diff; intended final files:
   - `src/Clipensk.App/App.ClipboardWorker.cs`;
   - `src/Clipensk.App/App.ProtectedClipboardDelivery.cs`;
   - `src/Clipensk.App/App.xaml.cs`;
   - lifecycle/policy/composition docs;
   - this handoff.
6. Fast-forward `main` with `force:false`.
7. Fresh fetch main; require exact feature SHA.
8. Require official Build on exact promoted SHA.
9. Check whether Native workflow actually triggered. По current path filter для этих App `.cs` changes он не ожидается; не выдумывать run, если GitHub его не создал.

## J. Next architecture-safe tranche after quiescence PASS

**Current v7 resumable policy-maintenance foundation**, затем actual cleanup/mutation.

Recommended boundary:

1. Current v7 добавляет empty-by-default durable pending-maintenance marker; no seed/default policy changes.
2. v6→v7 migration отдельная atomic step после full Current/Catalog pair validation, с rollback/retry/cancellation tests.
3. Protected delivery composition fail-closed не запускает resident capture, пока pending marker существует.
4. Current→Archive transfer должен брать тот же session mutation lease, что policy cleanup DB phase; иначе transfer может вынести запрещённый Current payload в Archive между cleanup phases.
5. Policy mutation DB phase под mutation lease:
   - new policy + Current cleanup + pending marker durable publication;
   - external-format cleanup также idempotently проходит Archive maintenance writes;
   - обычные non-external Archive DB-data по REQUIREMENTS §18 не удаляются.
6. После authoritative Current+Archive cleanup rebuild Catalog projection.
7. Затем existing `ProtectedExternalPayloadTrashCollector` переносит только genuinely unreferenced managed files в Trash.
8. Clear pending marker только после required durable cleanup/rebuild boundary; crash/reopen должен уметь resume, а не запускать capture по half-finished policy maintenance.

Не добавлять shortcut `UPDATE GlobalCapturePolicy` без cleanup lifecycle.

## K. Requirement semantics for disable

По REQUIREMENTS §18:

- отключённые актуальные DB-данные удаляются из Current;
- DB-данные, уже только в Archive, обычно сохраняются;
- для external-file formats ссылки удаляются и из Current, и из Archive;
- physical external file уходит в Trash только когда после cleanup не осталось допустимых ссылок.

Persisted `PayloadKind`, а не догадка по `FormatName`, должен определять external-row handling.

Event header не удаляется автоматически при удалении последнего payload row: FK идёт Event→Payload `ON DELETE CASCADE`, не наоборот. Future cleanup должен явно определить/реализовать event-with-zero-payload semantics.

## Resume rule

Не повторять завершённые Archive, transfer, Catalog v3, unified history, Catalog rebuild/recovery/replacement или Trash GC tranches, если fresh canonical `main` уже содержит их exact promoted commits.
