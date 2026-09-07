# Current database schema evolution

## Version ownership

Schema versions принадлежат конкретной роли БД, а не всей storage pair как одному числу.

Текущее production состояние:

- `current.db`: schema version **6**;
- `storage-catalog.db`: schema version **3**;
- Archive DB: schema version **1**.

`storage-catalog.db` остаётся rebuildable accelerator. Он не является source of truth для application identity, capture policies, clipboard history, custom-binary extension configuration или assigned Archive coverage.

## Current v2 — durable application identity

Current v2 добавила Clipensk-owned durable application identity:

- `ApplicationIdentity(ApplicationId, CreatedAtUtc)`;
- `ApplicationIdentityAlias(AliasType, AliasValue, ApplicationId, CreatedAtUtc)`;
- exact alias uniqueness и FK `ON DELETE CASCADE`.

`ApplicationId`, а не PID/HWND/path/AUMID, является durable FK для policy/history boundaries. AUMID и executable path остаются resolution aliases/evidence.

## Current v3 — per-application capture policies

Current v3 добавила:

- `ApplicationCapturePolicy` с exact `Inherit` / `Allow` / `Deny`;
- `ApplicationFormatCapturePolicy` с exact `FormatName`, rule и nullable положительным `MaxBytes`;
- PK/FK привязку к durable `ApplicationId`.

Global policy не seed-ится этой схемой и не получает hidden defaults.

## Current v4 — clipboard history

Current v4 добавила durable history contract из `CLIPBOARD_HISTORY_SCHEMA.md`:

- `ClipboardHistoryEvent` — event-time envelope и source snapshot;
- `ClipboardHistoryPayload` — ordered canonical payload rows;
- inline text/link/storage-items representation либо external content address;
- Event → Payload `ON DELETE CASCADE`;
- ApplicationIdentity → Event `ON DELETE SET NULL`;
- индексы по persisted calendar/time/source/format keys.

Schema v4 сама не запускает worker и не определяет lifecycle внешних файлов.

## Current v5 — storage-scoped global capture policy

Current v5 добавила `GlobalCapturePolicy` и `GlobalFormatCapturePolicy`, описанные в `GLOBAL_CAPTURE_POLICY.md`.

Таблицы создаются пустыми. Отсутствующая policy отличается от explicit Deny. Repository поддерживает nullable read и атомарную первичную initialization; update/delete требуют отдельного cleanup lifecycle.

## Current v6 — custom-binary file-extension configuration

Current v6 добавляет `CustomBinaryFormatConfiguration`, описанную в `CUSTOM_BINARY_FORMAT_CONFIGURATION.md`:

- `FormatName TEXT NOT NULL PRIMARY KEY` — exact registered/private clipboard format name;
- `FileExtension TEXT NOT NULL` — canonical physical extension для нового custom-binary content address.

Mapping storage-scoped и хранится только в зашифрованном Current. Catalog сохраняет уже назначенный SHA → relative path, но не является источником configuration.

Repository выполняет exact/BINARY lookup и атомарное первое назначение. Повторный initialize/rebind запрещён без отдельного cleanup contract. Missing mapping для нового custom-binary SHA является fail-closed; скрытый `.bin` production resolver не использует.

## New storage initialization

Новая storage pair создаётся staging-операцией:

1. `current.db` создаётся сразу как v6 с identity, application-policy, history, global-policy и custom-binary configuration tables;
2. `storage-catalog.db` создаётся сразу как v3 с `ExternalPayloadAddressIndex` и пустой `ArchiveSegmentIndex`;
3. обе БД полностью валидируются;
4. только затем staging `Current` перемещается на final path.

Policy и custom-binary mappings не seed-ятся defaults. Catalog archive projection также не seed-ится из догадок: она строится отдельно из authoritative Archive DB.

## Resumable legacy migration

Вся Current/Catalog pair проверяется **до** mutation. Catalog v1/v2/v3 должен успешно открыться и подтвердить identity/schema contract перед migration Current или Catalog.

Каждый migration step имеет отдельную transaction и durable version boundary.

### Current v1 → v2

1. создать identity tables;
2. `DatabaseIdentity.SchemaVersion: 1 → 2`;
3. `PRAGMA user_version = 2`;
4. COMMIT.

### Current v2 → v3

1. валидировать identity v2;
2. создать application-policy tables;
3. version `2 → 3` и `user_version = 3`;
4. COMMIT.

### Current v3 → v4

1. валидировать identity + application-policy schemas;
2. создать history tables/indexes;
3. version `3 → 4` и `user_version = 4`;
4. COMMIT.

### Current v4 → v5

1. валидировать identity, application-policy и history schemas;
2. создать пустые global-policy tables;
3. version `4 → 5` и `user_version = 5`;
4. cancellation check и COMMIT.

### Current v5 → v6

1. валидировать identity, application-policy, history и global-policy schemas;
2. создать пустую `CustomBinaryFormatConfiguration`;
3. version `5 → 6` и `user_version = 6`;
4. cancellation check и COMMIT.

Existing global policy, identity/application policy, history и Catalog rows не переписываются.

### Catalog v1 → v2 → v3

Catalog мигрирует отдельно согласно `STORAGE_CATALOG_SCHEMA.md`:

- v1 → v2 создаёт `ExternalPayloadAddressIndex`;
- v2 → v3 сначала валидирует v2 contract, затем создаёт `ArchiveSegmentIndex` и его indexes;
- каждый шаг отдельно меняет `DatabaseIdentity.SchemaVersion` и `PRAGMA user_version`;
- v1 не перепрыгивает прямо в v3.

Ошибка/отмена до COMMIT оставляет полноценную предыдущую schema version, поэтому следующий unlock может повторить конкретный шаг.

Для legacy pair v1/v1 Current выполняет `1→2→3→4→5→6`, Catalog — `1→2→3`; эти durable boundaries не схлопываются.

## Fail-closed validation

Одного `SchemaVersion` недостаточно.

- Current v2+ обязан иметь identity contract.
- Current v3+ дополнительно application-policy contract.
- Current v4+ clipboard-history contract.
- Current v5+ global-policy contract.
- Current v6+ custom-binary configuration contract.
- Catalog v2+ обязан иметь external-payload address contract.
- Catalog v3+ дополнительно обязан иметь archive-segment projection table/index contract.
- `DatabaseIdentity.SchemaVersion` и `PRAGMA user_version` должны совпадать.
- malformed schema/data не принимаются только потому, что version number совпадает.

Repositories schema не создают и не мигрируют. Это принадлежит `ProtectedStorageDatabaseService` до рабочего protected storage lifecycle.

Совместимость repository boundaries:

- `SqliteApplicationIdentityRepository`: Current v2+;
- `SqliteClipboardCapturePolicyRepository`: Current v3+;
- history repository/sink: Current v4+;
- `SqliteGlobalClipboardCapturePolicyRepository`: Current v5+;
- `SqliteCustomBinaryFormatConfigurationRepository`: Current v6+;
- `SqliteExternalPayloadAddressIndex`: Catalog v2+;
- `ProtectedArchiveSegmentCatalog`: Catalog v3 + active Current history schema.
