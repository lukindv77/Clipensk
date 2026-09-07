# Protected clipboard delivery composition

## Назначение и граница этапа

`ProtectedClipboardDeliveryServices.TryCreateAsync` связывает persisted global policy,
индивидуальные policy overrides, durable application identity, Catalog-backed external resolver,
history sink и accepted capture delivery с одной активной `ProtectedStorageSessionLease`.
Это готовый composition boundary для app host; `App` пока его не вызывает автоматически.
Он не запускает worker, не меняет listener lifecycle и не обрабатывает очередь при создании.

Latest storage contract — Current v6 / Catalog v2. Current v6 добавляет storage-scoped
custom-binary extension configuration, но сам composition boundary по-прежнему принимает
extension provider как явную dependency.

## Явные зависимости

Caller передаёт:

- активную protected storage session;
- `IClipboardAcceptedCaptureDeliveryFactory`;
- обязательный `IClipboardCustomBinaryFileExtensionProvider`;
- опциональную connection factory для существующего SQLCipher boundary;
- cancellation token конкретной операции создания.

Production storage теперь предоставляет готовую связку:

- `SqliteCustomBinaryFormatConfigurationRepository` — exact `FormatName → FileExtension` в Current v6;
- `RepositoryClipboardCustomBinaryFileExtensionProvider` — адаптер repository к
  `IClipboardCustomBinaryFileExtensionProvider`.

App host должен создавать repository/provider из **той же active session**, которая передаётся
в `TryCreateAsync`. Скрытого `.bin` fallback нет: если новый custom-binary SHA требует расширение,
а exact mapping отсутствует, processing завершается fail-closed.

`ResidentWindowsHost` реализует delivery factory через существующий overload
`CreateAcceptedCaptureDeliveryPipeline(policyProvider, sink, identityRegistry)`.
Windows не получает зависимость от Storage. Factory interface находится в Core.
Сохраняются existing source resolution, policy merge, single `DataPackageView` и reader routing.

Factory только строит graph: не читает clipboard/БД, не потребляет queue, не запускает задачи
и не приобретает ресурсы с отдельным teardown. Она не добавляет собственный session wrapper:
composition накладывает `ProtectedClipboardAcceptedCaptureDelivery` с той же session.

## Порядок создания

1. Проверить обязательные зависимости и действительность session; связать caller/session tokens.
2. Прочитать global policy из Current через `SqliteGlobalClipboardCapturePolicyRepository`.
3. Если policy отсутствует, вернуть `null`, не вызывая delivery factory и не создавая остальные services.
4. Для сохранённой policy, включая explicit Deny, собрать capture services и history services
   с одной session и connection factory.
5. Передать в factory policy provider/identity registry из capture services и sink из history services.
6. Повторно проверить отмену/активность и вернуть services с protected delivery wrapper.

Ошибка policy/schema/storage не превращается в `null`, Allow или Deny. Global Deny не равен
отсутствию настройки и не является безусловным override индивидуальных правил.

При создании открывается только Current ReadOnly для global policy. Catalog, external files,
identity/application-policy операции и custom-binary extension lookup остаются lazy до фактической
обработки. Поэтому наличие provider не означает чтение `CustomBinaryFormatConfiguration` при
самом `TryCreateAsync`.

## Отмена и владение

Связанный token операции создания освобождается после её завершения. Его последующая отмена
не отзывает уже возвращённые services. Доступ к protected data продолжает определяться исходной
session и caller token каждого `ProcessNextAsync`.

Отмена внутри factory не публикует services. После lock/dispose или повторного unlock старый
компонент не получает доступ новой сессии: delivery, repositories, sink и extension provider
остаются привязаны к старой session. Creation не продлевает lifecycle.

Factory-owned graph не имеет самостоятельного background lifecycle. После создания worker не
существует; app host должен отдельно управлять запуском, отменой, остановкой, queue lifecycle
и освобождением ссылок. Возвращение services не доказывает готовность всего capture runtime.

`TryCreateAsync` не назначает поток исполнения. SQLite здесь синхронный; UI host должен вынести
вызов с UI thread и проверить session/generation перед использованием результата.
После успешного COMMIT history sink/delivery не превращает успех в late cancellation.

## Custom-binary extension semantics

Для нового custom binary payload resolver выполняет следующий порядок:

1. сначала exact SHA lookup в Catalog v2;
2. если persisted address уже существует, он используется без extension provider;
3. для нового SHA provider читает exact mapping из Current v6;
4. missing/invalid mapping — ошибка, а не default;
5. canonical extension участвует только в первом content-addressed relative path;
6. после записи Catalog фиксирует first persisted address; последующая смена mapping не
   переименовывает existing SHA и поэтому rebind без cleanup запрещён.

Полный contract — `CUSTOM_BINARY_FORMAT_CONFIGURATION.md`.

## Проверки

Composition tests используют production storage services с тестовой SQLite connection factory
и inert Windows factory. Покрываются:

- unconfigured policy без factory/processing/extension calls;
- explicit Allow и Deny, exact persisted limits, единственное ReadOnly открытие при создании;
- durable identity и индивидуальные policy overrides;
- cancellation/lock/dispose до открытия и отмена внутри factory;
- обязательный extension provider без скрытого fallback;
- malformed persisted policy как ошибка;
- раздельный lifetime creation token и operation tokens;
- запрет использования старой session;
- явная обработка accepted text через SQL sink и успешный COMMIT перед lock.

Отдельные Current v6 tests покрывают repository/provider для custom extensions, v5→v6 migration,
fail-closed missing mapping, rollback/cancellation и сохранность global policy.

Тесты не имитируют настоящий Windows clipboard. Windows factory adapter компилируется в Build;
ручная WinUI/clipboard проверка и worker end-to-end evidence пока отсутствуют.

## Оставшиеся зависимости app host

Источник custom-binary extensions **реализован**: App больше не должен изобретать mapping или
передавать hidden `.bin`. Следующий app-level tranche должен после успешного unlock/initial setup:

1. использовать текущую active `ProtectedStorageSessionLease`;
2. создать `SqliteCustomBinaryFormatConfigurationRepository` и
   `RepositoryClipboardCustomBinaryFileExtensionProvider` на этой session;
3. вызвать `ProtectedClipboardDeliveryServices.TryCreateAsync` вне UI thread;
4. принять результат только при совпадающей generation и всё ещё active session;
5. при `null` из-за отсутствующей global policy ничего не запускать.

Само подключение этого composition к `App` ещё не выполнено. После него отдельным tranche
остаётся полный worker lifecycle: start/stop, lock/unlock, cancellation, stale session, close,
queue ownership и COMMIT semantics. Настройка global policy сама по себе журналирование не включает.
