# Криптографический профиль Clipensk

Статус: **частично реализовано; x64 native build pipeline implemented, но production native delivery ещё NOT READY.**

Дата решения: 4 сентября 2026 года.

Этот документ является authoritative для password → MasterKey lifecycle и защищённого SQLite boundary. Детали native source build находятся в `NATIVE_SQLCIPHER_BUILD.md`.

Поддерживаемая архитектура Clipensk — только Windows x64 (AMD64). ARM64 не является product target.

## 1. Storage-wide MasterKey

Все защищённые БД одного хранилища Clipensk используют один 256-битный MasterKey.

MasterKey:

- нигде не сохраняется в открытом виде;
- выводится из введённого пользователем пароля;
- после успешной разблокировки удерживается только в памяти процесса;
- при завершении защищённой сессии его управляемый буфер очищается best-effort.

Пароль пользователя нигде не сохраняется.

## 2. KDF profile v1

Для нового хранилища зафиксирован Argon2id v1.3 (`0x13`):

- memory cost: `65,536 KiB` (64 MiB);
- iterations/time cost: `3`;
- parallelism/lanes: `4`;
- salt: `16 bytes`;
- MasterKey output: `32 bytes`.

Это соответствует second recommended profile RFC 9106 для Argon2id с ограниченной памятью. Параметры хранятся в crypto metadata для versioning/migration.

Пароль преобразуется в UTF-8 без дополнительной Unicode-нормализации.

## 3. storage-crypto.json

В корне DataRoot хранится незашифрованный `storage-crypto.json` с техническими данными:

- schema version;
- StorageId;
- algorithm/KDF profile;
- salt;
- MasterKey length;
- verifier;
- `StorageInitialized`.

Файл не содержит пароль, MasterKey, clipboard payload или историю.

Первичное создание выполняется через temp-file + atomic move без overwrite. `StorageInitialized=true` записывается только после успешного создания и проверки initial protected Current/Catalog pair.

Повреждённый или неподдерживаемый metadata не считается новой установкой и автоматически не перезаписывается.

## 4. MasterKey verifier

Перед обращением к БД введённый пароль проверяется через:

`HMAC-SHA256(MasterKey, "Clipensk.MasterKeyVerifier.v1\\0" || StorageId)`

Сравнение constant-time.

Verifier не является достаточным условием `UNLOCKED`. Окончательный unlock разрешён только после успешного открытия и проверки protected database identity тем же MasterKey.

## 5. SQLCipher integration boundary

Production boundary:

- managed API: `Microsoft.Data.Sqlite.Core`;
- provider: `SQLitePCLRaw.provider.sqlcipher`;
- native library: `sqlcipher.dll`;
- native implementation: самостоятельно собираемая SQLCipher Community Edition;
- deprecated bundled `e_sqlcipher` binaries не используются.

Минимально поддерживаемая runtime-версия SQLCipher — `4.12.0`, поскольку protected handle обязан подтвердить `PRAGMA cipher_status = 1`.

Для x64 реализован pinned source-build pipeline и отдельный native smoke workflow. Его PASS должен подтверждаться конкретным CI run; наличие workflow само по себе не является evidence.

ARM64 build/smoke не требуется, поскольку ARM64 не поддерживается продуктом. Packaging verified DLL в установленное x64-приложение остаётся отдельным delivery tranche.

## 6. Передача raw MasterKey

MasterKey не передаётся SQLCipher как пользовательский пароль для повторного PBKDF2.

Формируется raw-key representation:

`x'<64 hex characters>'`

и передаётся через `sqlite3_key` до первого чтения страниц БД.

После keying применяются:

- `PRAGMA cipher_compatibility = 4`;
- `PRAGMA cipher_memory_security = ON`.

Затем runtime проверяет:

1. `PRAGMA cipher_version >= 4.12.0`;
2. `PRAGMA cipher_status = 1`;
3. `SELECT count(*) FROM sqlite_master`;
4. `PRAGMA quick_check`;
5. `DatabaseIdentity`.

Любой failure оставляет приложение LOCKED.

## 7. Initial protected databases

Initial storage foundation создаёт:

```text
<DataRoot>\
  Current\
    current.db
    storage-catalog.db
```

Пара создаётся в staging-каталоге и публикуется в final `Current` только после повторной проверки обеих БД.

Состояние, где существует только один файл пары, считается `MissingOrPartialStorage` и не исправляется созданием второго файла поверх неизвестного состояния.

Если Current/Catalog отсутствуют, но уже найден `Archive/archive_*.db`, automatic initial initialization запрещена: нужен отдельный recovery/catalog-rebuild workflow.

После подтверждённой initial pair создаются `Archive`, `Files`, `Trash`, `Languages`.

## 8. DatabaseIdentity v1

Каждая initial protected DB содержит singleton `DatabaseIdentity`:

- StorageId;
- DatabaseId;
- DatabaseRole;
- SchemaVersion;
- EncryptionVersion;
- CreatedAtUtc;
- nullable archive family/coverage fields.

Для `current.db` роль `Current`, для `storage-catalog.db` — `StorageCatalog`.

При каждом protected open проверяются exactly one identity row, StorageId, DatabaseId, physical role, schema/encryption version, отсутствие archive coverage для Current/Catalog и совпадение `PRAGMA user_version`.

## 9. Unlock protocol

```text
LOCKED
  ↓ password
UNLOCKING
  ↓ Argon2id → candidate MasterKey
metadata verifier
  ↓
SQLCipher provider/version/cipher_status
  ↓
open Current + Catalog with MasterKey
  ↓
sqlite_master + quick_check + DatabaseIdentity
  ↓
mark StorageInitialized when needed
  ↓
UNLOCKED
```

Нельзя завершать `UNLOCKED` после одного только metadata verifier.

## 10. Sensitive memory

Реализовано best-effort:

- temporary UTF-8 password bytes zeroed;
- MasterKey owned by `MasterKeyLease` and zeroed on dispose;
- wrong candidate key zeroed;
- verifier buffers zeroed;
- temporary SQLCipher raw-key representation zeroed after `sqlite3_key`;
- SQLCipher `cipher_memory_security=ON`.

Ограничение WinUI/.NET: `PasswordBox.Password` возвращает immutable managed `string`; приложение сразу очищает UI и не сохраняет строку, но физически гарантировать её стирание из CLR memory нельзя.

## 11. Evidence boundaries

Обычный unit-test backend использует plain SQLite только для проверки layout/schema/DatabaseIdentity state machine. Его PASS **не доказывает encryption**.

Для x64 SQLCipher integration считается подтверждённой только если native workflow:

- собрал pinned native inputs;
- проверил expected exports/architecture;
- production storage service создал protected pair;
- DB files не имеют plaintext SQLite header;
- correct MasterKey повторно открывает storage;
- unrelated MasterKey не открывает storage;
- production provider возвращает `cipher_version` и `cipher_status=1`.

## 12. Что ещё не реализовано

Остаются:

- packaging/delivery verified `sqlcipher.dll` для выбранной схемы распространения;
- byte-for-byte reproducibility/provenance hardening;
- таблицы clipboard history;
- catalog index schema;
- Archive schema/creation/read path;
- migrations;
- password/MasterKey change;
- crypto-metadata recovery;
- partial Current/Catalog recovery + catalog rebuild;
- auto-lock runtime и уничтожение всех storage handles при lock.
