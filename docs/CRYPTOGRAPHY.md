# Криптографический профиль Clipensk

Статус: **частично реализовано; production native SQLCipher delivery ещё NOT READY**.

Дата решения: 4 сентября 2026 года.

Этот документ является authoritative для password → MasterKey lifecycle и защищённого SQLite boundary. Он уточняет более ранние формулировки в `REQUIREMENTS.md` и `ARCHITECTURE.md`.

## 1. Storage-wide MasterKey

Все защищённые БД одного хранилища Clipensk используют один 256-битный MasterKey.

MasterKey:

- нигде не сохраняется в открытом виде;
- выводится из введённого пользователем пароля;
- после успешной разблокировки удерживается только в памяти процесса;
- при завершении защищённой сессии его управляемый буфер очищается best-effort.

Пароль пользователя нигде не сохраняется.

## 2. KDF profile v1

Для нового хранилища зафиксирован Argon2id v1.3 (`0x13`) со следующими параметрами:

- memory cost: `65,536 KiB` (64 MiB);
- iterations/time cost: `3`;
- parallelism/lanes: `4`;
- salt: `16 bytes` (128 bit), криптографически случайный и уникальный для storage;
- MasterKey output: `32 bytes` (256 bit).

Это соответствует second recommended profile из RFC 9106 для Argon2id с ограниченной памятью. Параметры хранятся вместе с crypto metadata, чтобы формат можно было версионировать и мигрировать.

Пароль преобразуется в UTF-8 без дополнительной Unicode-нормализации: последовательность символов должна совпадать с введённой пользователем.

## 3. storage-crypto.json

После выбора DataRoot и первого успешного задания пароля в корне DataRoot создаётся незашифрованный файл:

`storage-crypto.json`

Он содержит только технические данные, необходимые для повторного вывода и проверки MasterKey:

- schema version;
- StorageId;
- algorithm name;
- KDF profile/parameters;
- salt;
- MasterKey length;
- verifier;
- `StorageInitialized` — durable marker того, что initial protected Current/Catalog pair уже была успешно создана и проверена.

Файл **не содержит**:

- пароль;
- MasterKey;
- clipboard payload;
- историю.

Первичное создание выполняется через temporary file + atomic move без overwrite существующего metadata. Обновление `StorageInitialized` также выполняется через temp-file + atomic replace и только после успешной проверки защищённых БД.

Повреждённый или неподдерживаемый `storage-crypto.json` не считается новой установкой и не перезаписывается автоматически: приложение остаётся LOCKED.

## 4. MasterKey verifier

До обращения к БД введённый пароль быстро проверяется через storage-wide verifier:

`HMAC-SHA256(MasterKey, "Clipensk.MasterKeyVerifier.v1\\0" || StorageId)`

Сравнение выполняется constant-time.

Verifier не является достаточным условием `UNLOCKED` и не считается доказательством доступности истории. Окончательный unlock выполняется только после успешного открытия и проверки protected database identity тем же MasterKey.

## 5. SQLCipher integration boundary

Выбран production boundary:

- managed SQLite API: `Microsoft.Data.Sqlite.Core`;
- SQLitePCLRaw provider: `SQLitePCLRaw.provider.sqlcipher`;
- native library name, ожидаемый provider: `sqlcipher` / `sqlcipher.dll`;
- native SQLCipher: самостоятельно собираемая из исходников SQLCipher Community Edition;
- deprecated public bundles с bundled `e_sqlcipher` binary не используются.

Минимально поддерживаемая runtime-версия SQLCipher: `4.12.0`, потому что protected handle обязан подтверждаться через `PRAGMA cipher_status`.

Native x64/ARM64 build, provenance, reproducible-build pipeline, packaging и release delivery `sqlcipher.dll` пока не реализованы. Поэтому отсутствие native SQLCipher в development build является ожидаемым fail-closed состоянием, а не поводом открывать БД обычным SQLite.

## 6. Передача raw MasterKey в SQLCipher

MasterKey не передаётся SQLCipher как пользовательский пароль для повторного PBKDF2.

Для SQLCipher формируется raw-key representation:

`x'<64 hex characters>'`

Она передаётся через `sqlite3_key` до первой операции, читающей страницы БД.

После keying применяются:

- `PRAGMA cipher_compatibility = 4`;
- `PRAGMA cipher_memory_security = ON`.

Затем runtime обязан проверить:

1. `PRAGMA cipher_version` — версия не ниже `4.12.0`;
2. `PRAGMA cipher_status` — значение `1`;
3. фактическое чтение БД через `SELECT count(*) FROM sqlite_master`;
4. `PRAGMA quick_check`;
5. self-describing `DatabaseIdentity`.

Если любой шаг не проходит, приложение остаётся LOCKED.

## 7. Initial protected databases

Первый implemented storage foundation создаёт только initial pair:

```text
<DataRoot>\
  Current\
    current.db
    storage-catalog.db
```

Создание pair выполняется в staging-каталоге. В final `Current` пара публикуется только после создания и повторной проверки обеих БД. Состояние, в котором существует только один файл пары, считается `MissingOrPartialStorage` и не чинится автоматическим созданием второго файла поверх неизвестного состояния.

Если Current/Catalog отсутствуют, но уже найден `Archive/archive_*.db`, автоматическая initial initialization запрещена: требуется отдельный recovery/catalog-rebuild workflow.

После подтверждённой initial pair создаются пустые каталоги `Archive`, `Files`, `Trash`, `Languages`.

## 8. DatabaseIdentity v1

Каждая initial protected DB содержит singleton-таблицу `DatabaseIdentity`:

- StorageId;
- DatabaseId;
- DatabaseRole;
- SchemaVersion;
- EncryptionVersion;
- CreatedAtUtc;
- nullable archive family/coverage fields.

Для `current.db` роль — `Current`, для `storage-catalog.db` — `StorageCatalog`.

При каждом protected open проверяются:

- exactly one identity row;
- StorageId совпадает с `storage-crypto.json`;
- DatabaseId корректен и не пуст;
- роль соответствует физическому файлу;
- schema/encryption version поддерживаются;
- Current/Catalog не содержат archive coverage;
- `PRAGMA user_version` совпадает со schema version.

## 9. Unlock protocol

Актуальный порядок:

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

Реализовано:

- временный UTF-8 `byte[]` пароля очищается через `CryptographicOperations.ZeroMemory`;
- MasterKey находится в `MasterKeyLease`;
- `MasterKeyLease.Dispose()` очищает принадлежащий ему буфер;
- при ошибочном пароле производный candidate key очищается;
- verifier buffers очищаются после использования;
- временный SQLCipher raw-key representation очищается после `sqlite3_key`;
- SQLCipher connection включает `cipher_memory_security=ON`.

Ограничение WinUI/.NET:

`PasswordBox.Password` возвращает managed `string`. Приложение сразу очищает поле UI и не сохраняет строку, однако гарантированно физически стереть уже созданный immutable managed string из памяти CLR невозможно. Это ограничение нельзя выдавать за полную secure-memory гарантию.

## 11. Что ещё не реализовано

Отдельными задачами остаются:

- native SQLCipher x64/ARM64 build automation и reproducible/provenance verification;
- packaging/доставка `sqlcipher.dll` вместе с приложением;
- production CI, реально открывающий SQLCipher-encrypted DB на Windows x64/ARM64;
- таблицы фактической clipboard history в `current.db`;
- schema для производного catalog index;
- Archive schema и создание/чтение archive databases;
- миграции schema/encryption version;
- смена пароля и/или MasterKey;
- recovery procedure при потере или повреждении crypto metadata;
- recovery для partial Current/Catalog и catalog rebuild;
- auto-lock runtime и уничтожение всех storage handles при lock.

Текущие unit tests с обычным SQLite проверяют layout/schema/DatabaseIdentity state machine. Они **не являются доказательством SQLCipher encryption**. До отдельного Windows integration evidence нельзя объявлять production encrypted storage полностью готовым.
