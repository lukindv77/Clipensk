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
- `Link` — canonical URI string в `InlineCanonicalText`;
- `StorageItems` — exact versioned canonical JSON representation в `InlineCanonicalText`.

External payloads:

- `PngImage` — normalized PNG content address;
- `CustomBinary` — exact stored custom binary content address.

External row хранит SHA-256, relative path и size. Бинарные bytes не дублируются BLOB-ом в SQLite.

## Integrity

- Event → payload: `ON DELETE CASCADE`.
- Application identity → event: `ON DELETE SET NULL`.
- `PayloadOrder >= 0`.
- `CanonicalByteCount >= 0`.
- inline/external representation являются взаимоисключающими по `PayloadKind`.
- event index начинается с `CalendarDate`, чтобы будущая Current→Archive операция могла выбирать целые календарные дни без пересчёта времени.

## Versioning

Current schema v1 содержала только database identity, v2 добавила application identity, v3 — persisted capture policies. Clipboard history добавляется как v4 отдельным migration tranche поверх уже валидного v3.

DDL/validation contract реализован `ClipboardHistorySqlSchema`. До подключения migration наличие contract class не означает, что production bootstrap уже создаёт v4 database.
