# Криптографический профиль Clipensk

Статус: **частично реализовано**.

Дата решения: 4 сентября 2026 года.

Этот документ является authoritative для текущего password → MasterKey lifecycle. Он уточняет более ранние формулировки в `REQUIREMENTS.md` и `ARCHITECTURE.md`, где Argon2id ещё назывался кандидатом.

## 1. Storage-wide MasterKey

Все защищённые БД одного хранилища Clipensk должны использовать один 256-битный MasterKey.

MasterKey:

- нигде не сохраняется в открытом виде;
- выводится из введённого пользователем пароля;
- после успешной разблокировки удерживается только в памяти процесса;
- при завершении защищённой сессии его управляемый буфер должен быть очищен.

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
- verifier.

Файл **не содержит**:

- пароль;
- MasterKey;
- clipboard payload;
- историю.

Создание файла выполняется через temporary file + atomic move без overwrite существующего metadata.

Повреждённый или неподдерживаемый `storage-crypto.json` не считается новой установкой и не перезаписывается автоматически: приложение остаётся LOCKED.

## 4. MasterKey verifier

До появления реальных зашифрованных БД корректность введённого пароля проверяется через storage-wide verifier:

`HMAC-SHA256(MasterKey, "Clipensk.MasterKeyVerifier.v1\\0" || StorageId)`

Сравнение выполняется constant-time.

Verifier нужен для password/MasterKey lifecycle и **не является защитой от изменения файлов локальным злоумышленником с правом записи**. Когда появятся реальные защищённые БД, успешный verifier не должен быть единственным условием перехода в UNLOCKED: storage layer обязан дополнительно подтвердить, что MasterKey действительно открывает protected database identity.

## 5. Sensitive memory

Реализовано:

- временный UTF-8 `byte[]` пароля очищается через `CryptographicOperations.ZeroMemory`;
- MasterKey находится в `MasterKeyLease`;
- `MasterKeyLease.Dispose()` очищает принадлежащий ему буфер;
- при ошибочном пароле производный candidate key очищается;
- verifier buffers очищаются после использования.

Ограничение WinUI/.NET:

`PasswordBox.Password` возвращает managed `string`. Приложение сразу очищает поле UI и не сохраняет строку, однако гарантированно физически стереть уже созданный immutable managed string из памяти CLR невозможно. Это ограничение нельзя выдавать за полную secure-memory гарантию.

## 6. Что ещё не реализовано

Отдельными задачами остаются:

- реальные SQLite БД;
- конкретная native SQLCipher distribution/integration;
- передача raw 32-byte MasterKey в SQLCipher;
- SQLCipher cipher/KDF/page parameters;
- проверка protected DB identity перед окончательным unlock;
- создание Current/Catalog/Archive schema;
- смена пароля и/или MasterKey;
- recovery procedure при потере или повреждении crypto metadata;
- auto-lock runtime и уничтожение всех storage handles при lock.

До реализации protected databases нельзя утверждать, что clipboard history уже зашифрована: сейчас реализован password → MasterKey lifecycle и credential verification, но не шифрование БД.
