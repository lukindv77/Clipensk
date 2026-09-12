# Current database schema evolution

## Version ownership

Schema versions принадлежат конкретной роли БД, а не всей storage pair как одному числу.

Текущее production состояние:

- `current.db`: schema version **9**;
- `storage-catalog.db`: schema version **3**;
- Archive DB: schema version **1**.

`storage-catalog.db` остаётся rebuildable accelerator. Он не является source of truth для application identity, capture policies, clipboard history, custom-binary extension configuration, pending policy maintenance, discovered application formats, pending archive split state или assigned Archive coverage.

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

Current v6 добавила `CustomBinaryFormatConfiguration`, описанную в `CUSTOM_BINARY_FORMAT_CONFIGURATION.md`:

- `FormatName TEXT NOT NULL PRIMARY KEY` — exact registered/private clipboard format name;
- `FileExtension TEXT NOT NULL` — canonical physical extension для нового custom-binary content address.

Mapping storage-scoped и хранится только в зашифрованном Current. Catalog сохраняет уже назначенный SHA → relative path, но не является источником configuration.

Repository выполняет exact/BINARY lookup и атомарное первое назначение. Повторный initialize/rebind запрещён без отдельного cleanup contract. Missing mapping для нового custom-binary SHA является fail-closed; скрытый `.bin` production resolver не использует.

## Current v7 — durable pending policy-maintenance marker

Current v7 добавила `PendingPolicyMaintenance` — singleton durable marker для resumable policy cleanup/mutation workflow.

Таблица создаётся **пустой**. Schema не seed-ит maintenance state и сама не меняет policy/history/Catalog/external files. Presence marker означает незавершённую policy maintenance; protected delivery composition fail-closed до explicit recovery/resume.

Durable marker дополняет runtime quiescence: crash/reopen не должен позволять resident capture продолжить работу по частично изменённой persisted policy.

## Current v8 — per-application discovered clipboard formats

Current v8 добавила `ApplicationDiscoveredFormat`:

- `ApplicationId TEXT NOT NULL`;
- `FormatName TEXT NOT NULL`;
- `FirstSeenAtUtc TEXT NOT NULL`;
- `LastSeenAtUtc TEXT NOT NULL`;
- composite PK `(ApplicationId, FormatName)`;
- FK `ApplicationId → ApplicationIdentity(ApplicationId) ON DELETE CASCADE`.

Это storage-scoped durable observation state для уже обнаруженных clipboard format names у конкретной application identity. Schema не создаёт application identities и не seed-ит format rows.

## Current v9 — durable pending archive-split state

Current v9 добавила crash-recovery foundation для Archive Split из `ARCHIVE_SPLIT_PROTOCOL.md`.

`PendingArchiveSplit` хранит ровно одну активную операцию:

- `OperationId`;
- source archive filename и `SourceDatabaseId`;
- source coverage start/end;
- durable phase;
- `CreatedAtUtc`.

`PendingArchiveSplitSegment` хранит immutable ordered result plan:

- `(OperationId, SegmentOrder)` как PK;
- canonical result `FileName`;
- result `DatabaseId`;
- coverage start/end;
- уникальность filename и DatabaseId внутри операции;
- FK на operation с `ON DELETE CASCADE`.

Допустимые persisted phases идут строго по порядку:

1. `Planned`;
2. `ReadyToPublish`;
3. `PhysicalPublished`;
4. `CatalogPublished`.

`SqlitePendingArchiveSplitRepository` валидирует Current identity/schema, canonical archive filenames, exact partition/ordering, operation ownership и разрешает phase advance только на один шаг. Marker можно очистить только после `CatalogPublished`.

Schema v9 и repository **не реализуют** сам split planner, shadow DB build, filesystem publication или Catalog switch; это последующие slices.

## New storage initialization

Новая storage pair создаётся staging-операцией:

1. `current.db` создаётся сразу как v9 со всеми Current v2-v9 contracts, включая пустые `PendingPolicyMaintenance`, `ApplicationDiscoveredFormat`, `PendingArchiveSplit` и `PendingArchiveSplitSegment`;
2. `storage-catalog.db` создаётся сразу как v3 с `ExternalPayloadAddressIndex` и пустой `ArchiveSegmentIndex`;
3. обе БД полностью валидируются;
4. только затем staging `Current` перемещается на final path.

Policy, custom-binary mappings, pending maintenance, discovered formats и pending archive split state не seed-ятся defaults. Catalog archive projection не строится из догадок: она восстанавливается/обновляется отдельно из authoritative Archive DB state.

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

### Current v6 → v7

1. валидировать identity, application-policy, history, global-policy и custom-binary schemas;
2. создать пустую `PendingPolicyMaintenance`;
3. version `6 → 7` и `user_version = 7`;
4. cancellation check и COMMIT.

### Current v7 → v8

1. валидировать все Current v7 contracts;
2. создать пустую `ApplicationDiscoveredFormat`;
3. version `7 → 8` и `user_version = 8`;
4. cancellation check и COMMIT.

### Current v8 → v9

1. валидировать все Current v8 contracts;
2. создать пустые `PendingArchiveSplit` и `PendingArchiveSplitSegment`;
3. version `8 → 9` и `user_version = 9`;
4. cancellation check и COMMIT.

Existing identity/application policy, history, global policy, custom-binary mappings, pending policy-maintenance state, discovered formats и Catalog rows не переписываются соответствующими последующими migration steps.

### Catalog v1 → v2 → v3

Catalog мигрирует отдельно согласно `STORAGE_CATALOG_SCHEMA.md`:

- v1 → v2 создаёт `ExternalPayloadAddressIndex`;
- v2 → v3 сначала валидирует v2 contract, затем создаёт `ArchiveSegmentIndex` и его indexes;
- каждый шаг отдельно меняет `DatabaseIdentity.SchemaVersion` и `PRAGMA user_version`;
- v1 не перепрыгивает прямо в v3.

Ошибка/отмена до COMMIT оставляет полноценную предыдущую schema version, поэтому следующий unlock может повторить конкретный step.

Для legacy pair v1/v1 Current выполняет `1→2→3→4→5→6→7→8→9`, Catalog — `1→2→3`; durable boundaries не схлопываются.

## Fail-closed validation

Одного `SchemaVersion` недостаточно.

- Current v2+ обязан иметь identity contract.
- Current v3+ дополнительно application-policy contract.
- Current v4+ clipboard-history contract.
- Current v5+ global-policy contract.
- Current v6+ custom-binary configuration contract.
- Current v7+ pending policy-maintenance contract.
- Current v8+ application-discovered-format contract.
- Current v9+ pending archive-split operation/segment contracts.
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
- pending-maintenance composition/repository: Current v7+;
- `SqliteApplicationDiscoveredFormatRepository`: Current v8+;
- `SqlitePendingArchiveSplitRepository`: Current v9+;
- `SqliteExternalPayloadAddressIndex`: Catalog v2+;
- `ProtectedArchiveSegmentCatalog`: Catalog v3 + active Current history schema.

## Acceptance evidence for Current v9

Current v9 + pending Archive Split marker was promoted to `main` at storage baseline `08d23672a75f85ca61c2ad62ae57395f2eadfdbc` and accepted on that exact SHA by:

- Build #408, run `34709397765`: **SUCCESS**;
- Native SQLCipher #73, run `34709397774`: **SUCCESS**, including pinned SQLCipher x64 build/provenance, encrypted-storage verification and published-runtime SQLCipher loading.

Later docs-only descendants do not change that storage acceptance evidence.