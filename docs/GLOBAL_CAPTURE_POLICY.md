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
не должна запускаться. Само сохранение policy не запускает worker.

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
- update/delete API отсутствуют: отключение форматов требует отдельного policy cleanup
  согласно REQUIREMENTS §18, его нельзя заменить простой перезаписью rules.

Constructor не открывает БД. Read использует ReadOnly и один SELECT/snapshot для header
и всех formats, включая проверку orphan rows. Запись использует ReadWrite, immediate
transaction, проверку отсутствия policy и INSERT header + formats без upsert.
Конкурирующие первичные записи сериализуются SQLite; перезаписи победившей policy нет.

Операции привязаны к `ProtectedStorageSessionLease`, связывают caller/session cancellation,
проверяют отмену до/после открытия, при чтении rows и перед COMMIT. Ошибка или отмена до
COMMIT откатывает всю запись. После успешного COMMIT нет late-cancellation проверки,
превращающей сохранённую policy в ошибку отмены. Connections/readers освобождаются.
SQLite calls синхронны; preemptive interruption отдельного SQL-вызова не обещается.

Repository принимает Current **v5 и более позднюю совместимую схему**. Проверяются
storage identity/Current role, `user_version`, table/PK/FK shape и persisted rules.
Некорректная policy не трактуется как отсутствие настройки. Repository не создаёт и не
мигрирует schema. Возвращённые policy snapshots принадлежат caller; repository их не
кэширует и не продлевает session.

## Миграция и latest schema

`ProtectedStorageDatabaseService` теперь создаёт новую пару как **Current v6 / Catalog v2**.
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
Migration tests дополнительно подтверждают сохранность policy при продвижении Current до v6.

## Первичная настройка в JournalWindow

Раздел «Приложения и правила сбора» содержит первичную настройку и read-only сводку
сохранённых правил. После создания active protected session выполняется чтение policy,
которое различает отсутствие настройки, сохранённые правила и ошибку чтения. Отсутствие
настройки не блокирует доступ к журналу.

UI предлагает только поддерживаемые стандартные formats через exact Windows
`StandardDataFormats`: Text, Html, Rtf, Bitmap, WebLink, ApplicationLink, StorageItems.
Набор элементов редактора не является defaults: общий и все format selectors первоначально
не выбраны. Каждый формат требует явного Allow/Deny, в том числе при global Deny.
Для разрешённого формата обязательно выбрать положительный Int64 limit в байтах либо явно
«Без лимита». Для запрещённого формата `MaxBytes` не задаётся.

Custom-format UI всё ещё отсутствует. При этом durable extension contract для custom binary
уже существует в Current v6: exact `FormatName → FileExtension` хранится в
`CustomBinaryFormatConfiguration`, а `RepositoryClipboardCustomBinaryFileExtensionProvider`
предоставляет fail-closed production provider. UI первичной global policy этот mapping пока
не создаёт и не добавляет custom formats автоматически.

SQLite read/write выполняются вне UI thread с session cancellation и generation checks.
Lock/close инвалидирует stale results и очищает protected UI state. Само чтение или
сохранение policy worker не запускает.

## Composition status

Persisted policy подключена к `ProtectedClipboardDeliveryServices.TryCreateAsync`: boundary
собирает capture/history services и protected delivery через явную factory, а для отсутствующей
policy возвращает `null`. Контракт: `PROTECTED_CLIPBOARD_DELIVERY_COMPOSITION.md`.

Источник custom-binary extensions больше не является открытым архитектурным вопросом:
Current v6 repository/provider реализованы. **App-level вызов composition и worker lifecycle
по-прежнему не подключены.** App должен после unlock/initial setup создать provider из той же
active session и передать его в composition boundary; запуск/остановка worker остаются отдельным
lifecycle tranche.

Policy cleanup для последующего изменения остаётся отдельным этапом. Конкретные format/size
defaults по-прежнему не назначены.
