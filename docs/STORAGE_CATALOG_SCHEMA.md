# Storage catalog schema evolution

`storage-catalog.db` является rebuildable accelerator и картой физического хранилища. Критическая информация не должна существовать только в каталоге.

## Version ownership

- `storage-catalog.db` v1: только `DatabaseIdentity`;
- `storage-catalog.db` v2: добавляет rebuildable индекс адресов внешних clipboard payload.

Версия catalog независима от `current.db` schema version. Текущий production bootstrap создаёт новый catalog сразу как v2 и принимает legacy v1 только как вход для resumable migration.

## Catalog v2 — external payload address index

`ExternalPayloadAddressIndex` хранит ускоряющее отображение:

```text
SHA-256(exact stored bytes) -> RelativePath + SizeBytes
```

Поля:

- `Sha256` — lowercase 64-character SHA-256, primary key;
- `RelativePath` — единственный persisted путь внутри `Files`, unique;
- `SizeBytes` — размер exact stored bytes, неотрицательный.

Отдельное поле `FirstStoredDate` не требуется: дата первого физического размещения уже является частью canonical relative path `YYYY-MM-DD/<sha>.<extension>`.

Глобальный ключ — именно SHA-256 exact stored bytes. Он не зависит от source application, clipboard format name, capture date или текущей БД истории.

## Initialization и migration

Новая storage pair создаётся как:

- `current.db` v4;
- `storage-catalog.db` v2 с `ExternalPayloadAddressIndex`.

Для существующей pair сначала валидируются обе БД в их допустимых входных версиях. До успешной whole-pair validation mutation не выполняется.

Legacy Catalog v1 мигрирует отдельной транзакцией:

1. создаётся `ExternalPayloadAddressIndex`;
2. `DatabaseIdentity.SchemaVersion` меняется `1 -> 2` только для роли `StorageCatalog` и ожидаемого `StorageId`;
3. `PRAGMA user_version` меняется на 2;
4. transaction commit.

Если transaction не commit-ится, Catalog остаётся полноценным v1 и следующая разблокировка может повторить v1 -> v2. Current schema version этой migration не изменяется.

После migration Current и Catalog повторно валидируются в production versions v4/v2.

## Runtime reservation semantics

`SqliteExternalPayloadAddressIndex` предоставляет protected-session lookup/reservation поверх Catalog v2.

`GetOrAdd(candidate)` выполняется транзакционно:

- для нового SHA сохраняется candidate address;
- для уже существующего SHA возвращается ранее сохранённый address, даже если новый candidate построен из более поздней capture date;
- тот же SHA с другим `SizeBytes` считается конфликтом и завершается fail-closed;
- collision одного `RelativePath` между разными SHA не перезаписывается и завершается fail-closed.

Индекс проверяет `StorageId`, роль `StorageCatalog`, schema/user version и точную форму `UNIQUE(RelativePath)` перед использованием. Операции связаны с cancellation token активного `ProtectedStorageSessionLease`.

## Source of truth и rebuild

Catalog row не является единственным источником адреса. Исторические payload rows в `current.db` и `archive_*.db` сохраняют `ExternalSha256`, `ExternalRelativePath` и `ExternalSizeBytes`.

Следовательно, при потере каталога индекс должен быть восстановим сканированием Current + всех Archive. Rebuild обязан fail-closed при конфликте, когда один SHA встречается с разными relative paths или sizes.

## First-stored semantics

Для нового SHA capture calendar date может использоваться как дата первого размещения.

Для уже известного SHA resolver обязан вернуть ранее сохранённый `RelativePath`; новая capture date не должна перемещать payload и не должна создавать второй физический файл.

## Custom binary extension

Catalog schema не определяет правило выбора расширения для нового custom binary payload. Оно хранит уже выбранный physical relative path и поэтому не требует знания extension при повторном dedup lookup.

Правило выбора extension для впервые сохраняемого custom binary остаётся отдельным representation contract и не должно угадываться catalog schema.
