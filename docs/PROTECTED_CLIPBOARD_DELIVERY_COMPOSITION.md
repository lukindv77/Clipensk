# Protected clipboard delivery composition

## Назначение и граница этапа

`ProtectedClipboardDeliveryServices.TryCreateAsync` связывает persisted global policy,
индивидуальные policy overrides, durable application identity, Catalog-backed external resolver,
history sink и accepted capture delivery с одной активной `ProtectedStorageSessionLease`.

Latest storage contract — Current v6 / Catalog v2. Current v6 предоставляет storage-scoped
custom-binary extension configuration; production App использует её через
`SqliteCustomBinaryFormatConfigurationRepository` и
`RepositoryClipboardCustomBinaryFileExtensionProvider`.

Composition строит **inert graph**. Само создание не читает clipboard payload, не потребляет
capture queue, не вызывает `ProcessNextAsync` и не запускает worker. Запуск resident processing
принадлежит отдельному App lifecycle, описанному в `CLIPBOARD_WORKER_LIFECYCLE.md`.

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
2. Прочитать global policy из Current через `SqliteGlobalClipboardCapturePolicyRepository`.
3. Если policy отсутствует, вернуть `null`, не создавая delivery graph.
4. Для сохранённой policy, включая explicit Deny, собрать capture/history services на той же session.
5. Передать factory policy provider, history sink и durable identity registry.
6. Повторно проверить cancellation/session и вернуть protected delivery wrapper.

Ошибка policy/schema/storage не превращается в `null`, Allow или Deny. `null` означает только
отсутствующую global policy.

При создании открывается только Current ReadOnly для global policy. Catalog, external files,
application-policy operations и custom-binary extension lookup остаются lazy до фактического
`ProcessNextAsync`.

## App-level composition и запуск runtime

После `ProtectedDataAccessChanged(true)` событие приходит до того, как `JournalWindow` успевает
создать `ProtectedStorageSessionLease`. Поэтому App использует deferred-dispatcher шаг и после
возврата unlock-handler повторно проверяет host/lifecycle и наличие active session.

После этих проверок App выполняет composition вне UI thread через `Task.Run`. Результат публикуется
только если совпадают composition generation, window/host/lifecycle references, active session и её
reference identity. Старый результат после lock, close или нового composition request не принимается.

Clipboard listener **не запускается просто после unlock**. Если global policy отсутствует,
boundary возвращает `null`; App оставляет monitoring выключенным и worker не создаёт. Ошибка
composition также не включает fallback capture path.

Если services non-null, App создаёт exact-session worker generation. Новый worker сначала ждёт
предыдущий worker task, затем повторно проходит generation/session checks и только после этого
становится единственным queue reader. Listener запускается через window dispatcher **после** этого
ready-reader boundary. Поэтому request не может попасть в новую epoch, пока reader ещё заблокирован
ожиданием старой session; source-application resolution не откладывается за этот handoff.

После первого успешного `GlobalCapturePolicy.InitializeAsync` JournalWindow отправляет внутреннее
уведомление App **после COMMIT**. Ошибка подписчика не может превратить already committed policy в
UI failure. App выполняет новый composition request, и non-null graph проходит тот же worker/readiness
lifecycle.

При lock App останавливает listener, инвалидирует worker generation и composition generation.
Каждая worker generation имеет App-owned CTS, linked с session token; lock/close/replacement явно
отменяют App CTS, а revoke/dispose session независимо отменяет linked session token. Новая session
становится reader только после завершения task предыдущего worker, сохраняя single-reader contract
`ClipboardCaptureQueue`.

## Custom-binary extension semantics

Для нового custom binary payload resolver выполняет:

1. exact SHA lookup в Catalog v2;
2. existing address используется без provider;
3. для нового SHA provider читает exact `FormatName → FileExtension` из Current v6;
4. missing/invalid mapping завершается fail-closed;
5. canonical extension участвует в первом content-addressed relative path;
6. first persisted Catalog address остаётся неизменным для duplicate SHA.

Низкоуровневые `ExternalPayloadAddressFactory.ForCustomBinary` и
`ExternalPayloadStore.StoreCustomBinaryAsync` требуют explicit extension параметр; optional `.bin`
fallback отсутствует структурно.

Полный storage contract — `CUSTOM_BINARY_FORMAT_CONFIGURATION.md`.

## Отмена и владение

Creation token не является lifetime token возвращённого graph. После создания processing lifetime
ограничен исходной `ProtectedStorageSessionLease` и App-owned worker generation cancellation.

Lock/dispose/reopen не передаёт новый доступ старому graph: repositories, sink, provider и delivery
остаются привязаны к старой session. Composition/worker generation guards дополнительно запрещают
публикацию или запуск stale result.

Worker не меняет history COMMIT semantics: successful COMMIT не демотируется late cancellation.
Non-cancellation failure одного production capture fail-closed для этого request и не прекращает
последующие resident captures; protected wrapper до inner pipeline выполняет только cancellation
gates, а production queue request dequeued до downstream source/policy/read/persist processing.

## Проверки и оставшийся evidence

Storage composition tests покрывают unconfigured policy, Allow/Deny, durable identity,
individual overrides, cancellation/lock/dispose, malformed policy, mandatory extension provider,
operation-token lifetime и committed accepted-text path. Current v6 tests отдельно покрывают
extension repository/provider и migration.

Core worker tests покрывают blocked cancellation, продолжение после item failure и serial
`ProcessNextAsync`. Windows x64 Build компилирует App lifecycle wiring.

После успешного CI этот tranche делает автоматический capture runtime архитектурно связанным end-to-end.
Однако **manual WinUI/real-clipboard smoke остаётся UNVERIFIED**: unit/CI tests не эмулируют настоящий
foreground source application, Windows `WM_CLIPBOARDUPDATE` и WinRT `DataPackageView`.
