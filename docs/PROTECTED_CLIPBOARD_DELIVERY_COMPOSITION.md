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
capture queue, не вызывает `ProcessNextAsync` и не запускает worker.

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

## App-level composition lifecycle

`App` теперь подключает этот boundary к unlock lifecycle, но **не запускает worker**.

После `ProtectedDataAccessChanged(true)` событие приходит до того, как `JournalWindow` успевает
создать `ProtectedStorageSessionLease`. Поэтому App сохраняет существующий deferred-dispatcher
шаг и после возврата unlock-handler повторно проверяет:

- тот же `ResidentWindowsHost`;
- тот же `ProtectedApplicationLifecycle`;
- `CanAccessProtectedData`;
- наличие active session в `JournalWindow`.

После этих проверок App оставляет существующий clipboard listener lifecycle и отдельно запускает
composition вне UI thread через `Task.Run`. Для session создаются
`SqliteCustomBinaryFormatConfigurationRepository` и
`RepositoryClipboardCustomBinaryFileExtensionProvider`, затем вызывается
`ProtectedClipboardDeliveryServices.TryCreateAsync`.

Результат публикуется в retained App field только если совпадают composition generation,
window/host/lifecycle references, active session и её reference identity. Старый результат после
lock, close или нового composition request не принимается.

Если global policy ещё не настроена, boundary возвращает `null`; App не подменяет это Allow/Deny
и не создаёт worker.

После первого успешного `GlobalCapturePolicy.InitializeAsync` JournalWindow отправляет внутреннее
уведомление App **после COMMIT**. Уведомление изолировано от результата durable операции: ошибка
подписчика не может превратить уже committed policy в UI failure. App выполняет новый composition
request с той же generation/session защитой.

При lock и при закрытии App увеличивает generation и очищает retained composition reference.
Старая session одновременно теряет protected access через существующий lifecycle cancellation.

## Custom-binary extension semantics

Для нового custom binary payload resolver выполняет:

1. exact SHA lookup в Catalog v2;
2. existing address используется без provider;
3. для нового SHA provider читает exact `FormatName → FileExtension` из Current v6;
4. missing/invalid mapping завершается fail-closed;
5. canonical extension участвует в первом content-addressed relative path;
6. first persisted Catalog address остаётся неизменным для duplicate SHA.

Низкоуровневые `ExternalPayloadAddressFactory.ForCustomBinary` и
`ExternalPayloadStore.StoreCustomBinaryAsync` также требуют explicit extension параметр; optional
`.bin` fallback отсутствует структурно.

Полный storage contract — `CUSTOM_BINARY_FORMAT_CONFIGURATION.md`.

## Отмена и владение

Creation token не является lifetime token возвращённого graph. Доступ после создания определяется
исходной protected session и caller token будущего `ProcessNextAsync`.

Lock/dispose/reopen не передаёт новый доступ старому graph: repositories, sink, provider и delivery
остаются привязаны к старой session. App generation guard дополнительно запрещает публикацию
stale composition result.

Ошибки composition не публикуют partial graph и не ослабляют protected access. Они не запускают
fallback capture path.

## Проверки и оставшийся этап

Storage composition tests покрывают unconfigured policy, Allow/Deny, durable identity,
individual overrides, cancellation/lock/dispose, malformed policy, mandatory extension provider,
operation-token lifetime и committed accepted-text path. Current v6 tests отдельно покрывают
extension repository/provider и migration.

App-level wiring компилируется как часть Windows x64 Build. Ручная WinUI/clipboard проверка пока
не выполнена.

**Worker lifecycle остаётся отдельным tranche.** Retained `ProtectedClipboardDeliveryServices`
сейчас никто не вызывает через `ProcessNextAsync`; capture queue не потребляется. Следующий этап
должен отдельно определить и реализовать start/stop, lock/unlock cancellation, stale-session rules,
close/dispose, queue ownership и worker failure/retry semantics. До этого end-to-end real clipboard
capture остаётся NOT READY.
