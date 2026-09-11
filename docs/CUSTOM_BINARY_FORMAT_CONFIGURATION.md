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
- `InitializeAsync(formatName, fileExtension, token)` — атомарное первое назначение отдельного mapping.

`SqliteCustomBinaryFormatConfigurationRepository` работает только через активную `ProtectedStorageSessionLease`, проверяет StorageId, роль Current, schema/user version и table shape. Read использует ReadOnly; initialization — ReadWrite + immediate transaction.

Повторное `InitializeAsync` для уже существующего exact `FormatName` запрещено, даже если передано то же расширение. Rebind/update требует отдельного cleanup contract, потому что существующая история и external payload addresses уже могут ссылаться на прежнее расширение.

Caller cancellation и session cancellation объединяются. Отмена/ошибка до COMMIT откатывает initialization. После успешного COMMIT нет late cancellation, превращающего состоявшуюся durable запись в ошибку.

## Атомарная первичная настройка policy + mappings

JournalWindow не выполняет первичную global policy и custom mappings отдельными durable writes. Для UI используется `SqliteInitialClipboardCaptureConfigurationService`, который принимает:

- полностью валидированную initial `ClipboardCapturePolicy`;
- список explicit `InitialCustomBinaryFormatConfiguration` для разрешённых custom formats;
- ту же active `ProtectedStorageSessionLease` и cancellation boundary.

Service открывает Current v6 один раз, валидирует identity/user_version и обе schema families, начинает immediate transaction и требует, чтобы `GlobalCapturePolicy`, `GlobalFormatCapturePolicy` и `CustomBinaryFormatConfiguration` ещё не содержали durable initial-configuration rows. Затем в **одной transaction** записываются global header, format rules и custom extension mappings.

Если mapping невалиден, уже существует partial initial configuration, SQL insert завершается ошибкой либо cancellation приходит до COMMIT, global policy и новые mappings не остаются частично сохранёнными. После успешного COMMIT операция остаётся успешной при late cancellation.

Custom mapping разрешён aggregate service только для exact format, который присутствует в переданной policy с explicit `Allow`. `WaveAudio`, `RiffAudio` и `FileContents` блокируются до открытия БД. Duplicate exact format names/mappings отклоняются; silent normalization самого `FormatName` нет.

Individual repository остаётся доступным как низкоуровневый first-write boundary, но product initial-setup UI использует aggregate service, чтобы не создавать невосстановимый split state между first-write-only policy и extension configuration.

## Extension provider

`RepositoryClipboardCustomBinaryFileExtensionProvider` адаптирует repository к существующему `IClipboardCustomBinaryFileExtensionProvider`.

Для нового custom binary payload:

1. resolver сначала проверяет Catalog по SHA;
2. если address существует, он используется без обращения к provider;
3. если SHA новый, provider делает exact lookup по `FormatName`;
4. отсутствие mapping завершается fail-closed `InvalidDataException`;
5. hidden `.bin` fallback отсутствует в production resolver и в low-level `ForCustomBinary` / `StoreCustomBinaryAsync` API: extension является обязательным параметром;
6. canonical extension участвует в создании первого relative path и затем фиксируется Catalog address.

## Initial custom-format UI

Первичный global-policy editor теперь позволяет пользователю **явно** добавлять custom binary rows. Никакие discovered/private formats не добавляются и не включаются автоматически.

Для каждой добавленной строки пользователь задаёт exact `FormatName` и explicit `Allow`/`Deny`. Для `Allow` обязательны:

- canonicalizable file extension;
- explicit positive `MaxBytes` либо explicit unlimited.

Для `Deny` extension mapping не создаётся и `MaxBytes` не задаётся. Имена custom formats участвуют в том же ordinal uniqueness contract, что и standard format rows; попытка повторить standard/custom exact name отклоняется до durable write.

После reload read-only summary показывает extension для каждого non-standard allowed format. Если policy была создана старым/ручным путём без mapping, UI показывает отсутствие mapping как fail-closed состояние; оно не заменяется `.bin` или эвристикой.

Раздел «Приложения» показывает для выбранного `ApplicationId` persisted runtime-discovered exact `FormatName` как read-only список. Discovery не добавляет format в policy и не включает его автоматически. Storage maintenance уже умеет атомарно публиковать **новые** exact `FormatName → FileExtension` mappings вместе с изменением application policy, но product UI для enable discovered format к этому contract пока не подключён. Rebind/update/delete mapping остаются отдельным cleanup contract.

## Application policy maintenance + mappings

`ProtectedCurrentApplicationPolicyMaintenanceService` имеет backward-compatible legacy path и mapping-aware overload с `ApplicationCustomBinaryFormatConfiguration`.

Mapping-aware path до mutation:

- требует exact non-empty `FormatName` без silent normalization;
- блокирует prohibited clipboard formats через capture guard;
- требует, чтобы соответствующее правило application policy было explicit `Allow`;
- нормализует extension через `ExternalPayloadAddressFactory.NormalizeCustomBinaryExtension`;
- отклоняет duplicate exact mapping names;
- разрешает reuse уже существующего mapping только при том же canonical extension;
- отклоняет попытку rebind на другое extension без mutation.

После validation service открывает Current и в **одной immediate transaction** публикует новые mappings, application policy, относящийся к Current cleanup и durable `PendingPolicyMaintenance` marker. Поэтому failure/cancellation до COMMIT не оставляет split state между mapping, policy, cleanup и marker.

Legacy operation сохраняет v1 marker и policy-only fingerprint. Mapping-aware operation создаёт v2 marker с отдельным `customBinaryConfigurationFingerprint`, рассчитанным по полному storage-wide mapping snapshot после планируемых inserts. Смысл существующего `policyFingerprint` не меняется, поэтому уже записанные v1 markers остаются совместимыми.

Continuation phases Archive → Catalog → Trash → Completion сохраняют versioned state и fingerprint при сериализации. Exact mapping-aware retry сравнивает application target, policy fingerprint, marker version и custom-binary configuration fingerprint; legacy retry не считается тем же v2 request.

Этот contract только безопасно добавляет отсутствующие mappings. Он намеренно не предоставляет rebind/update/delete mapping и не делает discovered formats автоматически разрешёнными.

## App composition и runtime

Production App создаёт `SqliteCustomBinaryFormatConfigurationRepository` и `RepositoryClipboardCustomBinaryFileExtensionProvider` из той же active protected session и передаёт provider в `ProtectedClipboardDeliveryServices.TryCreateAsync`.

После non-null composition App запускает exact-session single-reader worker, а Windows clipboard listener включается только после ready-reader gate. Поэтому custom extension lookup остаётся lazy до фактического нового custom-binary payload, но mapping уже durable до запуска runtime после initial setup.

После успешного aggregate setup JournalWindow отправляет App post-COMMIT notification; App пересобирает protected composition для той же active session. Ошибка callback не демотирует committed initial configuration.

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
- rollback при отмене внутри individual initialization и последующий retry;
- malformed persisted extension;
- v5→v6 migration, сохранность global policy и пустую новую table;
- invalid Catalog before mutation;
- migration SQL failure/cancellation rollback и retry;
- aggregate initial setup policy + mapping;
- rollback всей aggregate transaction при injected custom-mapping insert failure;
- отказ от aggregate setup поверх partial existing mapping без записи policy;
- validation prohibited/custom-not-allowed mappings до открытия БД;
- atomic application-policy maintenance + normalized mapping + v2 marker;
- rollback mapping/policy/marker одной transaction при injected failure;
- same-extension reuse, different-extension rebind rejection и exact mapping-aware retry;
- полный v2 Resume flow через Archive → Catalog → Trash → Completion с очисткой marker и сохранением durable mapping.

Текущий application custom-format maintenance tranche затрагивает `src/Clipensk.Storage/**`; feature Build обязан подтверждать Restore/Build/Test на exact feature SHA. После продвижения final tree обязательны exact-main Build и Native SQLCipher, потому что native workflow path scope включает `src/Clipensk.Storage/**`.

Manual WinUI/real-clipboard smoke остаётся отдельным UNVERIFIED evidence.
