# Protected clipboard delivery composition

## Назначение и граница этапа

`ProtectedClipboardDeliveryServices.TryCreateAsync` связывает persisted global policy,
индивидуальные policy overrides, durable application identity, Catalog-backed external resolver,
history sink и accepted capture delivery с одной активной `ProtectedStorageSessionLease`.

Latest storage contract — Current v7 / Catalog v3. Current v6 предоставляет storage-scoped
custom-binary extension configuration; Current v7 добавляет пустой по умолчанию durable
`PendingPolicyMaintenance` marker для resumable policy-maintenance lifecycle.

Composition строит **inert graph**. Само создание не читает clipboard payload, не потребляет
capture queue и не вызывает `ProcessNextAsync`. Resident processing принадлежит App worker
lifecycle, описанному в `CLIPBOARD_WORKER_LIFECYCLE.md`.

## Явные зависимости

Caller передаёт:

- активную protected storage session;
- `IClipboardAcceptedCaptureDeliveryFactory`;
- обязательный `IClipboardCustomBinaryFileExtensionProvider`;
- опциональную connection factory существующего SQLCipher boundary;
- cancellation token операции создания.

`ResidentWindowsHost` реализует delivery factory через
`CreateAcceptedCaptureDeliveryPipeline(policyProvider, sink, identityRegistry)`.
Windows layer не получает зависимость от Storage; factory interface остаётся в Core.

Production App создаёт extension repository/provider из **той же active session**, которая
передаётся в `TryCreateAsync`. Ни App, ни resolver не выводят расширение из clipboard format name,
MIME/registry associations или hidden `.bin` default.

## Порядок создания boundary

1. Проверить обязательные зависимости и active session; связать caller/session cancellation.
2. ReadOnly проверить Current v7 `PendingPolicyMaintenance`.
3. Если durable marker присутствует, завершить composition через `PendingPolicyMaintenanceException` до global-policy read/factory creation.
4. Прочитать global policy из Current через `SqliteGlobalClipboardCapturePolicyRepository`.
5. Если policy отсутствует, вернуть `null`, не создавая delivery graph.
6. Для сохранённой policy, включая explicit Deny, собрать capture/history services на той же session.
7. Передать factory policy provider, history sink и durable identity registry.
8. Повторно проверить cancellation/session и вернуть protected delivery wrapper.

Ошибка maintenance/policy/schema/storage не превращается в `null`, Allow или Deny. `null` означает только
отсутствующую global policy. Любая строка pending-maintenance table является conservative fail-closed
blocker; foundation tranche пока не создаёт и не очищает такие строки production-кодом.

При composition Current открывается ReadOnly сначала для durable maintenance gate, затем для global policy.
Catalog, external files, application-policy operations и custom-binary extension lookup остаются lazy до
фактического `ProcessNextAsync`.

## App-level composition и запуск runtime

После `ProtectedDataAccessChanged(true)` событие может прийти до того, как `JournalWindow` успеет
создать `ProtectedStorageSessionLease`. Поэтому App использует deferred-dispatcher шаг и после
возврата unlock-handler повторно проверяет host/lifecycle и наличие active session.

После этих проверок App выполняет composition вне UI thread через `Task.Run`. Результат публикуется
только если совпадают composition generation, window/host/lifecycle references, active session и её
reference identity. Старый результат после lock, close, maintenance suspension или нового composition request не принимается.

Clipboard listener **не запускается просто после unlock**. Если global policy отсутствует,
boundary возвращает `null`; если pending marker существует, composition fail-closed завершается ошибкой.
В обоих случаях App оставляет monitoring выключенным и worker не создаёт. Ошибка composition или active
maintenance suspension также не включает fallback capture path.

Если services non-null, App создаёт exact-session worker generation. Новый worker сначала ждёт
previous worker task, затем повторно проходит generation/session/suspension checks и только после этого
становится единственным queue reader. Listener запускается через window dispatcher **после** этого
ready-reader boundary.

## Maintenance suspension и fresh recompose

Destructive maintenance не должна менять persisted policy, пока старый delivery graph ещё способен завершить capture по закэшированной global policy. App поэтому имеет отдельный quiescence boundary до storage mutation.

`TryQuiesceClipboardRuntimeAsync`:

- атомарно получает unique suspension owner token;
- блокирует новые composition/worker/listener readiness paths;
- инвалидирует уже running composition generation;
- останавливает listener и capture epoch;
- отменяет current worker generation;
- ждёт exact предыдущий worker task до полного завершения.

Только после successful quiesce caller получает owner token и может начинать destructive maintenance. После этого runtime **не** возобновляется автоматически при exception/cancellation maintenance: это fail-closed foundation для durable pending-operation contract.

`TryResumeClipboardRuntimeAfterMaintenance` снимает только exact owner token. Если original protected session всё ещё current, App запускает **новую** composition и заново читает persisted maintenance state и global policy; pre-maintenance `ProtectedClipboardDeliveryServices` не переиспользуется.

Owner token также защищает lock/reopen ABA: stale caller старой session не может снять suspension, уже принадлежащую новой session. Lock сбрасывает только in-memory owner для новой protected session; Current v7 marker сохраняется durable и блокирует capture после reopen, пока будущий recovery workflow явно не завершит maintenance.

Полный порядок listener/worker drain и cancellation semantics описан в `CLIPBOARD_WORKER_LIFECYCLE.md`.

## Post-COMMIT initial setup handoff

Product initial setup сохраняет global policy и explicit custom-binary extension mappings
через `SqliteInitialClipboardCaptureConfigurationService` в одной Current transaction.

После successful aggregate COMMIT JournalWindow отправляет внутреннее notification App. Callback
не является частью durable transaction: его exception не может превратить committed policy/mappings
в UI save failure и не откатывает storage.

App после notification выполняет новый composition request для той же active session. Non-null graph
проходит обычный worker/readiness lifecycle; если session уже revoked/replaced или runtime suspended,
generation/session/suspension guards отбрасывают stale result.

Low-level `SqliteGlobalClipboardCapturePolicyRepository.InitializeAsync` и
`SqliteCustomBinaryFormatConfigurationRepository.InitializeAsync` остаются first-write operations,
но product initial UI не цепляет их последовательно, чтобы не создавать split durable state.

## Custom-binary extension semantics

Для нового custom binary payload resolver выполняет:

1. exact SHA lookup в current Catalog v3 `ExternalPayloadAddressIndex`;
2. existing address используется без provider;
3. для нового SHA provider читает exact `FormatName → FileExtension` из Current v6+;
4. missing/invalid mapping завершается fail-closed;
5. canonical extension участвует в первом content-addressed relative path;
6. first persisted Catalog address остаётся неизменным для duplicate SHA.

Низкоуровневые `ExternalPayloadAddressFactory.ForCustomBinary` и
`ExternalPayloadStore.StoreCustomBinaryAsync` требуют explicit extension parameter; optional `.bin`
fallback отсутствует структурно.

Initial policy UI позволяет explicit custom rows, но не выполняет clipboard-format discovery и не
добавляет неизвестные formats автоматически. Allowed custom row требует extension до aggregate
COMMIT. Полный contract — `CUSTOM_BINARY_FORMAT_CONFIGURATION.md`.

## Worker cancellation и владение

Creation token не является lifetime token возвращённого graph. После создания processing lifetime
ограничен исходной `ProtectedStorageSessionLease` и App-owned worker generation cancellation.

Lock/dispose/reopen не передаёт новый доступ старому graph: repositories, sink, provider и delivery
остаются привязаны к старой session. Composition/worker generation guards запрещают публикацию или
запуск stale result.

При lock App останавливает listener, инвалидирует worker generation и composition generation.
Каждая worker generation имеет App-owned CTS, linked с session token; lock/close/replacement/quiescence явно
отменяют App CTS, а revoke/dispose session независимо отменяет linked session token. Новая session
становится reader только после завершения previous worker task, сохраняя single-reader contract
`ClipboardCaptureQueue`.

Worker не меняет history COMMIT semantics: successful COMMIT не демотируется late cancellation.
Non-cancellation failure одного production capture fail-closed для этого request и не прекращает
последующие resident captures; production queue request dequeued до downstream
source/policy/read/persist processing.

## Проверки и оставшийся evidence

Storage composition tests покрывают unconfigured policy, Allow/Deny, durable identity,
individual overrides, cancellation/lock/dispose, malformed policy, mandatory extension provider,
operation-token lifetime, committed accepted-text path и fail-closed pending-maintenance marker.
Current v7 migration tests отдельно покрывают empty-by-default table, preservation existing state,
Catalog-before-mutation validation, rollback/retry и cancellation-before-COMMIT.

Aggregate initial-configuration tests покрывают единый policy+mapping COMMIT, rollback при custom
insert failure, rejection partial existing setup и pre-open validation prohibited/not-allowed custom
mappings.

Core worker tests покрывают blocked cancellation, продолжение после item failure и serial
`ProcessNextAsync`. Windows x64 Build компилирует App lifecycle, suspension owner-token wiring и dynamic initial-policy UI wiring.

Current v7 foundation **не** реализует production writer marker, policy mutation, destructive Current/Archive cleanup, Catalog rebuild или Trash retention. Эти операции должны использовать уже существующий runtime quiescence boundary и durable marker как отдельный следующий tranche.

**Manual WinUI/real-clipboard smoke остаётся UNVERIFIED**: unit/CI tests не эмулируют настоящий
foreground source application, Windows `WM_CLIPBOARDUPDATE`, WinRT `DataPackageView`, dispatcher race
при suspension и пользовательскую работу custom-format editor.
