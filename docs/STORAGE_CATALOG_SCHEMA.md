# Storage catalog schema evolution

`storage-catalog.db` является rebuildable accelerator и картой физического хранилища. Критическая информация не должна существовать только в каталоге.

## Version ownership

- `storage-catalog.db` v1: только `DatabaseIdentity`;
- `storage-catalog.db` v2: добавляет rebuildable индекс адресов внешних clipboard payload.

Версия Catalog независима от `current.db` schema version. Текущий production bootstrap создаёт новую protected pair как **Current v6 / Catalog v2**. Legacy Catalog v1 принимается только как вход для resumable migration.

## Catalog v2 — external payload address index

`ExternalPayloadAddressIndex` хранит ускоряющее отображение:

```text
SHA-256(exact stored bytes) -> RelativePath + SizeBytes
```

Поля:

- `Sha256` — lowercase 64-character SHA-256, primary key;
- `RelativePath` — единственный persisted путь внутри `Files`, unique;
- `SizeBytes` — размер exact stored bytes, неотрицательный.

Отдельное поле `FirstStoredDate` не требуется: дата первого физического размещения входит в canonical relative path `YYYY-MM-DD/<sha>.<extension>`.

Глобальный ключ — именно SHA-256 exact stored bytes. Он не зависит от source application, clipboard format name, capture date или текущей/архивной БД истории.

## Initialization и migration

Новая storage pair создаётся как:

- `current.db` v6;
- `storage-catalog.db` v2 с `ExternalPayloadAddressIndex`.

Current и Catalog имеют независимые schema versions. Current v1→v6 и Catalog v1→v2 мигрируют resumably по своим контрактам.

Для существующей pair сначала валидируются обе БД в допустимых входных версиях. До успешной whole-pair validation mutation не выполняется.

Legacy Catalog v1 мигрирует отдельной транзакцией:

1. создаётся `ExternalPayloadAddressIndex`;
2. `DatabaseIdentity.SchemaVersion` меняется `1 -> 2` только для роли `StorageCatalog` и ожидаемого `StorageId`;
3. `PRAGMA user_version` меняется на 2;
4. transaction commit.

Если transaction не commit-ится, Catalog остаётся полноценным v1 и следующая разблокировка может повторить migration. Current schema version этой transaction не изменяется.

После всех migration production pair повторно валидируется как Current v6 / Catalog v2.

## Runtime reservation semantics

`SqliteExternalPayloadAddressIndex` предоставляет protected-session lookup/reservation поверх Catalog v2.

`GetOrAdd(candidate)` выполняется транзакционно:

- для нового SHA сохраняется candidate address;
- для уже существующего SHA возвращается ранее сохранённый address, даже если новый candidate построен из более поздней capture date;
- тот же SHA с другим `SizeBytes` считается конфликтом и завершается fail-closed;
- collision одного `RelativePath` между разными SHA не перезаписывается и завершается fail-closed.

Индекс проверяет `StorageId`, роль `StorageCatalog`, schema/user version и exact `UNIQUE(RelativePath)` contract перед использованием. Операции связаны с cancellation token активного `ProtectedStorageSessionLease`.

## Source of truth и rebuild

Catalog row не является единственным источником адреса. Исторические payload rows сохраняют `ExternalSha256`, `ExternalRelativePath` и `ExternalSizeBytes`.

Когда archive storage будет реализован, rebuild Catalog должен сканировать Current + все валидные Archive и fail-closed при конфликте, если один SHA встречается с разными relative paths или sizes. До появления archive implementation production runtime фактически имеет только Current history source.

Потеря `storage-catalog.db` не должна превращать Catalog в единственный носитель критической информации; восстановление индекса остаётся обязательной maintenance capability.

## First-stored semantics

Для нового SHA capture calendar date может использоваться как дата первого размещения.

Для уже известного SHA resolver обязан вернуть ранее сохранённый `RelativePath`; новая capture date не перемещает payload и не создаёт второй physical file.

## Custom binary extension

Catalog schema не определяет правило выбора расширения для нового custom binary payload. Оно хранит уже выбранный physical relative path и не требует знания extension при повторном dedup lookup.

В Current v6 storage-scoped mapping `FormatName -> FileExtension` хранится отдельно в `CustomBinaryFormatConfiguration`; production provider читает его через protected session. Для нового custom SHA mapping обязателен; существующий Catalog SHA использует persisted address без повторного выбора extension.

Первичная global capture policy + custom-binary extension configuration сохраняется aggregate service-ом атомарно в Current v6. Catalog v2 при этом не меняется.
