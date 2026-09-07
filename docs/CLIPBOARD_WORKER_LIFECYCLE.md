# Clipboard worker lifecycle

## Назначение

Resident clipboard runtime состоит из трёх отдельных частей:

1. Windows listener (`ClipboardUpdateMonitor`) только превращает `WM_CLIPBOARDUPDATE` в bounded queue request;
2. protected delivery composition строит session-bound `ProtectedClipboardDeliveryServices`;
3. `ClipboardAcceptedCaptureWorker` последовательно вызывает `Delivery.ProcessNextAsync`.

Worker не создаёт собственную storage session и не владеет MasterKey. Его lifetime ограничен той же `ProtectedStorageSessionLease`, на которой построен delivery graph.

## Gate запуска

Новый capture runtime может стартовать только когда одновременно истинны все условия:

- lifecycle разрешает protected data access;
- `JournalWindow` держит active `ProtectedStorageSessionLease`;
- composition относится к exact той же session;
- persisted global policy существует, поэтому `ProtectedClipboardDeliveryServices.TryCreateAsync` вернул non-null graph;
- App generation/window/host/lifecycle/session checks всё ещё совпадают.

Clipboard listener **не запускается при одном только unlock**. Сначала выполняется composition. Если global policy отсутствует или composition завершается ошибкой, listener остаётся выключенным и worker отсутствует.

После первой успешной инициализации global policy JournalWindow отправляет App post-COMMIT notification. App повторяет composition; только после non-null результата планирует worker и включает listener.

Так request, возникший до первичной настройки policy, не может остаться в queue и позже обработаться по только что сохранённой policy.

## Worker loop

`ClipboardAcceptedCaptureWorker` принимает один `IClipboardAcceptedCaptureDelivery` и выполняет один `ProcessNextAsync` за раз.

- параллельных вызовов delivery из одного worker нет;
- `false`/`true` result одного capture не завершает resident loop;
- cancellation текущей worker token является нормальным завершением;
- non-cancellation ошибка одного capture fail-closed отбрасывает этот request и worker продолжает ждать следующего.

Продолжение после item failure допустимо, потому что production pipeline сначала удаляет request из `ClipboardCaptureQueue` в `ClipboardCaptureSourceStage.ResolveNextAsync`, и только затем выполняет source resolution, identity/policy, format read и persistence. Повтор poisoned request в hot loop не происходит.

## Один reader между сессиями

`ClipboardCaptureQueue` создан как single-reader channel. App поэтому сериализует worker generations:

- retained App state содержит предыдущий worker `Task`;
- worker новой protected session сначала ждёт завершения предыдущего task;
- только после этого повторно проверяет generation и exact session identity и становится новым reader;
- stale generation после lock/reopen не начинает dequeue.

App не создаёт отдельный lifetime CTS для worker. Используется `ProtectedStorageSessionLease.CancellationToken`, который отменяется при revoke protected access и dispose session.

## Listener и capture epoch

`ClipboardUpdateMonitor.Start()` открывает новый capture epoch. `Stop()` сначала прекращает принимать updates и инвалидирует epoch, затем снимает Windows listener.

При lock App:

1. останавливает monitoring, поэтому новые updates не принимаются и текущий epoch invalidated;
2. инвалидирует worker generation;
3. инвалидирует retained composition;
4. protected lifecycle отменяет session token, из-за чего blocked dequeue/processing worker завершается.

Даже если Win32 remove-listener вызов завершится ошибкой, monitor уже устанавливает `_acceptUpdates = false` и инвалидирует epoch до этой операции; stale Windows callback не принимается в queue.

При следующем unlock новая session не наследует requests старого capture epoch.

## Close и reopen

При закрытии JournalWindow его protected session освобождается, что отменяет worker token и отзывает MasterKey. App дополнительно останавливает monitoring, invalidates worker/composition generations и освобождает Windows host.

Повторный unlock создаёт новую `ProtectedStorageSessionLease` и требует нового composition. Старые services не получают доступ новой session.

## Persistence и cancellation

Worker не меняет существующие durable semantics:

- external address resolution завершается до history SQL transaction;
- cancellation проверяется до COMMIT;
- успешный COMMIT остаётся успехом и не демотируется late cancellation;
- ошибка одного capture не создаёт journal record для отклонённого/неполного payload;
- новый custom-binary SHA без exact Current v6 extension mapping fail-closed.

## Проверки

Core worker tests покрывают:

- cancellation blocked delivery;
- продолжение после non-cancellation item failure;
- отсутствие параллельных `ProcessNextAsync` вызовов.

Windows Build компилирует App lifecycle wiring. Полный manual WinUI/real-clipboard smoke остаётся отдельным evidence: автоматический unit test не эмулирует настоящий foreground application, `WM_CLIPBOARDUPDATE` и WinRT `DataPackageView`.
