# Current database schema evolution

## Version ownership

Schema versions принадлежат конкретной роли БД, а не всему storage pair как одному числу.

Текущее состояние:

- `current.db`: schema version **2**;
- `storage-catalog.db`: schema version **1**.

`storage-catalog.db` остаётся rebuildable accelerator и не является source of truth для application identity.

## Current v2

Current schema v2 добавляет durable application identity registry:

- `ApplicationIdentity`
  - `ApplicationId` — Clipensk-owned GUID, primary key;
  - `CreatedAtUtc`;
- `ApplicationIdentityAlias`
  - `AliasType`: `Aumid` или `ExecutablePath`;
  - `AliasValue`;
  - `ApplicationId`;
  - `CreatedAtUtc`;
  - primary key `(AliasType, AliasValue)` обеспечивает exact-alias uniqueness;
  - FK на `ApplicationIdentity` с `ON DELETE CASCADE`;
  - index по `ApplicationId`.

SQLite default `BINARY` comparison соответствует текущему fail-closed identity contract: executable path alias сравнивается как exact observed string. Более широкая path canonicalization не вводится этой migration.

## New storage initialization

Новый storage создаётся staging-парой:

1. `current.db` создаётся сразу как v2 вместе с application identity tables;
2. `storage-catalog.db` создаётся как v1;
3. обе БД полностью валидируются;
4. только после этого staging `Current` перемещается на финальный путь.

## Legacy Current v1 migration

Для существующей пары `Current v1 / Catalog v1` порядок обязателен:

1. открыть и проверить Current v1 без mutation;
2. открыть и проверить Catalog v1;
3. только если **обе** проверки успешны, открыть Current на запись;
4. в одной SQLite transaction создать v2 identity tables, обновить `DatabaseIdentity.SchemaVersion` и `PRAGMA user_version` до 2;
5. commit;
6. повторно валидировать Current как v2, включая точную table/PK/FK/index shape.

Если Catalog невалиден, migration Current не начинается. Если transaction не commit-ится, legacy Current остаётся v1.

## Fail-closed validation

Недостаточно только выставить `SchemaVersion=2`. Current v2 обязан иметь ожидаемую структуру `ApplicationIdentity` и `ApplicationIdentityAlias`.

Repository не создаёт schema лениво. `SqliteApplicationIdentityRepository` принимает только уже валидный Current v2, связанный с активным `ProtectedStorageSessionLease` и тем же `StorageId`.
