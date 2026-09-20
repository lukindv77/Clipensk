# Открытые вопросы Clipensk

Этот файл содержит только ещё не зафиксированные решения. Утверждённые требования находятся в `REQUIREMENTS.md`, техническая архитектура — в `ARCHITECTURE.md`, зафиксированный password → MasterKey profile и SQLCipher boundary — в `CRYPTOGRAPHY.md`.

## 1. Конкретная Open Source-лицензия

Зафиксировано: проект полностью Open Source.

**Решение пользователя (2026-09-20): выбор конкретной лицензии сознательно откладывается на
неопределённый срок.** Это не «не дошли руки», а принятое решение отложить; до него файла `LICENSE`
в репозитории нет, а страница «О программе» говорит только про открытость проекта, не называя
лицензию.

Варианты, между которыми выбор не сделан: MIT, BSD-3-Clause, MPL-2.0, GPL-3.0-or-later, другой.

## 2. Минимальная продуктовая версия Windows

Для начала реализации зафиксированы:

- .NET 10;
- Windows App SDK 2.4.0 Stable;
- WinUI 3;
- поддерживаемая архитектура продукта — только Windows x64 (AMD64); ARM64 вне product scope.

**Решение пользователя (2026-09-20): продуктовая поддержка — только Windows 11. Windows 10 из
продуктовой поддержки исключается.**

Не реализовано: технический каркас всё ещё заявляет `SupportedOSPlatformVersion` = 10.0.19041.0
(Windows 10 build 19041). Приведение target/manifest и пользовательской документации к «только
Windows 11» — отдельная невыполненная задача.

## 3. Финальная схема распространения

**Решение пользователя (2026-09-20): нужны оба варианта — и MSIX, и unpackaged/portable.**

Для MSIX уже зафиксировано, что при первом запуске пользователь выбирает каталог хранения данных.

Не реализовано: сейчас собирается только unpackaged x64 host. MSIX-упаковка, её installer path и
verification доставки `sqlcipher.dll` внутри MSIX — отдельная невыполненная задача.

Для текущего unpackaged x64 development/runtime path доставка verified `sqlcipher.dll` уже реализована: native pipeline публикует приложение вместе с exact verified DLL, runtime/native manifests и license files, а отдельный post-publish smoke запускает production storage boundary так, чтобы SQLCipher загружался именно из итогового publish layout. Это не выбирает и не доказывает будущий MSIX installer path.

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

**Решение пользователя (2026-09-20): при новой установке по умолчанию включены Plain/Unicode Text,
HTML, RTF, изображения, custom binary и `CF_HDROP`.**

Не зафиксированы и по-прежнему открыты **конкретные числовые лимиты** для каждого из них:

- лимит Plain/Unicode Text;
- лимит HTML;
- лимит RTF;
- лимиты изображений;
- лимиты explicitly enabled custom binary formats;
- ограничения `CF_HDROP` по количеству элементов/размеру canonical metadata representation.

Не реализовано: сейчас первичная настройка требует явного выбора пользователем и не предлагает
никакого набора по умолчанию. Применение решения выше — отдельная невыполненная задача, которая
блокируется отсутствием числовых лимитов.

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

Механизм зафиксирован и реализован (`DefaultJournalPeriod`, экран настроек).

**Решение пользователя (2026-09-20): период журнала по умолчанию — 30 дней.**

Не реализовано: `DefaultJournalPeriodDays` по-прежнему opt-in и по умолчанию не задан; проставление
значения 30 как дефолта — отдельная невыполненная задача.

## 8. Параметры ротации архивов по умолчанию

**Решение пользователя (2026-09-20):**

- maximum record count — **выключен** по умолчанию;
- maximum physical database size — **выключен** по умолчанию;
- maximum calendar span — **включён, 30 дней**;
- при выборе в настройках варианта ротации приложение обязано проверять заполнение выбранного
  условия и требовать его заполнения от пользователя.

Не реализовано: сейчас все пороги opt-in и пустые, а экран настроек принимает вариант ротации без
требования заполнить соответствующее условие. Оба пункта — отдельная невыполненная задача.

## 9. Формат файлов локализации

Формат де-факто реализован: плоский JSON-объект «ключ → значение» с теми же ключами, что и во
встроенном русском (`JsonExternalLocalizationLoader`).

**Решение пользователя (2026-09-20): значение задаётся по ИД (ключу); кроме ИД и значения в файле
указывается комментарий на русском языке, который генерирует само приложение.**

Не реализовано: текущий формат хранит только «ключ → значение», без поля комментария, и в
приложении нет команды экспорта шаблона перевода, которая этот русский комментарий бы порождала.
Это — отдельная невыполненная задача, вместе с ней остаются открытыми:

- правила совместимости версий схемы;
- подпись/проверка сторонних переводов, если потребуется.

## 10. Вставка выбранных данных из журнала

Зафиксировано:

- глобальная горячая клавиша настраивается пользователем и вызывает основной журнал;
- из журнала доступны все функции управления Clipensk.

Решено и реализовано:

- запись возвращается в clipboard в том виде, в каком была захвачена;
- `CF_HDROP`/StorageItems возвращается **текстом**: по одному полному пути на строку, в сохранённом
  порядке элементов. Сами файлы Clipensk не хранит (§19 требований), поэтому вернуть их как файлы
  невозможно; конверсия никогда не затирает реально захваченный plain text — в этом случае список
  файлов помечается как пропущенный;
- собственная запись Clipensk в буфер не захватывается повторно: она опознаётся по clipboard
  sequence number, иначе каждое повторное использование дублировало бы историю.

**Решения пользователя (2026-09-20):**

- **автоматическая вставка в исходное окно не используется**; решение по ней отложено;
- **focus restoration используется**: при открытии журнала (hotkey или клик по трею) HWND текущего
  foreground-окна запоминается **только в памяти**, на время сессии журнала, и никогда не
  сохраняется на диск; при закрытии/скрытии журнала выполняется попытка `SetForegroundWindow` на
  этот HWND, а если окно уже не существует (`IsWindow`) или вернуть фокус не удалось — это **тихо
  игнорируется**, без ошибки пользователю;
- **нужен отдельный режим «вставить как plain text»** независимо от сохранённых форматов.

Ограничение, зафиксированное осознанно: вернуть фокус в окно процесса с более высокой integrity
level (запущенного «от имени администратора») нельзя — это блокирует UIPI, и обойти это можно было
бы только запуском самого Clipensk с повышением, что для постоянно работающего резидентного
приложения неприемлемо. Такой случай попадает под «тихо игнорируется».

Не реализовано: ни focus restoration, ни режим «вставить как plain text» ещё не написаны —
`WindowsInvocationApplicationResolver` сейчас получает foreground HWND, но сразу отбрасывает его,
сохраняя только identity-метаданные. Обе задачи — отдельные невыполненные.

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

Остаётся определить/реализовать (решение по этому пункту **не принято**):

- explicit user merge/rebind workflow для moved/renamed unpackaged applications;
- правила удаления/retention неиспользуемых aliases;
- UI отображения и ручного управления discovered applications;
- нужны ли в будущем дополнительные **только advisory** attributes для предложения merge пользователю, без silent equivalence.

## 12. Один экземпляр на пользователя и изоляция хранилищ между пользователями Windows

**Решения пользователя (2026-09-20):**

- для текущего пользователя Windows **не допускается запуск двух экземпляров** Clipensk; это
  контролируется всегда;
- запуск **из-под другого пользователя Windows разрешён**, но со своими настройками и своей базой;
- доступ к «чужим» базам/архивам **не допускается и контролируется жёстко**;
- настройки и база по умолчанию размещаются только там, куда есть доступ у текущего пользователя;
- в настройках можно указать другое место хранения;
- **при указании нового пути все настройки и база должны быть полностью перемещены** в новое место.

Реализовано (принято в `main`, `bbef132…`):

- single-instance на пользователя через эксклюзивно удерживаемый lock-файл
  `%LocalAppData%\Clipensk\instance.lock` (не named mutex: `Global\`-имя требует привилегии, которой
  у обычного пользователя может не быть, а файловый замок беспривилегированный, машинный и
  самовосстанавливается после аварийного завершения);
- второй запуск не поднимает конкурирующий runtime, а передаёт запрос уже работающему экземпляру
  (`FindWindowEx` по message-only окну + `AllowSetForegroundWindow` + `WM_APP+2`); между сессиями
  терминальных служб это невозможно (оконные хэндлы не пересекают сессии) — там показывается
  сообщение;
- путь по умолчанию для данных — `%LocalAppData%\Clipensk\Data`, предлагается на первом запуске
  отдельной кнопкой рядом с выбором произвольной папки;
- `WindowsDataRootProtectionService` ограничивает ACL каталога данных текущим пользователем,
  `SYSTEM` и `Administrators`, снимая наследование, **до** записи первого durable-байта.
  `SYSTEM`/`Administrators` оставлены намеренно: их удаление не добавляет приватности там, где
  администратор может забрать владение, но ломает резервное копирование и обслуживание.

Осознанные границы реализации:

- ACL переписываются **только** для каталога, который создал сам Clipensk, либо для пустого. Если
  пользователь указал существующую непустую папку, права не трогаются и об этом сообщается: Clipensk
  не отбирает у других пользователей доступ к чужим данным, лежащим рядом;
- файловая система без поддержки ACL (FAT32/exFAT, часть сетевых шар) — предупреждение, а не отказ;
  содержимое баз остаётся зашифрованным в любом случае.

**Не реализовано (отдельный заход, решение пользователя от 2026-09-20 — делать отдельно):** полный
перенос настроек и базы при смене пути в настройках. Это не копирование папки: нужно гарантированно
закрыть все соединения SQLite/SQLCipher и приостановить capture runtime, применить дисциплину
«скопировать → проверить → только потом удалить исходник», пережить обрыв посреди операции без
потери и повреждения данных, и согласоваться с существующими инвариантами (не запускаться при
незавершённой ротации/split, работать через тот же protected-session/mutation-lease механизм). По
цене ошибки это прямой аналог Archive Rotation/Split.
