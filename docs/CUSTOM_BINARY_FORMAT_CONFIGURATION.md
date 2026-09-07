# Custom binary format file-extension configuration

## Назначение

Current schema v6 фиксирует storage-scoped источник физического расширения для новых explicitly enabled registered/private binary clipboard payload.

Windows clipboard format name не считается файловым расширением и не преобразуется эвристически через MIME, registry associations или произвольные правила. Для первого сохранения нового custom-binary SHA расширение должно быть явно настроено для exact `FormatName`.

## Durable schema

Current v6 добавляет таблицу `CustomBinaryFormatConfiguration`:

- `FormatName TEXT NOT NULL PRIMARY KEY` — exact clipboard format name; comparison использует SQLite `BINARY` semantics;
- `FileExtension TEXT NOT NULL` — canonical physical extension.

Конфигурация хранится в зашифрованном `Current/current.db`, принадлежит выбранному storage и не переносится в JSON settings или rebuildable Catalog.

Catalog v2 продолжает хранить уже зарезервированный SHA → relative path. Если custom payload с тем же SHA уже известен Catalog, его первый persisted address используется повторно, и extension configuration для duplicate не переопределяет физический путь.

## Canonical extension

`ExternalPayloadAddressFactory.NormalizeCustomBinaryExtension` определяет canonical representation:

- вход не может быть null/empty/whitespace;
- surrounding whitespace удаляется;
- leading `.` добавляется, если отсутствует;
- path separators и invalid filename characters запрещены;
- результат приводится к lowercase invariant;
- значение `.` запрещено.

Persisted repository повторно проверяет, что прочитанное значение уже находится в canonical form. Неканоническое или повреждённое значение является ошибкой, а не fallback.

## Repository contract

`ICustomBinaryFormatConfigurationRepository` предоставляет:

- `ReadFileExtensionAsync(formatName, token)` — nullable exact lookup;
- `InitializeAsync(formatName, fileExtension, token)` — атомарное первое назначение.

`SqliteCustomBinaryFormatConfigurationRepository` работает только через активную `ProtectedStorageSessionLease`, проверяет StorageId, роль Current, schema/user version и table shape. Read использует ReadOnly; initialization — ReadWrite + immediate transaction.

Повторное `InitializeAsync` для уже существующего exact `FormatName` запрещено, даже если передано то же расширение. Rebind/update требует отдельного cleanup contract, потому что существующая история и external payload addresses уже могут ссылаться на прежнее расширение.

Caller cancellation и session cancellation объединяются. Отмена/ошибка до COMMIT откатывает initialization. После успешного COMMIT нет late cancellation, превращающего состоявшуюся durable запись в ошибку.

## Extension provider

`RepositoryClipboardCustomBinaryFileExtensionProvider` адаптирует repository к существующему `IClipboardCustomBinaryFileExtensionProvider`.

Для нового custom binary payload:

1. resolver сначала проверяет Catalog по SHA;
2. если address существует, он используется без обращения к provider;
3. если SHA новый, provider делает exact lookup по `FormatName`;
4. отсутствие mapping завершается fail-closed `InvalidDataException`;
5. hidden `.bin` fallback отсутствует в production resolver и в low-level `ForCustomBinary` / `StoreCustomBinaryAsync` API: extension является обязательным параметром;
6. canonical extension участвует в создании первого relative path и затем фиксируется Catalog address.

Этот contract не включает custom-format UI. Текущий первичный global-policy editor по-прежнему показывает только standard formats. Будущий discovered-format/application-policy UI должен сначала иметь явное extension решение до включения custom binary capture.

App-level composition теперь создаёт `SqliteCustomBinaryFormatConfigurationRepository` и `RepositoryClipboardCustomBinaryFileExtensionProvider` из той же active protected session и передаёт provider в `ProtectedClipboardDeliveryServices.TryCreateAsync`. Это только inert graph composition; worker и `ProcessNextAsync` пока не запускаются.

## Migration

Latest Current schema — v6; Catalog остаётся v2.

Migration `Current v5 → v6`:

1. до mutation валидируется вся Current/Catalog pair;
2. в Current проверяются identity, application-policy, history и global-policy schemas;
3. в отдельной transaction создаётся пустая `CustomBinaryFormatConfiguration`;
4. `DatabaseIdentity.SchemaVersion` и `PRAGMA user_version` обновляются `5 → 6`;
5. cancellation проверяется перед COMMIT.

Ошибка или отмена оставляет полноценный v5 и позволяет повторить migration. Existing global policy, history, identities, per-application policies и Catalog addresses не переписываются.

Новый storage создаётся сразу Current v6 / Catalog v2 с пустой custom-binary configuration table.

## Проверки

Storage tests покрывают:

- missing mapping и fail-closed provider;
- normalization и exact/ordinal lookup;
- сохранность mapping через новую protected session;
- запрет rebind;
- invalid/missing extensions;
- caller/lock/dispose cancellation;
- connection cleanup;
- rollback при отмене внутри initialization и последующий retry;
- malformed persisted extension;
- v5→v6 migration, сохранность global policy и пустую новую table;
- invalid Catalog before mutation;
- migration SQL failure/cancellation rollback и retry.

Windows feature Build #139 подтвердил x64 scope, Restore, Build и Test после app-composition и low-level fallback hardening. Official main Build и Native SQLCipher должны подтверждаться заново после продвижения final tree в `main`.
