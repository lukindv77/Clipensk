# Clipboard history schema contract

Этот документ фиксирует durable representation clipboard history, добавленное в Current schema v4 и сохраняемое без изменения в текущем production Current schema v6. Он описывает history storage/read contract; worker lifecycle, archive transfer и UI behavior имеют отдельные контракты.

## Event envelope

`ClipboardHistoryEvent` хранит одно принятое clipboard event:

- `EventId` — Clipensk-owned GUID;
- `EventUtc` — UTC timestamp события;
- `LocalOffsetMinutes` — локальный offset события в минутах;
- `WindowsTimeZoneId` — Windows time-zone ID из `EventTimeContext`;
- `CalendarDate` — сохранённая календарная дата события для archive ownership;
- `SourceApplicationId` — nullable durable Clipensk `ApplicationId`;
- `SourceProcessId`, `SourceExecutablePath`, `SourceApplicationUserModelId` — runtime snapshot metadata, не durable identity.

`InvocationApplication` сюда не записывается: это context вызова журнала, а не источник clipboard event.

Удаление Application identity не должно удалять историю. FK для `SourceApplicationId` использует `ON DELETE SET NULL`; runtime source snapshot остаётся у события.

## Ordered payloads

`ClipboardHistoryPayload` использует composite key `(EventId, PayloadOrder)`. Порядок payload внутри accepted capture сохраняется явно.

Для каждого payload сохраняются:

- исходный exact `FormatName`;
- устойчивый `PayloadKind`;
- exact `CanonicalByteCount` после уже выполненного MaxBytes gate;
- inline canonical representation либо external content address;
- `SearchText`, когда такая projection определена.

`MaxBytes` не сохраняется: это policy input. Конкретный reader implementation также не является durable schema data.

## Payload representation

Inline payloads:

- `Text` — исходный Plain Text / HTML / RTF representation в `InlineCanonicalText`; `SearchText` отдельно;
- `Link` — `Uri.OriginalString`, то есть та же canonical URI string, чьи UTF-8 bytes участвовали в MaxBytes gate;
- `StorageItems` — exact versioned canonical JSON representation в `InlineCanonicalText`.

External payloads:

- `PngImage` — normalized PNG content address;
- `CustomBinary` — exact stored custom binary content address.

External row хранит SHA-256, relative path и size. Бинарные bytes не дублируются BLOB-ом в SQLite.

`SqliteClipboardHistorySink` не придумывает внешний адрес. До начала history SQL transaction он обязан разрешить каждый внешний payload через `IClipboardExternalPayloadAddressResolver`. Если разрешение хотя бы одного payload завершается ошибкой или отменой, event/payload rows не записываются.

Переданный resolver-у `eventCalendarDate` является candidate date только для впервые сохраняемого content object. Для уже известного content-address resolver обязан вернуть ранее persisted address/relative path и сохранить исходную first-stored date.

`CatalogClipboardExternalPayloadAddressResolver` использует Catalog v2 `IExternalPayloadAddressIndex` как race-safe SHA→address reservation и затем `ExternalPayloadStore.EnsureStoredAtAddressAsync` для записи или восстановления exact persisted file. Повторный SHA всегда использует первый зарезервированный path.

Для PNG physical extension фиксирован `.png`. Для нового custom binary extension обязан предоставить `IClipboardCustomBinaryFileExtensionProvider`; пустой результат запрещён и не получает скрытого `.bin` fallback. Если custom SHA уже присутствует в Catalog, старый persisted address используется без вызова extension provider.

Sink дополнительно проверяет, что address соответствует exact payload bytes по SHA-256 и size и не выходит за configured `Files` root.

## Transaction boundary

Все inline representations и external addresses должны быть подготовлены до начала history SQL transaction.

В одной transaction записываются:

1. `ClipboardHistoryEvent`;
2. все ordered `ClipboardHistoryPayload` rows.

Cancellation проверяется непосредственно перед `COMMIT`. После успешного `COMMIT` sink возвращает успех и не превращает durable запись в поздний `OperationCanceledException`.

Это исключает частично записанный SQL event. Возможный orphan external file после подготовленного external payload и последующего SQL failure относится к reference tracking/GC, а не является основанием создавать неполный history event.

## Integrity

- Event → payload: `ON DELETE CASCADE`.
- Application identity → event: `ON DELETE SET NULL`.
- `PayloadOrder >= 0`.
- `CanonicalByteCount >= 0`.
- inline/external representation взаимоисключающие по `PayloadKind`.
- event index начинается с `CalendarDate`, чтобы Current→Archive мог выбирать целые календарные дни без пересчёта времени.

## Versioning

Current evolution:

- v1 — database identity;
- v2 — application identity;
- v3 — application capture policy;
- v4 — clipboard history;
- v5 — persisted global capture policy;
- v6 — custom binary format configuration.

History table contract введён в v4 и остаётся совместимым в Current v5/v6. DDL/validation реализован `ClipboardHistorySqlSchema`; production bootstrap/migration принадлежит `ProtectedStorageDatabaseService`. History sink требует Current schema v4 или новее и сам schema не создаёт.

Текущая production pair: **Current v6 / Catalog v2**.

## Current history read contract

`ICurrentClipboardHistoryRepository.ReadAsync(JournalDateRange period, int limit, CancellationToken)` возвращает snapshot сохранённых событий из Current. `SqliteCurrentClipboardHistoryRepository` реализует boundary внутри `ProtectedStorageSessionLease`.

- Период и положительный `limit` обязательны; скрытых defaults нет.
- Период применяется к сохранённой `CalendarDate` включительно с обеих сторон, как задано `JournalDateRange`.
- События упорядочены по `EventUtc DESC`, затем exact `EventId DESC` с BINARY comparison.
- `limit` означает число событий, не payload rows; он применяется в SQL до LEFT JOIN payloads.
- Один SELECT обеспечивает единый SQLite read snapshot для envelope и payload rows.
- Envelope возвращает исходный `EventId`, durable source `ApplicationId`, runtime source snapshot и полный `EventTimeContext`.
- UTC/offset восстанавливаются из persisted values без текущей Windows time zone; сохранённая calendar date проверяется, а не пересчитывается.
- Inline representations, `SearchText`, canonical byte counts и external `SHA/RelativePath/SizeBytes` возвращаются без нормализации при чтении.
- External payload bytes не читаются; отсутствие физического файла не скрывает persisted reference.
- Чтение открывает только `Current/current.db` в `ReadOnly`, проверяет StorageId, роль, schema/user version и history schema shape.
- Repository creation не открывает БД и не создаёт/migrate schema.
- Некорректные event IDs, temporal/source metadata, payload order, canonical byte counts или external reference metadata завершают запрос ошибкой без частичного результата.
- Caller cancellation и session cancellation объединяются; проверка выполняется до/после открытия, между строками и перед возвратом.
- Repository не кэширует результаты и не продлевает session lifecycle.

Это bounded Current read boundary. Archive reads, unified Current+Archive query, FTS и source filters остаются отдельными этапами. `limit` не ограничивает суммарный payload volume выбранных событий.

Microsoft.Data.Sqlite операции здесь синхронные; repository сам не запускает background worker и не обещает preemptive interruption внутри отдельного SQLite вызова. App-level clipboard worker — другой lifecycle boundary, описанный в `CLIPBOARD_WORKER_LIFECYCLE.md`.

### Keyset continuation для Current

После первой страницы вызывающая сторона создаёт `ClipboardHistoryCursor.FromEntry(period, lastEntry)` и вызывает `ReadBeforeAsync(period, limit, cursor, cancellationToken)`.

- Курсор содержит только исходный period, UTC timestamp и непустой EventId; lease/connection/payload он не удерживает.
- Не-UTC timestamp и смена периода отклоняются; при смене периода чтение начинается заново через `ReadAsync`.
- Продолжение эксклюзивно: `EventUtc < cursor.UtcTimestamp` либо равное время и `EventId COLLATE BINARY < cursor.EventId`.
- GUID сериализуется в lowercase D; нек canonical persisted spelling отклоняется, чтобы BINARY ordering не менялся при round-trip.
- LIMIT применяется к событиям до JOIN; payload одного события не разрываются между страницами.
- Каждая страница — отдельный snapshot. Удаление anchor event и вставка более нового события не сдвигают continuation; backdated insert ниже курсора может появиться на следующей странице.
- Stable snapshot всей view session, archive transfer concurrency и полнота при mutation старых событий этим API не обещаются.

## Protected composition и текущий App wiring

`ProtectedClipboardHistoryServices.Create` создаёт `HistoryRepository` вместе с index/resolver/sink для одной активной `ProtectedStorageSessionLease` и той же connection factory. Creation не открывает БД и не вызывает extension provider; caller обязан передать period/limit при фактическом чтении.

Для capture path App уже создаёт protected delivery после появления активной protected storage session и persisted global policy, использует storage-backed custom-binary extension provider и управляет `ClipboardAcceptedCaptureWorker` по lifecycle rules. Lock/dispose/reopen инвалидируют прежний protected graph; при отсутствии initial global policy worker не запускается.

Это не означает готовность unified journal UI: Current+Archive query composition, archive storage и manual real-clipboard WinUI smoke остаются отдельными работами.
