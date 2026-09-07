# Clipboard history schema contract

Этот документ фиксирует durable representation clipboard history, добавленное в Current schema v4 и сохраняемое без изменения в production Current v6 и Archive v1. Он описывает history storage/read contract; worker lifecycle, archive transfer и UI behavior имеют отдельные контракты.

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

`CatalogClipboardExternalPayloadAddressResolver` использует существующий в Catalog v3 индекс `ExternalPayloadAddressIndex`, введённый в Catalog v2, как race-safe SHA→address reservation и затем `ExternalPayloadStore.EnsureStoredAtAddressAsync` для записи или восстановления exact persisted file. Повторный SHA всегда использует первый зарезервированный path.

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

History table contract введён в v4 и остаётся совместимым в Current v5/v6 и Archive v1. DDL/validation реализован `ClipboardHistorySqlSchema`; production bootstrap/migration принадлежит `ProtectedStorageDatabaseService`. History sink требует Current schema v4 или новее и сам schema не создаёт.

Текущая production pair: **Current v6 / Catalog v3**, Archive schema остаётся **v1**.

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

`limit` не ограничивает суммарный payload volume выбранных событий.

Microsoft.Data.Sqlite операции здесь синхронные; protected composition не выполняет такие reads на UI thread. Clipboard worker является отдельным lifecycle boundary.

### Keyset continuation

После первой страницы вызывающая сторона создаёт `ClipboardHistoryCursor.FromEntry(period, lastEntry)` и вызывает `ReadBeforeAsync(period, limit, cursor, cancellationToken)`.

- Курсор содержит только исходный period, UTC timestamp и непустой EventId; lease/connection/location/payload он не удерживает.
- Не-UTC timestamp и смена периода отклоняются; при смене периода чтение начинается заново через `ReadAsync`.
- Продолжение эксклюзивно: `EventUtc < cursor.UtcTimestamp` либо равное время и `EventId COLLATE BINARY < cursor.EventId`.
- GUID сериализуется в lowercase D; non-canonical persisted spelling отклоняется, чтобы BINARY ordering не менялся при round-trip.
- LIMIT применяется к событиям до JOIN; payload одного события не разрываются между страницами.
- Каждая физическая страница — отдельный SQLite snapshot.

Этот cursor теперь является логической позицией общего Current+Archive порядка и не содержит database-specific state.

## Unified Current + Archive read contract

`IUnifiedClipboardHistoryRepository` и `ProtectedUnifiedClipboardHistoryRepository` предоставляют read-only logical history поверх Current и Archive v1 без изменения durable history schema.

Read planning:

1. читается Catalog v3 `ArchiveSegmentIndex` через `ProtectedArchiveSegmentCatalog.ReadAsync`;
2. набор canonical `Archive/archive_*.db` filenames сверяется с Catalog projection; missing/unindexed physical archive или stale filename set завершается fail-closed до открытия unindexed Archive DB;
3. Current ReadOnly aggregate `MIN/MAX(CalendarDate)` формирует `CurrentStoreDescriptor`;
4. `StorageQueryPlanner` выбирает Current и только те Archive segments, coverage которых пересекает requested period;
5. не выбранные Archive databases не открываются для history read.

Catalog остаётся accelerator, а не authoritative Archive identity. Каждый выбранный Archive перед и после чтения полностью валидируется ReadOnly через `ProtectedArchiveDatabaseService.ValidateAsync`; planned `DatabaseId`, canonical filename и coverage обязаны совпадать с Archive identity.

### Physical read ordering and transfer safety

Unified reader намеренно наблюдает Current **до** выбранных Archive.

Current→Archive transfer имеет durable order `Archive COMMIT -> Archive verify -> Current purge`. Поэтому такой read order не создаёт логический пропуск при конкурентном transfer:

- если Current ещё не purged, событие видно в Current;
- если Current уже purged, Archive COMMIT уже состоялся и событие видно в Archive;
- crash-window `Current=yes, Archive=yes` может вернуть два physical copies, которые затем объединяются в одну logical entry.

Это не является глобальным SQLite snapshot всех DB. Каждый physical database read имеет собственный ReadOnly snapshot. Без глобального maintenance coordinator API не обещает point-in-time snapshot всей storage pair; correctness обеспечивается durable transfer order, exact duplicate verification и layout stability checks.

### Logical merge and physical locations

Глобальный порядок один для всех physical readers:

```text
EventUtc DESC, canonical EventId BINARY DESC
```

Каждый selected source применяет тот же `period`, `limit` и optional `ClipboardHistoryCursor`; затем страницы merge-ятся и global `limit` применяется к logical events.

`UnifiedClipboardHistoryEntry` содержит:

- одну detached `ClipboardHistoryEntry`;
- один или несколько `ClipboardHistoryPhysicalLocation`.

Location для Current не содержит archive identity. Archive location содержит exact `DatabaseId + FileName` выбранного segment.

Одинаковый `EventId` из нескольких physical DB схлопывается только при exact logical equality:

- EventId;
- UTC timestamp;
- persisted offset;
- exact `WindowsTimeZoneId`;
- durable SourceApplicationId;
- runtime source snapshot;
- количество и exact ordered payload records, включая inline/search/external reference metadata.

Одинаковый UTC instant с другим persisted offset или zone ID **не** считается exact duplicate. Любое расхождение одного EventId между physical databases завершается `InvalidDataException`, а не выбирает одну копию silently.

### Layout stability and cancellation

После merge Catalog projection и physical archive filename set читаются повторно. Изменение `DatabaseId/filename/coverage/IsSealed`, добавление/удаление archive file либо другое расхождение layout во время запроса требует retry и не возвращает silently incomplete page.

Caller cancellation и protected-session cancellation связаны. Revoked access не возвращает partial page. Repository creation не открывает ни Current, ни Catalog, ни Archive.

## Protected composition and App wiring

`ProtectedClipboardHistoryServices.Create` создаёт для одной active `ProtectedStorageSessionLease`:

- external address index/resolver;
- capture `HistorySink`;
- существующий Current-only `HistoryRepository`;
- новый `UnifiedHistoryRepository`.

Creation инертно: не открывает БД, не вызывает custom extension provider и не запускает background work. Capture/write path не переключён на unified repository и его lifecycle semantics не изменены.

App capture composition/worker уже привязаны к exact protected session и persisted global policy. Unified repository доступен read-side composition, но отдельное JournalWindow UI подключение unified pages в этом tranche не утверждается.

Manual real-clipboard/WinUI smoke остаётся отдельным неполученным evidence. FTS/source filters и stable point-in-time snapshot всей multi-DB view остаются отдельными возможностями.
