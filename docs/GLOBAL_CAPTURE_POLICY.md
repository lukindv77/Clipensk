# Глобальная capture policy — введена в Current v5

Latest Current schema — **v6**. Global capture policy остаётся тем же storage-scoped контрактом, введённым в v5; v6 добавляет отдельно custom-binary file-extension configuration и не меняет семантику policy.

## Принятый контракт

Глобальная policy принадлежит выбранному хранилищу и сохраняется в зашифрованной
`Current/current.db`. Она не является общей настройкой процесса в JSON и не хранится
только в rebuildable Catalog. Индивидуальные overrides остаются привязаны к `ApplicationId`.

При создании или миграции хранилища policy **не настроена**: обе policy-таблицы пусты.
Отсутствие policy не превращается в `Allow`, `Deny`, пустую разрешающую policy или значения
форматов/лимитов по умолчанию. Пользователь должен явно выполнить первичную настройку.
До этого чтение истории после unlock допустимо, но обработка новых clipboard payload
не должна запускаться. Само сохранение policy не запускает worker напрямую: post-COMMIT
уведомление инициирует повторную composition, и runtime стартует только если persisted policy
успешно прочитана обратно через composition boundary.

Существующая policy не перезаписывается in-place. Для изменения пользователь сначала выполняет
явный cleanup, который удаляет persisted global policy и возвращает хранилище в состояние
`unconfigured`. После этого новая policy снова создаётся только через обычный first-write
`InitializeAsync`. Cleanup не удаляет уже сохранённую clipboard history и не меняет
application overrides, custom-binary mappings либо Catalog.

## Schema

Current v5 добавила:

| Таблица | Данные и ограничения |
|---|---|
| `GlobalCapturePolicy` | `SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (=1)`; `CaptureRule TEXT NOT NULL`, только exact `Allow` / `Deny` |
| `GlobalFormatCapturePolicy` | `SingletonId INTEGER NOT NULL CHECK (=1)`; `FormatName TEXT NOT NULL`; `CaptureRule TEXT NOT NULL`; nullable положительный `MaxBytes`; PK `(SingletonId, FormatName)`; FK к global header с `ON DELETE CASCADE` |

Global rules не имеют родительской policy: `Inherit` и неизвестные enum значения не
принимаются при первичной настройке. Для индивидуальных overrides `Inherit` сохраняет
существующую семантику. Имена форматов сравниваются ordinal/BINARY, без нормализации.
Repository дополнительно отклоняет пустые/whitespace имена и нецелые размеры.
`MaxBytes = null` сохраняет отсутствие заданного лимита; численные лимиты не выбираются.
Размер измеряется по `CLIPBOARD_CAPTURE_SIZE_LIMITS.md`.

В таблицы попадают только переданные caller rules. Для неуказанного формата repository
ничего не добавляет. Selector читает формат только при итоговых `Capture = Allow` и явном
`Formats[name].Capture = Allow` после merge. Global `Deny` является базовым правилом
наследования; application override может заменить его. Это не безусловный kill switch.

## Repository и границы операций

`IGlobalClipboardCapturePolicyRepository` реализован в
`SqliteGlobalClipboardCapturePolicyRepository`:

- `ReadAsync(token)` возвращает immutable policy либо `null` для не настроенного хранилища;
- `InitializeAsync(policy, token)` атомарно сохраняет **первую** policy;
- любая повторная инициализация, в том числе тем же значением, завершается ошибкой;
- `CleanupAsync(token)` атомарно удаляет существующую policy и возвращает `true`, если она
  существовала, либо `false` для уже unconfigured storage;
- update/rebind API отсутствует: изменение policy всегда проходит через отдельное состояние
  `unconfigured`, после которого требуется новый explicit `InitializeAsync`.

Constructor не открывает БД. Read использует ReadOnly и один SELECT/snapshot для header
и всех formats, включая проверку orphan rows. Initialize использует ReadWrite, immediate
transaction, проверку отсутствия policy и INSERT header + formats без upsert.
Конкурирующие первичные записи сериализуются SQLite; перезаписи победившей policy нет.

Cleanup также использует ReadWrite + immediate transaction. Перед удалением repository
читает и валидирует **полную** persisted policy тем же snapshot boundary, поэтому malformed
policy или orphan rows не превращаются в разрешение «починить» состояние молчаливым delete.
Для корректной policy удаляется только singleton row `GlobalCapturePolicy`; format rows
удаляются проверенным `ON DELETE CASCADE`. Если policy уже отсутствует, операция является
линеаризованным no-op. Ошибка SQL либо cancellation до COMMIT откатывает cleanup целиком.

Операции привязаны к `ProtectedStorageSessionLease`, связывают caller/session cancellation,
проверяют отмену до/после открытия, при чтении rows и перед COMMIT. Ошибка или отмена до
COMMIT откатывает всю запись или cleanup. После успешного COMMIT нет late-cancellation проверки,
превращающей уже сохранённую/удалённую policy в ошибку отмены. Connections/readers
освобождаются. SQLite calls синхронны; preemptive interruption отдельного SQL-вызова не обещается.

Repository принимает Current **v5 и более позднюю совместимую схему**. Проверяются
storage identity/Current role, `user_version`, table/PK/FK shape и persisted rules.
Некорректная policy не трактуется как отсутствие настройки. Repository не создаёт и не
мигрирует schema. Возвращённые policy snapshots принадлежат caller; repository их не
кэширует и не продлевает session.

## Миграция и latest schema

`ProtectedStorageDatabaseService` создаёт новую пару как **Current v6 / Catalog v2**.
Global-policy tables по-прежнему появляются отдельным durable шагом `Current v4 → v5`.
После него отдельный `v5 → v6` создаёт пустую `CustomBinaryFormatConfiguration`, не
переписывая global policy. Подробности v6 — в `CUSTOM_BINARY_FORMAT_CONFIGURATION.md` и
`CURRENT_DATABASE_SCHEMA.md`.

Для существующей пары обе БД полностью проверяются до mutation Current. Не используются
`IF NOT EXISTS`, seed или перенос rules из JSON. History, application identity,
индивидуальные policies и persisted external addresses не переписываются.
Ошибка/отмена шага v4→v5 сохраняет полноценный v4; ошибка/отмена v5→v6 сохраняет
полноценный v5. Повторное открытие может безопасно продолжить migration.

## Проверки

Тесты global policy покрывают отсутствие defaults, exact round-trip через новую session,
изоляцию хранилищ, explicit Deny, повторную настройку, неверные rules/schema/data, orphan
rows, отмену/lock/dispose, освобождение connection, rollback записи и повтор после сбоя.
Cleanup tests дополнительно подтверждают cascade удаления, повторную explicit initialization,
линеаризованный no-op для unconfigured storage, rollback при DELETE failure/cancellation и
отказ cleanup для malformed/schema-invalid state. Migration tests подтверждают сохранность
policy при продвижении Current до v6.

## Настройка и cleanup в JournalWindow

Раздел «Приложения и правила сбора» содержит explicit setup, read-only сводку сохранённых
правил и отдельное действие сброса. После создания active protected session выполняется чтение
policy, которое различает отсутствие настройки, сохранённые правила и ошибку чтения.
Отсутствие настройки не блокирует доступ к журналу, но capture listener и worker остаются
выключенными.

UI предлагает только поддерживаемые стандартные formats через exact Windows
`StandardDataFormats`: Text, Html, Rtf, Bitmap, WebLink, ApplicationLink, StorageItems.
Набор элементов редактора не является defaults: общий и все format selectors первоначально
не выбраны. Каждый формат требует явного Allow/Deny, в том числе при global Deny.
Для разрешённого формата обязательно выбрать положительный Int64 limit в байтах либо явно
«Без лимита». Для запрещённого формата `MaxBytes` не задаётся.

Для configured policy UI не показывает in-place editor. Пользователь может только посмотреть
сводку либо выбрать «Сбросить сохранённые правила». Cleanup требует отдельного confirmation.
После успешного cleanup UI возвращается к пустому setup editor; никакие прежние rule/limit
значения не подставляются как defaults.

Custom-format UI всё ещё отсутствует. При этом durable extension contract для custom binary
уже существует в Current v6: exact `FormatName → FileExtension` хранится в
`CustomBinaryFormatConfiguration`, а `RepositoryClipboardCustomBinaryFileExtensionProvider`
предоставляет fail-closed production provider. UI global policy этот mapping пока не создаёт
и не добавляет custom formats автоматически.

SQLite read/write выполняются вне UI thread с session cancellation и generation checks.
Lock/close инвалидирует stale results и очищает protected UI state.

## Composition, worker и cleanup lifecycle

Persisted policy подключена к `ProtectedClipboardDeliveryServices.TryCreateAsync`: boundary
собирает capture/history services и protected delivery через явную factory, а для отсутствующей
policy возвращает `null`. Контракты: `PROTECTED_CLIPBOARD_DELIVERY_COMPOSITION.md` и
`CLIPBOARD_WORKER_LIFECYCLE.md`.

App вызывает composition после появления active protected session. Вызов выполняется вне UI
thread; результат принимается только при совпадающей generation, lifecycle/window/host references
и той же active session.

Если policy отсутствует на unlock, App получает `null`: listener остаётся выключенным, worker не
создаётся и clipboard updates до настройки не попадают в capture queue. После успешного
`InitializeAsync` JournalWindow уведомляет App только **после COMMIT**; App повторно compose-ит
runtime. Ошибка уведомления не превращает committed policy в ошибку сохранения.

Cleanup имеет дополнительный pre-mutation gate. После confirmation JournalWindow синхронно
уведомляет App **до открытия write transaction**. App прекращает clipboard monitoring,
инвалидирует capture epoch, worker generation и retained composition. Только после этого UI
запускает `CleanupAsync` вне UI thread. Worker CTS отменяет blocked/active delivery; существующие
pipeline cancellation gates остаются ответственны за отмену до history COMMIT, а уже завершённый
COMMIT не демотируется задним числом.

После любого результата cleanup JournalWindow best-effort запрашивает новую composition из
persisted state. Если cleanup committed, `ReadAsync` возвращает `null`, поэтому runtime остаётся
выключенным до нового explicit setup. Если cleanup откатился/завершился ошибкой, старая policy
остаётся в Current и может быть заново compose-нута. Durable cleanup result не зависит от
успешности этого post-operation runtime refresh.

Для non-null composition App планирует single-reader `ClipboardAcceptedCaptureWorker` на exact
session и только затем запускает Windows listener. Lock сначала останавливает monitoring и
инвалидирует capture epoch, затем invalidates worker/composition; session cancellation завершает
blocked или active worker. Worker новой session ждёт завершения предыдущего task перед dequeue.

После CI этот lifecycle делает automatic clipboard capture runtime связанным от listener до
history sink. Ручной WinUI/real-clipboard smoke всё ещё требует отдельной проверки и не считается
подтверждённым unit/CI тестами. Конкретные format/size defaults по-прежнему не назначены.