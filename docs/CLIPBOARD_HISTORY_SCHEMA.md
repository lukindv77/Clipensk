# Clipboard history schema contract

Этот документ фиксирует durable representation clipboard history для Current schema v4. Он не включает worker lifecycle, archive transfer или UI/query behavior.

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

Sink дополнительно проверяет, что возвращённый address соответствует exact payload bytes по SHA-256 и size и не выходит за configured `Files` root. Persistent hash/address index и его race semantics реализуются отдельным storage tranche.

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
