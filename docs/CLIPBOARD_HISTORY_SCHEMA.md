# Clipboard history schema contract

Этот документ фиксирует durable representation clipboard history для Current schema v4 и минимальный Current read contract. Он не включает worker lifecycle, archive transfer или UI behavior.

## Event envelope

`ClipboardHistoryEvent` хранит одно принятое clipboard event:

- `EventId` — Clipensk-owned GUID;
- `EventUtc` — UTC timestamp события;
- `LocalOffsetMinutes` — локальный offset события в минутах;
- `WindowsTimeZoneId` — Windows time-zone ID из `EventTimeContext`;
- `CalendarDate` — календарная дата события, используемая будущим archive ownership;
- `SourceApplicationId` — nullable durable Clipensk `ApplicationId`;
- `SourceProcessId`, `SourceExecutablePath`, `SourceApplicationUserModelId` — runtime snapshot metadata, не durable identity.

`InvocationApplication` сюда не записывается: это отдельный context вызова журнала, а не источник clipboard event.

Удаление Application identity не должно удалять историю. FK для `SourceApplicationId` использует `ON DELETE SET NULL`, при этом runtime source snapshot остаётся у события.

## Ordered payloads

`ClipboardHistoryPayload` использует composite key `(EventId, PayloadOrder)`. Порядок payload внутри accepted capture сохраняется явно.

Для каждого payload сохраняются:

- исходный `FormatName`;
- устойчивый `PayloadKind`;
- exact `CanonicalByteCount` после уже выполненного MaxBytes gate;
- inline canonical representation либо external content address;
- `SearchText`, когда такая projection определена.

`MaxBytes` не сохраняется: это policy input, а не свойство исторического payload. Конкретный reader implementation также не является durable schema data.

## Payload representation

Inline payloads:

- `Text` — исходный Plain Text / HTML / RTF representation в `InlineCanonicalText`; `SearchText` отдельно;
- `Link` — `Uri.OriginalString`, то есть та же canonical URI string, чьи UTF-8 bytes уже участвовали в MaxBytes gate;
- `StorageItems` — exact versioned canonical JSON representation в `InlineCanonicalText`.

External payloads:

- `PngImage` — normalized PNG content address;
- `CustomBinary` — exact stored custom binary content address.

External row хранит SHA-256, relative path и size. Бинарные bytes не дублируются BLOB-ом в SQLite.

`SqliteClipboardHistorySink` не придумывает внешний адрес. Перед открытием history SQL transaction он обязан разрешить каждый внешний payload через `IClipboardExternalPayloadAddressResolver`. Если разрешение хотя бы одного payload завершается ошибкой или отменой, event/payload rows не записываются.

Переданный resolver-у `eventCalendarDate` является только candidate date для **впервые сохраняемого** content object. Для уже известного content-address resolver обязан вернуть ранее зафиксированный address/relative path и тем самым сохранить исходную `firstStoredDate`. Использовать дату текущего capture как новую `firstStoredDate` для существующего duplicate запрещено.

`CatalogClipboardExternalPayloadAddressResolver` использует Catalog v2 `IExternalPayloadAddressIndex` как race-safe SHA→address reservation и затем `ExternalPayloadStore.EnsureStoredAtAddressAsync` для записи или восстановления exact persisted file. Поэтому повторный SHA всегда использует первый зарезервированный path, даже если текущий capture пришёл в другую календарную дату.

Для PNG physical extension фиксирован `.png`. Для нового custom binary extension обязан предоставить `IClipboardCustomBinaryFileExtensionProvider`; пустой результат запрещён и не получает скрытого `.bin` fallback. Если custom SHA уже присутствует в Catalog, старый persisted address используется без вызова extension provider.

Sink дополнительно проверяет, что возвращённый address соответствует exact payload bytes по SHA-256 и size и не выходит за configured `Files` root.

## Transaction boundary

Все inline representations и все external addresses должны быть подготовлены до начала history SQL transaction.

В одной transaction записываются:

1. `ClipboardHistoryEvent`;
2. все ordered `ClipboardHistoryPayload` rows.

Cancellation проверяется непосредственно перед `COMMIT`. После успешного `COMMIT` sink возвращает успех и не должен превращать уже состоявшуюся durable запись в поздний `OperationCanceledException`.

Это обеспечивает отсутствие частично записанного SQL event. Возможный orphan external file после успешно подготовленного external payload и последующего SQL failure является отдельной задачей reference tracking/GC, а не основанием создавать неполный history event.

## Integrity

- Event → payload: `ON DELETE CASCADE`.
- Application identity → event: `ON DELETE SET NULL`.
- `PayloadOrder >= 0`.
- `CanonicalByteCount >= 0`.
- inline/external representation являются взаимоисключающими по `PayloadKind`.
- event index начинается с `CalendarDate`, чтобы будущая Current→Archive операция могла выбирать целые календарные дни без пересчёта времени.

## Versioning

Current schema v1 содержала только database identity, v2 добавила application identity, v3 — persisted capture policies. Clipboard history добавлена как v4 поверх валидного v3.

DDL/validation contract реализован `ClipboardHistorySqlSchema`; production bootstrap/migration принадлежит `ProtectedStorageDatabaseService`. History sink требует совместимую Current schema v4 или новее и сам schema не создаёт.

## Current history read contract

`ICurrentClipboardHistoryRepository.ReadAsync(JournalDateRange period, int limit, CancellationToken)`
возвращает отдельный snapshot сохранённых событий из Current. `SqliteCurrentClipboardHistoryRepository`
реализует этот boundary внутри `ProtectedStorageSessionLease`.

- Период и положительный `limit` обязательны; скрытых defaults и автоматического выбора периода нет.
- Период применяется к **сохранённой** `CalendarDate` включительно с обеих сторон, как задано `JournalDateRange`.
- События упорядочены по `EventUtc DESC`, затем exact `EventId DESC` с BINARY comparison.
- Лимит означает число событий, а не число payload rows. Он применяется в SQL до LEFT JOIN payloads;
  все payload выбранного события возвращаются в порядке `PayloadOrder`.
- Один SELECT обеспечивает единый SQLite read snapshot для event envelopes и payload rows.
- Envelope возвращает исходный `EventId`, durable source `ApplicationId`, runtime source snapshot и полный
  `EventTimeContext`. UTC и offset восстанавливаются из persisted values без обращения к текущей Windows
  time zone; сохранённая calendar date проверяется на согласованность, а не заменяется новой датой.
- Inline representations, `SearchText`, canonical byte counts и external `SHA/RelativePath/SizeBytes`
  возвращаются из history rows. StorageItems JSON не пересериализуется, URI не нормализуется.
- External payload bytes не читаются. Отсутствующий внешний файл не мешает получить его сохранённую ссылку.
  Дата/путь первого сохранения не пересчитываются; Catalog и extension provider для чтения не нужны.
- Чтение открывает только `Current/current.db` в `ReadOnly`, проверяет StorageId, роль, schema/user version
  и history schema shape. Создание repository не открывает БД; чтение не создаёт и не мигрирует schema.
- Некорректные event IDs, временные поля, source snapshot, payload order, canonical inline byte counts
  или external reference metadata приводят к ошибке целого запроса без возврата частичного результата.
  Проверка metadata внешней ссылки не является проверкой существования, размера или SHA фактического файла.
- Caller cancellation и session cancellation объединяются. Отмена проверяется до открытия, после открытия,
  между строками и перед возвратом результата. Reader/connection освобождаются при успехе, отмене и ошибке.
- Repository не хранит результаты между вызовами и не продлевает session lifecycle. Возвращённые строки
  уже являются данными в памяти вызывающей стороны; очистка UI/cache после lock остаётся её обязанностью.

Это bounded Current read boundary, а не готовый journal query service: archive reads, FTS, source filters,
UI composition остаются отдельными этапами. `limit` ограничивает число событий,
но не суммарный объём их payload. Microsoft.Data.Sqlite операции выполняются синхронно; этот метод
не запускает background worker и не обещает preemptive interruption внутри отдельного SQLite вызова.
Будущий host должен явно определить execution/cancellation lifecycle перед подключением к UI.

### Keyset continuation для Current

После первой страницы `ReadAsync` вызывающая сторона создаёт
`ClipboardHistoryCursor.FromEntry(period, lastEntry)` из последнего события и вызывает
`ReadBeforeAsync(period, limit, cursor, cancellationToken)`. Период и положительный лимит
задаются явно на каждой странице; default page size, OFFSET и автоматического COUNT нет.

- Курсор содержит только исходный период, UTC timestamp и непустой EventId; он не удерживает
  lease, connection или payload. Не-UTC timestamp и смена периода отклоняются; при смене периода
  чтение начинается заново через `ReadAsync`.
- Продолжение эксклюзивно: `EventUtc < cursor.UtcTimestamp` либо равное время и
  `EventId COLLATE BINARY < cursor.EventId`. GUID сериализуется в lowercase D, как в history sink.
  Неканоническое написание persisted EventId отклоняется, чтобы преобразование в GUID и обратно
  не меняло позицию в BINARY ordering.
- LIMIT применяется к событиям до JOIN; payload одного события не разрываются между страницами.
  Пустая страница означает отсутствие дальнейших событий на момент этого SELECT.
- Каждая страница — отдельный snapshot. Удаление anchor event и вставка более нового события
  не сдвигают продолжение. Вставка задним числом ниже курсора может появиться на следующей странице;
  для записей выше курсора требуется начать чтение заново. Стабильный snapshot всей сессии просмотра,
  поведение переноса в archive и полнота чтения при изменении старых событий этим API не обещаются.
- Все проверки protected session, caller/session cancellation и metadata применяются и к продолжению.

Current v4 / Catalog v2 и SQL schema не изменяются.

Существующие Current v4 / Catalog v2 сохраняются; app-level capture delivery по-прежнему требует явную
глобальную `ClipboardCapturePolicy` с утверждённым источником, без самостоятельно выбранного Allow/Deny.

## Protected composition

`ProtectedClipboardHistoryServices.Create` создаёт `HistoryRepository` вместе с index/resolver/sink
для одной активной `ProtectedStorageSessionLease` и той же connection factory. Свойство имеет тип
`ICurrentClipboardHistoryRepository`. Creation не открывает БД и не вызывает extension provider;
caller обязан передать period/limit при фактическом чтении. Отзыв session и caller cancellation
остаются действующими после получения repository из composition.
App/UI не подключаются автоматически, background worker не запускается.
