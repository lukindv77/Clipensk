# Protected clipboard delivery composition

## Назначение и граница этапа

`ProtectedClipboardDeliveryServices.TryCreateAsync` связывает persisted global policy,
индивидуальные policy overrides, durable application identity, Catalog-backed external resolver,
history sink и accepted capture delivery с одной активной `ProtectedStorageSessionLease`.
Это готовый composition boundary для будущего app host; `App` пока его не вызывает автоматически.
Он не запускает worker, не меняет listener lifecycle и не обрабатывает очередь при создании.

## Явные зависимости

Caller передаёт:

- активную protected storage session;
- `IClipboardAcceptedCaptureDeliveryFactory`;
- обязательный `IClipboardCustomBinaryFileExtensionProvider`;
- опциональную connection factory для уже существующего SQLCipher boundary;
- cancellation token конкретной операции создания.

`ResidentWindowsHost` реализует factory через существующий overload
`CreateAcceptedCaptureDeliveryPipeline(policyProvider, sink, identityRegistry)`.
Windows не получает зависимости от Storage. Factory interface находится в Core.
Сохраняются существующие source resolution, policy merge, single DataPackageView и reader routing.
WndProc, formats, size semantics и storage schema этим этапом не меняются.

Factory только строит graph: не читает clipboard/БД, не потребляет queue, не запускает задачи
и не приобретает ресурсы с отдельным teardown. Она не добавляет собственный session wrapper:
composition накладывает `ProtectedClipboardAcceptedCaptureDelivery` с той же session.

## Порядок создания

1. Проверить обязательные зависимости и действительность session; связать caller/session tokens.
2. Прочитать global policy из Current через `SqliteGlobalClipboardCapturePolicyRepository`.
3. Если policy отсутствует, вернуть `null`, не вызывая delivery factory и не создавая остальные services.
4. Для сохранённой policy, включая explicit Deny, собрать capture services и history services
   с одной session и connection factory.
5. Передать в factory именно provider/identity registry из capture services и sink из history services.
6. Повторно проверить отмену/активность и вернуть services с protected delivery wrapper.

Ошибка policy/schema/storage не превращается в `null`, Allow или Deny.
Global Deny не равен отсутствию настройки и не является безусловным override индивидуальных правил.
При создании открывается только Current ReadOnly для global policy. Catalog, external files,
identity/application-policy БД-операции и extension provider остаются lazy до обработки.

## Отмена и владение

Связанный token операции создания освобождается после её завершения. Его последующая отмена
не отзывает уже возвращённые services. Их доступ продолжает определяться исходной protected session
и caller token каждого `ProcessNextAsync`.

Отмена внутри factory не публикует services. После lock/dispose или повторного unlock старый
компонент не получает доступ новой сессии: его delivery, policy/identity repositories и sink
остаются привязаны к старой session. Creation не продлевает её lifecycle.

Factory-owned graph не имеет самостоятельного background lifecycle. После создания worker не
существует; будущий host должен отдельно управлять запуском, отменой, остановкой, queue lifecycle
и освобождением ссылок. Возвращение services не доказывает готовность всего capture runtime.

`TryCreateAsync` не назначает поток исполнения. SQLite здесь синхронный; UI host должен вынести
вызов с UI thread и проверить session/generation перед использованием результата.
После успешного COMMIT history sink/delivery не превращает успех в late cancellation.

## Проверки

Тесты используют production storage services с тестовой SQLite connection factory и inert
подменой Windows factory. Покрываются:

- unconfigured policy без factory/processing/extension calls;
- explicit Allow и Deny, exact сохранённые limits, единственное ReadOnly открытие при создании;
- реальные durable identity и индивидуальные policy overrides;
- cancellation/lock/dispose до открытия и отмена внутри factory;
- обязательный extension provider без скрытого fallback;
- malformed persisted policy как ошибка;
- раздельный lifetime creation token и operation tokens, запрет использования старой session;
- явная обработка accepted text через составленный SQL sink и успешный COMMIT перед lock.

Тест не имитирует настоящий Windows clipboard. Windows factory adapter компилируется в Build;
ручная WinUI/clipboard проверка и worker end-to-end evidence пока отсутствуют.

## Оставшиеся зависимости app host

App-level вызов composition требует явного источника расширений новых custom binary payload.
Он не подменён `.bin`, молчаливым default или отключением форматов. После определения этого
источника можно связать composition с unlock/первичной настройкой, затем реализовать полный
worker lifecycle. При отсутствии policy ничего не запускать. Настройка global policy сама по
себе всё ещё не включает журналирование.
