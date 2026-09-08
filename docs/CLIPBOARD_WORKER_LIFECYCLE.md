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
- App generation/window/host/lifecycle/session checks всё ещё совпадают;
- App runtime не находится в maintenance suspension.

Clipboard listener **не запускается при одном только unlock**. Сначала выполняется composition. Если global policy отсутствует, composition завершается ошибкой либо runtime suspended, listener остаётся выключенным и новый worker не публикуется.

После первой успешной инициализации global policy JournalWindow отправляет App post-COMMIT notification. App повторяет composition. Non-null result только планирует exact-session worker; listener включается позже, когда эта generation уже дождалась предыдущего worker и стала единственным queue reader.

Так request, возникший до первичной настройки policy или пока новый worker ещё ждёт старого reader, не может остаться в queue и позже получить source-application metadata слишком поздно.

## Worker loop

`ClipboardAcceptedCaptureWorker` принимает один `IClipboardAcceptedCaptureDelivery` и выполняет один `ProcessNextAsync` за раз.

- параллельных вызовов delivery из одного worker нет;
- `false`/`true` result одного capture не завершает resident loop;
- cancellation текущей worker token является нормальным завершением;
- non-cancellation ошибка одного production capture fail-closed отбрасывает этот request и worker продолжает ждать следующего.

Продолжение после item failure допустимо для production graph, потому что protected wrapper до inner pipeline выполняет только cancellation gates, а сам pipeline сначала удаляет request из `ClipboardCaptureQueue` в `ClipboardCaptureSourceStage.ResolveNextAsync` и только затем выполняет source resolution, identity/policy, format read и persistence. Повтор poisoned request в hot loop не происходит.

## Один reader между сессиями

`ClipboardCaptureQueue` создан как single-reader channel. App поэтому сериализует worker generations:

- retained App state содержит предыдущий worker `Task`;
- worker новой protected session сначала ждёт завершения предыдущего task;
- только после этого повторно проверяет generation и exact session identity и становится новым reader;
- stale generation после lock/reopen не начинает dequeue.

Для каждой worker generation App создаёт отдельный `CancellationTokenSource`, linked с `ProtectedStorageSessionLease.CancellationToken`. Поэтому worker прекращается при любом из двух событий:

- revoke/dispose protected session;
- явный `InvalidateClipboardWorker()` при runtime replacement, lock, close или maintenance quiescence.

При replacement App сначала публикует новую generation/state, затем best-effort отменяет previous CTS. Новый worker всё равно ждёт previous task, поэтому одновременно два queue reader не появляются. CTS принадлежит своему worker task и освобождается в его `finally`; race между completion и App cancellation допускается и обрабатывается без нарушения lock/close path.

## Listener и capture epoch

`ClipboardUpdateMonitor.Start()` открывает новый capture epoch. `Stop()` сначала прекращает принимать updates и инвалидирует epoch, затем снимает Windows listener.

При lock App:

1. останавливает monitoring, поэтому новые updates не принимаются и текущий epoch invalidated;
2. инвалидирует worker generation и явно отменяет App-owned worker CTS;
3. инвалидирует retained composition;
4. сбрасывает только in-memory maintenance suspension, потому что следующая unlock создаёт новую protected session;
5. protected lifecycle также отменяет session token.

Будущий durable pending-maintenance marker обязан отдельно блокировать composition после reopen; in-memory reset при lock не является восстановлением незавершённой maintenance operation.

Даже если Win32 remove-listener вызов завершится ошибкой, monitor уже устанавливает `_acceptUpdates = false` и инвалидирует epoch до этой операции; stale Windows callback не принимается в queue.

При следующем unlock новая session не наследует requests старого capture epoch.

Listener запускается на window dispatcher только **после** того, как worker дождался previous task и прошёл exact generation/session checks. Worker уже может блокироваться в `DequeueAsync`, когда dispatcher включает listener, поэтому первый новый request сразу имеет готового single reader. Перед и после Win32 `Start()` App повторно проверяет window/host/lifecycle/session ownership и maintenance suspension; дополнительно dispatcher callback проверяет exact worker generation/CTS. Если lock/close/replacement/suspension выиграл race, monitoring не запускается либо сразу отзывается.

Если dispatcher больше не принимает callback, listener остаётся выключенным; worker затем завершается по App/session cancellation. Это fail-closed, а не fallback capture path.

## Maintenance quiescence foundation

App теперь имеет отдельный awaitable boundary `TryQuiesceClipboardRuntimeAsync` для будущей destructive maintenance, прежде всего policy cleanup/mutation.

Quiescence выполняется только для exact current window/host/lifecycle/session и использует уникальный non-zero suspension owner token:

1. caller cancellation проверяется до захвата suspension;
2. suspension owner атомарно публикуется только если runtime ещё не suspended;
3. повторно проверяется exact protected session ownership;
4. composition generation инвалидируется, поэтому уже начатый stale composition не может опубликоваться;
5. listener останавливается и текущий capture epoch инвалидируется;
6. worker generation инвалидируется, exact worker CTS отменяется;
7. App **обязательно ждёт завершения exact предыдущего worker task** до возврата owner token.

После publication suspension участвует во всех runtime readiness checks: новый composition request, worker start и dispatcher listener-start не проходят gate. Request, который успел попасть в старый epoch до `Stop`, позже отбрасывается queue epoch validation.

Worker cancellation проходит через protected delivery к history sink. Sink имеет последний cancellation boundary непосредственно перед Current SQL COMMIT. Поэтому после worker drain возможны только два безопасных состояния уже dequeued capture: либо он был отменён до COMMIT, либо COMMIT уже успел durable завершиться и будущая cleanup увидит эту запись. Quiescence не предполагает rollback уже successful COMMIT.

Suspension имеет **exact owner-token semantics**, а не boolean lock. Это защищает ABA race `old session suspended → lock resets → new session acquires suspension → stale old caller releases`: stale token не совпадает с новым owner и не может снять новую suspension.

Если caller cancellation приходит уже после owner acquisition, quiescence всё равно сначала полностью drain'ит worker. Поскольку destructive maintenance к этому моменту ещё не началась, exact owner затем освобождается и для той же всё ещё active session запрашивается fresh composition; после этого cancellation снова выбрасывается caller'у.

Successful quiesce возвращает owner token. После этого **никакого auto-resume нет**: future maintenance failure/cancellation может безопасно оставить capture suspended. `TryResumeClipboardRuntimeAfterMaintenance` освобождает только exact owner token и, если original protected session всё ещё current, запускает новое чтение persisted policy через fresh composition. Старый delivery graph не переиспользуется.

Window close не снимает maintenance suspension и не пытается resume runtime. Lock/revoke сбрасывает in-memory owner только для будущей новой session; durable recovery semantics должны обеспечиваться storage marker отдельно.

Текущий tranche создаёт только runtime quiescence/resume foundation. Он **не** меняет global/application policy, history rows, Current/Archive/Catalog schema и не запускает maintenance UI.

## Close и reopen

При закрытии JournalWindow его protected session освобождается, что отменяет session token и отзывает MasterKey. App дополнительно останавливает monitoring, explicitly отменяет worker generation, invalidates composition и освобождает Windows host. Maintenance suspension при close не превращается в resume path.

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

Feature Windows Build компилирует maintenance-aware App lifecycle wiring. Отдельного App unit-test harness для dispatcher/listener integration пока нет.

Полный manual WinUI/real-clipboard smoke остаётся отдельным evidence: автоматический test suite не эмулирует настоящий foreground application, `WM_CLIPBOARDUPDATE`, WinRT `DataPackageView`, quiesce во время активного clipboard read и lock/reopen dispatcher races.
