# Открытые вопросы Clipensk

Этот файл содержит только ещё не зафиксированные решения. Утверждённые требования находятся в `REQUIREMENTS.md`, техническая архитектура — в `ARCHITECTURE.md`, зафиксированный password → MasterKey и protected SQLite profile — в `CRYPTOGRAPHY.md`.

## 1. Конкретная Open Source-лицензия

Зафиксировано: проект полностью Open Source.

Не выбрано:

- MIT;
- BSD-3-Clause;
- MPL-2.0;
- GPL-3.0-or-later;
- другой вариант.

## 2. Минимальная продуктовая версия Windows

Для начала реализации зафиксированы:

- .NET 10;
- Windows App SDK 2.4.0 Stable;
- WinUI 3;
- технический target текущего каркаса допускает Windows 10 build 19041 и выше.

Нужно отдельно определить официально поддерживаемые пользователем версии Windows, в частности оставлять ли Windows 10 в продуктовой поддержке или ориентироваться только на Windows 11.

## 3. Финальная схема распространения

Нужно определить:

- MSIX как основной способ установки;
- unpackaged/portable вариант;
- нужны ли оба варианта.

Для MSIX уже зафиксировано, что при первом запуске пользователь выбирает каталог хранения данных. Текущий development-host собирается unpackaged и не фиксирует конечную схему распространения.

## 4. Оставшаяся криптографическая и native-delivery работа

Уже зафиксировано в `CRYPTOGRAPHY.md`:

- один MasterKey для всех защищённых БД;
- пароль не сохраняется;
- Argon2id v1.3, production profile 64 MiB / 3 iterations / 4 lanes / 16-byte salt / 32-byte MasterKey;
- `storage-crypto.json` содержит KDF metadata/verifier/StorageId и durable `StorageInitialized`, но не пароль и не MasterKey;
- production SQLite boundary — `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.sqlcipher`;
- native library — самостоятельно собираемая SQLCipher Community Edition под именем `sqlcipher.dll`;
- deprecated bundled `e_sqlcipher` binaries не используются;
- raw MasterKey передаётся как SQLCipher raw-key representation через `sqlite3_key`;
- SQLCipher runtime должен быть не ниже 4.12 и пройти `cipher_version` + `cipher_status=1`;
- compatibility profile — SQLCipher 4; `cipher_memory_security=ON`;
- `UNLOCKED` разрешён только после фактического открытия Current/Catalog, `sqlite_master`, `quick_check` и проверки `DatabaseIdentity`.

Остаётся определить/реализовать:

- воспроизводимый native build pipeline SQLCipher для Windows x64 и ARM64;
- provenance/hash/signing policy для собранного `sqlcipher.dll`;
- точную packaging-интеграцию native library для MSIX/unpackaged вариантов;
- Windows integration CI, доказывающий реальное SQLCipher encryption для обеих архитектур;
- процедуру смены пароля и/или MasterKey;
- recovery procedure при потере/повреждении crypto metadata;
- recovery для partial Current/Catalog и catalog rebuild.

## 5. Hot backup / snapshot

Сознательно отложено.

Пока сохраняется требование, что файлы должны быть доступны стороннему процессу для открытия на чтение/копирования «на лету». Окончательный протокол получения гарантированно согласованной резервной копии будет определён отдельно.

## 6. Набор форматов и лимиты по умолчанию

Нужно утвердить конкретные defaults:

- какие текстовые форматы включены при новой установке;
- лимит Plain/Unicode Text;
- лимит HTML;
- лимит RTF;
- лимиты изображений;
- лимиты explicitly enabled custom binary formats;
- ограничения `CF_HDROP` по количеству элементов/размеру текстовой записи.

Уже зафиксировано:

- HTML и RTF хранятся только в БД;
- изображения нормализуются в PNG и хранятся как external files;
- unknown registered/private binary payload выключен по умолчанию;
- CF_WAVE, CF_RIFF и virtual file contents не сохраняются.

## 7. Период журнала по умолчанию

Механизм зафиксирован, но конкретное значение (например 7/30/90 дней) пока не выбрано.

## 8. Параметры ротации архивов по умолчанию

Нужно определить начальные значения:

- maximum record count;
- maximum physical database size;
- maximum calendar span;
- ANY/ALL логика включённых условий.

## 9. Формат файлов локализации

Предварительно предлагается JSON, но окончательный schema/versioning ещё не утверждены.

Нужно определить:

- точную JSON-схему;
- правила совместимости версий;
- подпись/проверку сторонних переводов, если потребуется;
- команду экспорта шаблона перевода.

## 10. Вставка выбранных данных из журнала

Зафиксировано:

- глобальная горячая клавиша настраивается пользователем и вызывает основной журнал;
- из журнала доступны все функции управления Clipensk.

Нужно детализировать:

- режим «скопировать выбранную запись обратно в clipboard»;
- нужна ли автоматическая вставка в исходное окно;
- focus restoration;
- plain-text paste;
- обработку приложений с ограничениями foreground activation.
