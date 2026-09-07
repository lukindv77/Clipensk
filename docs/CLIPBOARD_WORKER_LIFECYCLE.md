# Clipboard worker lifecycle

## Назначение

Resident clipboard runtime состоит из трёх отдельных частей:

1. Windows listener (`ClipboardUpdateMonitor`) только превращает `WM_CLIPBOARDUPDATE` в bounded queue request;
2. protected delivery composition строит session-bound `ProtectedClipboardDeliveryServices`;
3. `ClipboardAcceptedCaptureWorker` последовательно вызывает `Delivery.ProcessNextAsync`.

Worker не создаёт собственную storage session и не владеет MasterKey. Его lifetime ограничен той же `ProtectedStorageSessionLease`, на которой построен delivery graph, и дополнительной App-owned cancellation для конкретной worker generation.

## Gate запуска

Новый capture runtime может стартовать только когда одновременно истинны все условия:

- lifecycle разрешает protected data access;
- `JournalWindow` держит active `ProtectedStorageSessionLease`;
- composition относится к exact той же session;
- persisted global policy существует, поэтому `ProtectedClipboardDeliveryServices.TryCreateAsync` вернул non-null graph;
- App generation/window/host/lifecycle/session checks всё ещё совпадают.

Clipboard listener **не запускается при одном только unlock**. Сначала выполняется composition. Если global policy отсутствует или composition завершается ошибкой, listener остаётся выключенным и worker отсутствует.

После успешной durable policy mutation, которая требует пересборки runtime, JournalWindow отправляет App отдельный refresh request после того, как durable операция уже достигла результата. Для `InitializeAsync` это происходит после COMMIT. Non-null composition только планирует exact-session worker; listener включается позже, когда эта generation уже дождалась предыдущего worker и стала единственным queue reader.

Так request, возникший до настройки policy, во время policy reset либо пока новый worker ещё ждёт старого reader, не может остаться в queue и позже получить source-application metadata слишком поздно.

## Worker loop

`ClipboardAcceptedCaptureWorker` принимает один `IClipboardAcceptedCaptureDelivery` и выполняет один `ProcessNextAsync` за раз.

- параллельных вызовов delivery из одного worker нет;
- `false`/`true` result одного capture не завершает resident loop;
- cancellation текущей worker token является нормальным завершением;
- non-cancellation ошибка одного production capture fail-closed отбрасывает этот request и worker продолжает ждать следующего.

Продолжение после item failure допустимо для production graph, потому что protected wrapper до inner pipeline выполняет только cancellation gates, а сам pipeline сначала удаляет request из `ClipboardCaptureQueue` в `ClipboardCaptureSourceStage.ResolveNextAsync` и только затем выполняет source resolution, identity/policy, format read и persistence. Повтор poisoned request в hot loop не происходит.

## Один reader между сессиями и runtime generations

`ClipboardCaptureQueue` создан как single-reader channel. App поэтому сериализует worker generations:

- retained App state содержит предыдущий worker `Task`;
- worker новой protected session или новой runtime generation сначала ждёт завершения предыдущего task;
- только после этого повторно проверяет generation и exact session identity и становится новым reader;
- stale generation после lock/reopen/policy replacement не начинает dequeue.

Для каждой worker generation App создаёт отдельный `CancellationTokenSource`, linked с `ProtectedStorageSessionLease.CancellationToken`. Поэтому worker прекращается при любом из двух событий:

- revoke/dispose protected session;
- явный `InvalidateClipboardWorker()` при runtime replacement, policy cleanup, lock или close.

При replacement App сначала публикует новую generation/state, затем best-effort отменяет previous CTS. Новый worker всё равно ждёт previous task, поэтому одновременно два queue reader не появляются. CTS принадлежит своему worker task и освобождается в его `finally`; race между completion и App cancellation допускается и обрабатывается без нарушения lock/close path.

## Listener и capture epoch

`ClipboardUpdateMonitor.Start()` открывает новый capture epoch. `Stop()` сначала прекращает принимать updates и инвалидирует epoch, затем снимает Windows listener.

При lock App:

1. останавливает monitoring, поэтому новые updates не принимаются и текущий epoch invalidated;
2. инвалидирует worker generation и явно отменяет App-owned worker CTS;
3. инвалидирует retained composition;
4. protected lifecycle также отменяет session token.

Даже если Win32 remove-listener вызов завершится ошибкой, monitor уже устанавливает `_acceptUpdates = false` и инвалидирует epoch до этой операции; stale Windows callback не принимается в queue.

При следующем unlock новая session не наследует requests старого capture epoch.

Listener запускается на window dispatcher только **после** того, как worker дождался previous task и прошёл exact generation/session checks. Worker уже может блокироваться в `DequeueAsync`, когда dispatcher включает listener, поэтому первый новый request сразу имеет готового single reader. Перед и после Win32 `Start()` App повторно проверяет window/host/lifecycle/session ownership; дополнительно dispatcher callback проверяет exact worker generation/CTS. Если lock/close/replacement выиграл race, monitoring не запускается либо сразу отзывается.

Если dispatcher больше не принимает callback, listener остаётся выключенным; worker затем завершается по App/session cancellation. Это fail-closed, а не fallback capture path.

## Policy cleanup и runtime replacement

Configured global policy нельзя перезаписать in-place. Для последующего изменения UI выполняет отдельный cleanup, возвращающий storage в состояние `unconfigured`.

Cleanup имеет pre-mutation runtime gate:

1. после явного пользовательского confirmation JournalWindow синхронно посылает `GlobalCapturePolicyMutationStarting`;
2. App останавливает listener, тем самым инвалидируя текущий capture epoch;
3. App invalidates worker generation и явно отменяет App-owned worker CTS;
4. App invalidates retained composition, чтобы stale graph не мог быть опубликован повторно;
5. только после этого JournalWindow запускает `SqliteGlobalClipboardCapturePolicyRepository.CleanupAsync` вне UI thread.

`CleanupAsync` использует Current ReadWrite + immediate transaction, валидирует полную persisted policy до удаления и удаляет singleton header; format rows исчезают через проверенный `ON DELETE CASCADE`. Ошибка либо cancellation до COMMIT оставляет старую policy целиком. После успешного COMMIT policy отсутствует и старые значения не превращаются в defaults для нового setup.

После любого результата cleanup JournalWindow best-effort отправляет `GlobalCapturePolicyRefreshRequested`. App заново compose-ит **фактически persisted** state:

- committed cleanup → `TryCreateAsync` возвращает `null`; listener/worker остаются выключенными до нового explicit `InitializeAsync`;
- rollback/ошибка cleanup → прежняя policy остаётся в Current и может снова создать non-null graph;
- ошибка post-operation refresh не меняет durable результат cleanup и оставляет runtime fail-closed до следующего composition trigger.

In-flight capture не получает специального исключения из общих persistence semantics. Pre-mutation cancellation старается остановить его до следующего cancellation gate; если history COMMIT уже завершился, запись остаётся успешной. Cleanup policy не удаляет существующую history.

Если lock/reopen выигрывает race, UI generation/session guards запрещают stale confirmation/result изменять новую session. Новый worker по-прежнему ждёт завершения previous task перед dequeue.

## Close и reopen

При закрытии JournalWindow его protected session освобождается, что отменяет session token и отзывает MasterKey. App дополнительно останавливает monitoring, explicitly отменяет worker generation, invalidates composition и освобождает Windows host.

Повторный unlock создаёт новую `ProtectedStorageSessionLease` и требует нового composition. Старые services не получают доступ новой session.

## Persistence и cancellation

Worker не меняет существующие durable semantics:

- external address resolution завершается до history SQL transaction;
- cancellation проверяется до COMMIT;
- успешный COMMIT остаётся успехом и не демотируется late cancellation;
- ошибка одного capture не создаёт journal record для отклонённого/неполного payload;
- новый custom-binary SHA без exact Current v6 extension mapping fail-closed.

Policy cleanup следует тому же правилу COMMIT: cancellation до cleanup COMMIT откатывает удаление, а уже committed cleanup не демотируется поздней отменой.

## Проверки

Core worker tests покрывают:

- cancellation blocked delivery;
- продолжение после non-cancellation item failure;
- отсутствие параллельных `ProcessNextAsync` вызовов.

Storage policy tests отдельно покрывают cleanup success/no-op, cascade, malformed state, cancellation и rollback. Windows Build компилирует App lifecycle wiring и reset UI. Полный manual WinUI/real-clipboard smoke остаётся отдельным evidence: автоматический unit test не эмулирует настоящий foreground application, `WM_CLIPBOARDUPDATE` и WinRT `DataPackageView`.