# Открытые вопросы Clipensk

Этот файл содержит только ещё не зафиксированные решения. Утверждённые требования находятся в `REQUIREMENTS.md`, техническая архитектура — в `ARCHITECTURE.md`, зафиксированный password → MasterKey profile и SQLCipher boundary — в `CRYPTOGRAPHY.md`.

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
- поддерживаемая архитектура продукта — только Windows x64 (AMD64); ARM64 вне product scope;
- технический target текущего каркаса допускает Windows 10 build 19041 и выше.

Нужно отдельно определить официально поддерживаемые пользователем версии Windows, в частности оставлять ли Windows 10 в продуктовой поддержке или ориентироваться только на Windows 11.

## 3. Финальная схема распространения

Нужно определить:

- MSIX как основной способ установки;
- unpackaged/portable вариант;
- нужны ли оба варианта.

Для MSIX уже зафиксировано, что при первом запуске пользователь выбирает каталог хранения данных. Текущий development-host собирается unpackaged и не фиксирует конечную схему распространения.

## 4. Оставшаяся криптографическая/native конфигурация

Уже зафиксировано в `CRYPTOGRAPHY.md` и `NATIVE_SQLCIPHER_BUILD.md`:

- один MasterKey для всех защищённых БД;
- пароль не сохраняется;
- KDF для новых storage — Argon2id v1.3;
- production profile: 64 MiB, 3 iterations, 4 lanes, 16-byte salt, 32-byte MasterKey;
- storage-wide `storage-crypto.json` содержит salt/profile/verifier, но не пароль и не MasterKey;
- raw 32-byte MasterKey передаётся SQLCipher через `sqlite3_key`;
- SQLCipher handle обязан подтвердить `cipher_version >= 4.12.0` и `cipher_status=1`;
- Current/Catalog `DatabaseIdentity` является обязательным gate перед `UNLOCKED`;
- production native SQLCipher строится из source, deprecated bundled `e_sqlcipher` binaries не используются;
- для Windows x64 реализован pinned source-build/smoke pipeline на SQLCipher 4.17.0 + OpenSSL 3.5.8; его фактический PASS должен подтверждаться отдельным CI run;
- ARM64 не поддерживается и не является будущим native target.

Остаётся определить/реализовать:

- packaging и runtime delivery verified `sqlcipher.dll` для выбранной схемы распространения;
- byte-for-byte reproducibility/provenance hardening;
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

Семантика измерения `MaxBytes` для уже канонизированных payload зафиксирована в `CLIPBOARD_CAPTURE_SIZE_LIMITS.md`: текстовые representations и ссылки измеряются в UTF-8, изображения — по нормализованным PNG bytes, custom binary — по точным сохраняемым bytes. Для `CF_HDROP` canonical persisted metadata representation всё ещё нужно определить вместе с его лимитами; произвольная сериализация запрещена.

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
- ANY/ALL логику включённых условий.

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

## 11. Persistent application identity для policy/history

Нужно определить стабильную идентичность приложения, пригодную как durable key для per-application capture policy и будущей history attribution.

Нельзя без отдельного решения считать durable identity:

- PID или process lifetime;
- HWND/foreground-window handle;
- путь к executable сам по себе, поскольку он может измениться при перемещении/обновлении;
- display name или другой пользовательский label.

Технически подтверждено по Windows application identity contracts:

- для packaged applications `ApplicationUserModelID` (AUMID) представляет application identity, строится из Package Family Name + Package Relative Application ID и документирован как persistable value, независимый от package version и architecture; это подходящий durable candidate для packaged branch;
- Package Family Name сам по себе недостаточен как application key: один package может объявлять несколько applications;
- unpackaged Win32 application не имеет package identity;
- explicit AppUserModelID для classic Win32 опционален; если приложение его не задаёт, Windows может использовать internal heuristic AppUserModelID, который приложение не может получить как стабильное значение;
- `GetCurrentProcessExplicitAppUserModelID` читает explicit process AppUserModelID только для текущего процесса, поэтому Clipensk не может считать этот API универсальным способом идентификации произвольного source process;
- window-level `System.AppUserModel.ID` можно наблюдать для конкретного HWND, но он может идентифицировать отдельный user-visible mode/subexperience и переопределять process-level ID, поэтому сам по себе не является универсальным durable application key.

Официальные ссылки:

- https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/package-identity-overview
- https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.core.applistentry.appusermodelid
- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/packaging/
- https://learn.microsoft.com/en-us/windows/win32/shell/appids
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-getcurrentprocessexplicitappusermodelid

Следовательно, packaged branch можно строить вокруг AUMID, но общий fallback для unpackaged Win32 остаётся открытым. Нужно определить, какие наблюдаемые attributes допустимы для такого fallback, как обрабатываются обновление/перемещение executable и когда две process instances считаются одним приложением. До этого executable path/PID нельзя неявно повышать до durable identity.

`SourceApplication` и `InvocationApplication` остаются разными runtime concepts и не должны неявно объединяться одним guessed key.

До фиксации unpackaged fallback concrete per-application policy repository/storage schema и универсальный durable application key не вводятся.
