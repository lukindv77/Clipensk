# Current database schema evolution

## Version ownership

Schema versions принадлежат конкретной роли БД, а не всему storage pair как одному числу.

Текущее состояние после этого migration tranche:

- `current.db`: schema version **4**;
- `storage-catalog.db`: schema version **1**.

`storage-catalog.db` остаётся rebuildable accelerator и не является source of truth для application identity, индивидуальных capture policies или clipboard history.

## Current v2 — durable application identity

Current schema v2 добавила durable application identity registry:

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

SQLite default `BINARY` comparison соответствует текущему fail-closed identity contract: executable path alias сравнивается как exact observed string. Более широкая path canonicalization не вводится migration.

## Current v3 — per-application capture policies

Current schema v3 добавила индивидуальные policy overrides, привязанные к durable `ApplicationId`:

- `ApplicationCapturePolicy`
  - `ApplicationId` — primary key и FK на `ApplicationIdentity(ApplicationId)`;
  - `CaptureRule` — exact enum string `Inherit`, `Allow` или `Deny`;
  - удаление `ApplicationIdentity` каскадно удаляет policy;
- `ApplicationFormatCapturePolicy`
  - `ApplicationId`;
  - `FormatName` — непустое точное имя clipboard format;
  - `CaptureRule` — `Inherit`, `Allow` или `Deny`;
  - `MaxBytes` — nullable, но при наличии строго больше нуля;
  - primary key `(ApplicationId, FormatName)`;
  - FK на `ApplicationCapturePolicy(ApplicationId)` с `ON DELETE CASCADE`.

Global capture policy **не** seed-ится схемой и не получает скрытого значения по умолчанию. `SqliteClipboardCapturePolicyRepository` принимает global policy явной constructor dependency и читает из Current только per-application overrides.

`ApplicationId`, а не PID, HWND, executable path или AUMID, является durable FK для policy data.

## Current v4 — clipboard history storage contract

Current schema v4 добавляет durable event/payload representation, определённую в `CLIPBOARD_HISTORY_SCHEMA.md`:

- `ClipboardHistoryEvent` — event-time envelope, nullable durable source `ApplicationId` и runtime source snapshot metadata;
- `ClipboardHistoryPayload` — ordered payload rows с canonical byte count, inline canonical representation либо external content address;
- FK Event → Payload использует `ON DELETE CASCADE`;
- FK ApplicationIdentity → Event использует `ON DELETE SET NULL`, чтобы удаление identity не удаляло историю;
- индексы по calendar date/time, source application/time и format name.

Schema v4 **не** запускает capture worker и сама не определяет external payload `firstStoredDate`/dedup lifecycle. Это остаётся responsibility будущего persistence sink/index contract.

## New storage initialization

Новый storage создаётся staging-парой:

1. `current.db` создаётся сразу как v4 вместе с identity, application-policy и clipboard-history tables;
2. `storage-catalog.db` создаётся как v1;
3. обе БД полностью валидируются;
4. только после этого staging `Current` перемещается на финальный путь.

## Resumable legacy migration

Проверка всей пары выполняется до mutation Current: Catalog v1 должен быть успешно открыт и подтверждён до начала schema changes.

Migration разбита на отдельные транзакционные шаги.

### Current v1 → v2

1. создать `ApplicationIdentity` и `ApplicationIdentityAlias`;
2. обновить `DatabaseIdentity.SchemaVersion` с 1 до 2;
3. выставить `PRAGMA user_version = 2`;
4. commit.

Если transaction не commit-ится, Current остаётся v1.

### Current v2 → v3

1. строго валидировать существующую identity schema v2;
2. создать `ApplicationCapturePolicy` и `ApplicationFormatCapturePolicy`;
3. обновить `DatabaseIdentity.SchemaVersion` с 2 до 3;
4. выставить `PRAGMA user_version = 3`;
5. commit.

Если шаг migration не commit-ится, Current остаётся полноценным v2.

### Current v3 → v4

1. строго валидировать identity schema и application-policy schema v3;
2. создать `ClipboardHistoryEvent` и `ClipboardHistoryPayload` вместе с требуемыми FK/index constraints;
3. обновить `DatabaseIdentity.SchemaVersion` с 3 до 4;
4. выставить `PRAGMA user_version = 4`;
5. commit.

Если этот шаг не commit-ится, Current остаётся полноценным v3. Policy и identity rows не пересоздаются и не теряются.

Для legacy пары v1/v1 последовательность выполняется как отдельные durable шаги `v1 → v2 → v3 → v4`, а не как одна неразличимая mutation.

## Fail-closed validation

Одного `SchemaVersion` недостаточно.

- Current v2+ обязан иметь точную identity table/PK/FK/index shape.
- Current v3+ дополнительно обязан иметь точную application-policy table/PK/FK shape.
- Current v4+ дополнительно обязан иметь clipboard-history table/PK/FK/index shape.
- `DatabaseIdentity.SchemaVersion` и `PRAGMA user_version` должны совпадать.
- malformed schema не принимается только потому, что таблицы имеют правильные имена.

Repositories не создают schema лениво. Schema creation/migration принадлежит `ProtectedStorageDatabaseService` до установления рабочего protected storage lifecycle.

`SqliteApplicationIdentityRepository` работает с Current v2 и более поздними версиями при сохранении identity schema contract. `SqliteClipboardCapturePolicyRepository` требует Current v3 или более позднюю совместимую схему. Будущий history repository/sink должен требовать Current v4 или более позднюю совместимую схему.
