# Криптографический профиль Clipensk

Статус: **частично реализовано; x64 native build pipeline implemented, но production native delivery ещё NOT READY.**

Дата решения: 4 сентября 2026 года. Модель ключа без файла метаданных (соль в заголовке баз) — решение пользователя 23 сентября 2026 года.

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

Это соответствует second recommended profile RFC 9106 для Argon2id с ограниченной памятью.

Параметры профиля зашиты в код и не хранятся в файлах. Номер профиля записан в первом байте соли
хранилища (§3): профиль v1 — байт `0x01`. Будущий профиль получит новый номер, и Clipensk выберет
параметры по этому байту без перебора.

Пароль преобразуется в UTF-8 без дополнительной Unicode-нормализации.

## 3. Соль хранилища в заголовке каждой базы

Решение пользователя 2026-09-23 (`OPEN_QUESTIONS.md` §4): отдельного файла криптографических
метаданных нет. Для восстановления работы достаточно файлов баз и пароля.

Соль хранилища — 16 байт: байт 0 — номер KDF-профиля, байты 1–15 — случайные (CSPRNG). Она создаётся
один раз при создании хранилища и дальше не меняется, в том числе при смене пароля (§12).

Одна и та же соль:

- служит солью Argon2id: `MasterKey = Argon2id(пароль, соль)`;
- передаётся SQLCipher вместе с ключом (§6), и SQLCipher записывает её открытым текстом в первые
  16 байт **каждого** файла базы хранилища (`current.db`, `storage-catalog.db`, каждый
  `archive_*.db`, их копии и shadow-файлы).

Поэтому соль продублирована в каждой базе, и любой уцелевший файл базы позволяет вывести ключ.
Соль не секретна: она нужна только для уникальности ключа, как и прежде, когда она лежала открыто в
`storage-crypto.json`.

**Поиск соли.** Кандидаты — существующие файлы `Current/current.db`, `Current/storage-catalog.db`,
`Archive/archive_*.db` (в этом порядке, архивы — по имени). Из каждого читаются первые 16 байт;
файлы короче 16 байт, нечитаемые и с неизвестным номером профиля пропускаются. Различные соли
упорядочиваются по числу файлов, в которых они встречаются, при равенстве — по порядку кандидатов.
Первой пробуется самая частая соль, затем следующие (§4). Так повреждённый заголовок одной базы или
подложенный чужой архив не мешает открыть хранилище.

**Хранилище существует**, если есть хотя бы один файл-кандидат. Тогда Clipensk только просит
ввести пароль и никогда не предлагает задать новый: создание нового хранилища поверх существующих
баз исключено. Если файлы есть, но ни у одного нет пригодной соли, состояние — `Invalid`
(fail-closed, разблокировка недоступна).

**Прежний формат.** Хранилище, в корне которого лежит `storage-crypto.json` (формат до 2026-09-23),
не поддерживается и не конвертируется (решение пользователя: рабочих хранилищ этого формата нет).
Его состояние — `Invalid`; хранилище создаётся заново в пустой папке.

**Принятое ограничение** (решение пользователя 2026-09-23). Ключ и соль одинаковы во всех базах
хранилища, значит одинаков и HMAC-ключ страниц SQLCipher. Тот, кто может записывать в папку данных,
может вставить в одну базу страницу из другой базы того же хранилища с тем же номером, и SQLCipher
этого не заметит. Подмену файла целиком ловит `DatabaseIdentity`; конфиденциальность не страдает;
возврат страницы к её старой версии внутри одного файла SQLCipher не ловит и при разных солях.

## 4. Проверка пароля

Отдельного проверочного значения нет. Пароль проверяется пробным открытием базы: SQLCipher
сверяет HMAC первой страницы, и при неверном ключе первый же запрос завершается
`SQLITE_NOTADB` (26, «file is not a database»).

Для каждой соли-кандидата (§3) по порядку:

1. `MasterKey = Argon2id(пароль, соль)`.
2. Кандидаты-файлы открываются этим ключом по очереди, пока один не откроется и не даст
   корректный `DatabaseIdentity`; его `StorageId` становится `StorageId` хранилища.
3. Если **все** файлы отвергли ключ (`SQLITE_NOTADB`) — ключ неверен, переход к следующей соли.
4. Если ни один файл не открылся, но не все отказы — `SQLITE_NOTADB` (ошибка чтения, повреждение) —
   хранилище недоступно, дальше соли не перебираются.

Если отвергнуты ключи всех солей — пароль неверен. В обычном хранилище соль одна, и неверный
пароль стоит одного вывода Argon2id.

Отказ от проверочного значения стойкость не снижает: злоумышленник с копией баз и так проверяет
пароли пробным открытием, и каждая попытка стоит ему тот же Argon2id.

Успешное пробное открытие не является достаточным условием `UNLOCKED`: после него Current и
Catalog проходят полную проверку (§9).

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

## 6. Передача ключа SQLCipher

MasterKey не передаётся SQLCipher как пользовательский пароль для повторного PBKDF2.

Ключ хранилища в памяти — 48 байт: MasterKey (32) и соль хранилища (16)
(`StorageKeyMaterial`). Из него формируется raw-key representation с явной солью:

`x'<64 hex characters of MasterKey><32 hex characters of salt>'`

и передаётся через `sqlite3_key` до первого чтения страниц БД. При создании файла SQLCipher
записывает эту соль в его первые 16 байт; при открытии существующего файла с другой солью HMAC
первой страницы не сходится, и файл не открывается (`SQLITE_NOTADB`). Поэтому все базы хранилища
гарантированно несут одну соль. `VACUUM` соль сохраняет.

Проверено локально на SQLCipher 4.5.6 (консоль Ubuntu): заголовок нового файла равен переданной
соли; тот же ключ с другой солью и неверный ключ дают `SQLITE_NOTADB`; после `VACUUM` заголовок
прежний. Для production-сборки 4.17.0 это подтвердил native smoke (§11): Native run `35851776903` на
`68ec635` — SUCCESS.

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

Новое хранилище создаётся, только если в DataRoot нет ни одной базы-кандидата (§3): соль
генерируется заново, `StorageId` — новый. Пара создаётся в staging-каталоге и публикуется в final
`Current` только после повторной проверки обеих БД. Публикация пары и есть момент создания
хранилища: прерванное создание оставляет лишь staging-каталог, и следующая попытка начинается
заново.

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
  ↓ соли из заголовков баз (§3); нет баз → новая соль, новый StorageId
  ↓ Argon2id → candidate MasterKey
пробное открытие баз (§4) → StorageId из DatabaseIdentity
  ↓
SQLCipher provider/version/cipher_status
  ↓
open Current + Catalog with MasterKey
  ↓
sqlite_master + quick_check + DatabaseIdentity
  ↓
UNLOCKED
```

Нельзя завершать `UNLOCKED` после одного только пробного открытия.

Новое хранилище создаётся только при разблокировке, которую экран начал как «создание пароля»
(пароль введён дважды). Если к моменту разблокировки базы исчезли из папки, которая до этого
определялась как существующее хранилище, новое хранилище не создаётся.

## 10. Sensitive memory

Реализовано best-effort:

- temporary UTF-8 password bytes zeroed;
- MasterKey owned by `MasterKeyLease` and zeroed on dispose;
- wrong candidate key zeroed;
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
- первые 16 байт обеих БД равны соли хранилища;
- тот же MasterKey с другой солью не открывает storage;
- после `VACUUM` соль в заголовке прежняя и storage открывается;
- production provider возвращает `cipher_version` и `cipher_status=1`.

## 12. Смена пароля

Решение пользователя 2026-09-23: смена пароля — перешифровка всех баз хранилища новым MasterKey
(соль хранилища прежняя). Без отдельного файла негде хранить «обёрнутый» ключ данных, поэтому
вариант с обёрткой ключа невозможен. Время смены растёт с объёмом истории; нужен отдельный
отказоустойчивый протокол (сначала копии с новым ключом, затем переключение — по образцу переноса
хранилища). **Реализовано** — `PASSWORD_CHANGE_PROTOCOL.md`: копии всех баз перешифровываются
`sqlite3_rekey` и проверяются, затем атомарно подменяют оригиналы; прерванную смену по файлам
доводит или отменяет разблокировка, отдельного marker'а нет.

## 13. Что ещё не реализовано

Пункты, которые перечислялись здесь в первой версии документа и с тех пор реализованы (таблицы
истории, Catalog, Archive, миграции, восстановление Catalog, автоблокировка, проверенный
publish-путь `sqlcipher.dll` для unpackaged-сборки), описаны в своих документах.

Остаётся (`OPEN_QUESTIONS.md` §4): byte-for-byte reproducibility/provenance hardening.

Действие «Начать текущую базу заново» при потере `current.db` — `CURRENT_RESTART.md`.

Резервные копии `Current/CatalogQuarantine` удаляются по сроку корзины
(`STORAGE_CATALOG_RECOVERY.md`, «Срок хранения quarantine»).

Граница блокировки: lock отзывает MasterKey и защищённую сессию, но не ждёт завершения уже начатых
операций (`ProtectedStorageSessionLease.Dispose`) — операция, открывшая соединение до отзыва,
закрывает его сама при завершении или отмене. Поэтому перенос хранилища, которому нужна гарантия
отсутствия открытых файлов, дополнительно открывает их эксклюзивно
(`DATA_ROOT_RELOCATION_PROTOCOL.md` §1).
