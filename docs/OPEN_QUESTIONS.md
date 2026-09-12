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

Для текущего unpackaged x64 development/runtime path доставка verified `sqlcipher.dll` уже реализована: native pipeline публикует приложение вместе с exact verified DLL, runtime/native manifests и license files, а отдельный post-publish smoke запускает production storage boundary так, чтобы SQLCipher загружался именно из итогового publish layout. Это не выбирает и не доказывает будущий MSIX installer path.

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
- текущий unpackaged x64 publish path проверяет hash/provenance staged `sqlcipher.dll` и выполняет post-publish production storage smoke непосредственно через final runtime layout;
- ARM64 не поддерживается и не является будущим native target;
- production умеет перестраивать обе Catalog v3 projections внутри существующего валидного Catalog;
- explicit pre-session recovery умеет атомарно пересоздать **отсутствующий** `storage-catalog.db` из валидного Current + Archive state, не ослабляя normal unlock fail-closed semantics;
- explicit pre-session replacement умеет полностью построить replacement **существующего** Catalog из authoritative Current + Archive state и атомарно опубликовать его с сохранением старых Catalog bytes в `Current/CatalogQuarantine`;
- пользовательский pre-session recovery UI реализует явный confirmation flow и различает missing-Catalog recreation и existing-Catalog replacement, не ослабляя обычный unlock path.

Остаётся определить/реализовать:

- packaging и runtime-delivery verification для будущей выбранной **финальной** схемы распространения, если она отличается от текущего unpackaged x64 path (в частности MSIX);
- byte-for-byte reproducibility/provenance hardening;
- процедуру смены пароля и/или MasterKey;
- recovery procedure при потере/повреждении crypto metadata;
- recovery strategy при потере `current.db` (Catalog не является source of truth и сам по себе не может восстановить Current);
- retention/удаление Catalog quarantine backups.

## 5. Hot backup / snapshot

Сознательно отложено.

Пока сохраняется требование, что файлы должны быть доступны стороннему процессу для открытия на чтение/копирования «на лету». Окончательный протокол получения гарантированно согласованной резервной копии будет определён отдельно.

## 6. Набор форматов и лимиты по умолчанию

Источник global policy и отсутствие автоматического default зафиксированы в
`GLOBAL_CAPTURE_POLICY.md`: защищённый Current, storage scope, явная первичная настройка.
Current v6, repository, JournalWindow setup UI, app-level policy loading, custom-binary extension
repository/provider, protected composition, worker lifecycle и cleanup при последующем изменении
policy уже реализованы. Global и per-application change path останавливает capture runtime,
атомарно публикует новую policy вместе с Current cleanup и durable pending-maintenance marker,
после чего resumable continuation последовательно выполняет Archive cleanup, Catalog maintenance,
external-payload Trash collection и marker completion; capture runtime возобновляется только после
успешного завершения continuation. Отдельный `ProtectedExternalPayloadTrashCollector` остаётся
last-reference physical cleanup foundation и переносит только verified history-unreferenced canonical
external objects `Files -> Trash` после authoritative Current+Archive live-set reconstruction.

Нужно утвердить конкретные defaults:

- какие текстовые форматы включены при новой установке;
- лимит Plain/Unicode Text;
- лимит HTML;
- лимит RTF;
- лимиты изображений;
- лимиты explicitly enabled custom binary formats;
- ограничения `CF_HDROP` по количеству элементов/размеру canonical metadata representation.

Семантика измерения `MaxBytes` зафиксирована в `CLIPBOARD_CAPTURE_SIZE_LIMITS.md`: текстовые representations и ссылки измеряются в UTF-8, изображения — по нормализованным PNG bytes, custom binary — по точным сохраняемым bytes, `CF_HDROP`/StorageItems — по exact UTF-8 bytes versioned canonical JSON metadata representation.

Уже зафиксировано/реализовано:

- HTML и RTF хранятся только в БД;
- для HTML `SearchText` строится отдельной managed projection после MaxBytes gate; raw `CF_HTML` остаётся неизменным canonical payload;
- для RTF `SearchText` строится отдельной conservative managed projection после MaxBytes gate, без `RichEditBox`/UI-thread dependency; raw RTF остаётся неизменным canonical payload, а malformed/unsafe parser cases завершаются fail-closed с `SearchText = null`;
- для `CF_HDROP` canonical representation v1 фиксирует version, исходный item order, full path, name, extension, directory flag и preferred Copy/Move/Link/Unknown operation; file contents не читаются;
- изображения нормализуются в PNG и хранятся как external files;
- explicitly allowed registered/private binary payload читается только как `IRandomAccessStream`, измеряется по exact bytes и остаётся выключенным по умолчанию;
- exact custom-binary `FormatName → FileExtension` хранится storage-scoped в Current v6; hidden `.bin` fallback отсутствует как в production resolver, так и в низкоуровневых address/store API;
- App compose-ит protected delivery после active unlock session и повторно после первого policy COMMIT;
- listener стартует только для non-null persisted policy graph, а single-reader worker использует cancellation exact protected session;
- lock останавливает listener, инвалидирует capture epoch и worker/composition generations; новая session не начинает dequeue до завершения предыдущего worker task;
- external last-reference physical cleanup foundation умеет после authoritative Current+Archive projection rebuild безопасно перемещать history-unreferenced canonical Files objects в date-grouped Trash, fail-closed проверяя SHA и reparse-point containment;
- CF_WAVE, CF_RIFF и virtual file contents не сохраняются и блокируются до reader routing.

Manual WinUI/real-clipboard smoke для полного runtime остаётся отдельным неполученным evidence; unit/CI tests не эмулируют настоящий `WM_CLIPBOARDUPDATE`, foreground source application и WinRT `DataPackageView`.

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

## 11. Application identity alias/merge hardening

Durable application identity contract зафиксирован в `APPLICATION_IDENTITY.md` и уже имеет persistent implementation:

- единственный durable key policy/history — Clipensk-owned `ApplicationId` (непустой GUID);
- AUMID и exact executable path используются как resolution aliases/evidence, а не как primary key;
- packaged AUMID является сильным alias и имеет приоритет, когда доступен;
- unpackaged Win32 без AUMID может автоматически разрешаться по уже известному exact executable path alias;
- впервые увиденный unpackaged path может получить новый `ApplicationId`;
- перемещение/переименование executable на ранее неизвестный path не считается автоматически тем же приложением;
- PID, HWND, display name, publisher, version resource и file hash не могут молча объединять identities;
- `SourceApplication` и `InvocationApplication` остаются отдельными runtime concepts;
- `ApplicationId` и aliases сохраняются в защищённом Current с atomic uniqueness/conflict semantics;
- conflicting AUMID/path bindings завершаются fail-closed, без last-write-wins;
- per-application capture policy repository индексируется только по durable `ApplicationId`, а не по runtime process metadata.

Следовательно, concrete policy/history schema больше не заблокирована отсутствием универсального Windows-native durable key.

Остаётся определить/реализовать:

- explicit user merge/rebind workflow для moved/renamed unpackaged applications;
- правила удаления/retention неиспользуемых aliases;
- UI отображения и ручного управления discovered applications;
- нужны ли в будущем дополнительные **только advisory** attributes для предложения merge пользователю, без silent equivalence.
