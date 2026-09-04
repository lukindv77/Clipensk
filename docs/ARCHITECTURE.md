# Архитектура Clipensk

## 1. Общая модель

Clipensk проектируется как Windows desktop-приложение с современным WinUI 3 интерфейсом и отдельным системным слоем Win32 interop.

```text
Windows Clipboard
       │
       ▼
Resident Core
       │
       ├── Clipboard Monitor
       ├── Hotkey Manager
       ├── Tray Integration
       ├── Application Context
       └── Capture Pipeline
               │
               ▼
          Storage Layer
               │
      ┌────────┼─────────┐
      │        │         │
 current.db  Archive   Files
      │        │         │
      └──── HistoryQueryService ──► WinUI 3 Journal Shell
```

WinUI 3 является UI-слоем. Clipboard monitoring, HWND, глобальные hotkeys, tray и системные события находятся в отдельном Windows/Interop слое.

## 2. Структура solution и технологический baseline

Текущий baseline разработки:

- C# / .NET 10;
- WinUI 3;
- Windows App SDK 2.4.0 Stable;
- технический Target Framework Windows: `net10.0-windows10.0.26100.0`;
- `SupportedOSPlatformVersion`: `10.0.19041.0`;
- текущий development-host — unpackaged; финальная схема распространения остаётся отдельным решением.

```text
Clipensk.slnx

src/
  Clipensk.App/             WinUI 3 UI, App lifecycle, Journal Shell
  Clipensk.Core/            Domain и application services
  Clipensk.Windows/         Win32/WinRT integration
  Clipensk.Storage/         SQLite/SQLCipher/FTS/catalog/archive/files
  Clipensk.Infrastructure/  configuration, logging, localization, crypto helpers

tests/
  Clipensk.Core.Tests/
  Clipensk.Storage.Tests/       planned
  Clipensk.Integration.Tests/   planned
```

`Clipensk.Core` не должен зависеть от WinUI, HWND или конкретной реализации SQLite.

## 3. Резидентная модель

Приложение работает одним per-user процессом.

Для приёма системных сообщений используется отдельное скрытое message-only окно:

```text
ResidentMessageWindow
  ├── WM_CLIPBOARDUPDATE
  ├── WM_HOTKEY
  ├── tray callbacks
  └── session/system messages
```

Закрытие основного окна журнала не должно завершать резидентный процесс. Полный выход выполняется отдельной командой.

## 4. Clipboard Capture Pipeline

Обработчик `WM_CLIPBOARDUPDATE` не выполняет тяжёлую работу. Он только фиксирует изменение и передаёт запрос в очередь обработки.

```text
WM_CLIPBOARDUPDATE
   ↓
CaptureRequest
   ↓
Capture Queue
   ↓
Source App Resolution
   ↓
Policy Evaluation
   ↓
Format Readers
   ↓
Normalization
   ↓
Deduplication
   ↓
Persistence
```

Win32 используется для мониторинга и системного контекста. WinRT DataTransfer API может использоваться для удобного извлечения стандартных representations.

## 5. Контекст приложений

Различаются минимум два контекста:

1. `SourceApplication` — приложение, поместившее данные в clipboard.
2. `InvocationApplication` — приложение, из которого пользователь вызвал журнал Clipensk.

При вызове журнала глобальной горячей клавишей сначала сохраняется foreground HWND/PID, и только потом активируется окно Clipensk.

Приложения имеют постоянную сущность `Application` и индивидуальную `ApplicationCapturePolicy`.

## 6. Состояния приложения и шифрование

```text
STARTING
   ↓
LOCKED
   ↓ password
UNLOCKING
   ↓
UNLOCKED
```

В `LOCKED`:

- clipboard listener не должен записывать историю;
- защищённые БД не открываются для пользовательской истории;
- доступны только безопасные настройки и файловое обслуживание без чтения фактических clipboard-данных.

После ввода пароля:

```text
Password
  ↓
Argon2id (кандидат)
  ↓
MasterKey
  ↓
SQLCipher databases (кандидат)
```

Один MasterKey используется для всех защищённых БД хранилища.

Пароль никогда не сохраняется.

## 7. Физическое хранилище

После первого выбора пользователем корневого каталога:

```text
<DataRoot>\
  Current\
    current.db
    storage-catalog.db

  Archive\
    archive_000001.db
    archive_000002.db
    archive_000025.db
    archive_000025_0001.db
    ...

  Files\
    YYYY-MM-DD\
      <SHA256>.png
      <SHA256>.<custom-extension>

  Trash\
    ...

  Languages\
    ...
```

Пути Current, Archive, Files и Trash должны быть заменяемыми через настройки. Изменение пути выполняется как контролируемая операция физического перемещения данных.

## 8. Роли баз данных

### current.db

- единственная постоянно пополняемая база истории;
- read/write в разблокированном режиме;
- содержит актуальные события и актуальные пользовательские overlay-данные.

### archive_*.db

- содержит исторические сегменты;
- обычный режим открытия — read-only;
- read/write разрешён только MaintenanceCoordinator;
- каждый архив самоописываем и содержит собственный назначенный календарный период.

### storage-catalog.db

- ускоряющий индекс и карта физического хранилища;
- не является единственным источником критических данных;
- должен полностью перестраиваться по current.db и archive_*.db.

## 9. Самоописываемая база

Каждая защищённая БД должна иметь внутреннюю структуру идентичности, например:

```text
DatabaseIdentity
  StorageId
  DatabaseId
  DatabaseRole
  SchemaVersion
  EncryptionVersion
  CreatedAt
  ArchiveBaseNumber      nullable
  ArchiveSplitSequence   nullable
  CoverageStartDate      nullable
  CoverageEndDate        nullable
```

Для архива `CoverageStartDate/EndDate` обязательны.

Это обеспечивает rebuild `storage-catalog.db` без исходного каталога.

## 10. Календарное владение архивами и временной контекст события

Архивная модель основана на целых календарных днях.

Внутреннее представление диапазона:

```text
[start-of-first-day ; start-of-day-after-last-day)
```

Жёсткий инвариант:

```text
для любого CalendarDate
ArchiveOwnerCount <= 1
```

Каждое событие сохраняет временной контекст момента возникновения:

```text
EventTimeContext
  UtcTimestamp
  LocalOffset
  WindowsTimeZoneId
  CalendarDate
```

`CalendarDate` определяется по локальной Windows time zone на момент события. Последующая смена time zone Windows не должна перераспределять уже сохранённые события между календарными днями архивов.

`storage-catalog.db` может содержать ускоряющую таблицу:

```text
ArchiveDayOwnership
  CalendarDate PRIMARY KEY
  SegmentId
```

Она является производным индексом и восстанавливается из самих архивов.

## 11. Ротация архива

Ротация настраивается по одному или нескольким условиям:

- количество записей;
- размер физического файла;
- календарный период сегмента.

Политика задаёт логику ANY/ALL для включённых условий.

Граница ротации всегда проходит между календарными днями. Один день никогда не делится из-за достижения порога размера или количества записей.

## 12. Именование архивов

Основные архивы получают имена:

```text
archive_000001.db
archive_000002.db
...
```

При разделении существующего архива формируется семейство:

```text
archive_000025.db
archive_000025_0001.db
archive_000025_0002.db
...
```

Существующий файл сохраняет исходное имя. Новые сегменты используют следующий свободный четырёхзначный суффикс. Вложенные суффиксы не создаются.

## 13. Archive Transfer

Перенос Current → Archive выполняется безопасным многофазным протоколом.

```text
PLANNED
  ↓
COPYING
  ↓
COPIED
  ↓
VERIFIED
  ↓
SOURCE_PURGING
  ↓
COMPLETED
```

Удаление из Current разрешено только после подтверждённой записи в Archive.

Допустимые состояния одной логической записи:

```text
Current=yes  Archive=no
Current=yes  Archive=yes
Current=no   Archive=yes
```

Запрещено:

```text
Current=no   Archive=no
```

Временный дубль является нормальной точкой восстановления.

## 14. Разделение архива

Операция `SplitArchive`:

1. получает исходный архив и календарные точки разделения;
2. создаёт новые архивные файлы с суффиксами;
3. переносит данные целыми календарными днями;
4. проверяет новые сегменты;
5. проверяет отсутствие пересечения;
6. актуализирует catalog;
7. только после подтверждения удаляет перенесённые данные из исходного сегмента.

## 15. History Query Planning

Период журнала является частью физического query plan.

```text
JournalQueryContext
   ↓
StorageQueryPlanner
   ↓
storage-catalog.db
   ↓
select current + only intersecting archives
   ↓
open archives READ ONLY
   ↓
parallel/bounded queries
   ↓
merge + sort + dedupe
   ↓
UI
```

Изменение периода создаёт новый `JournalQueryContext` и отменяет старый запрос.

## 16. Поиск

Базовый кандидат: SQLite FTS5 для текстового содержимого.

Для больших журналов используется keyset pagination по сочетанию даты события и постоянного ID вместо глубокого `OFFSET`.

HTML и RTF должны иметь:

- исходное текстовое representation для повторного использования;
- нормализованный plain-text SearchText для поиска.

## 17. Clipboard payload model

Принцип:

> Бинарный clipboard payload непосредственно в БД не хранится. В БД находятся только текстовые clipboard representations и технические метаданные.

### В БД

- Unicode/plain text;
- HTML original + extracted SearchText;
- RTF original + extracted SearchText;
- URL и другие поддерживаемые строковые форматы;
- пути и сведения о `CF_HDROP`;
- метаданные ссылок на внешние payload.

### External Files

Только:

- изображения, предварительно нормализованные в PNG;
- explicitly enabled registered/private binary formats.

### Ignore completely

- CF_WAVE;
- CF_RIFF;
- virtual file contents.

## 18. External File Store

Идентификатор файла:

```text
SHA-256(exact stored bytes)
```

Для изображения pipeline имеет обязательный порядок:

```text
clipboard image representation
   ↓
decode
   ↓
normalize to PNG
   ↓
SHA-256(normalized PNG bytes)
   ↓
Files\YYYY-MM-DD\<SHA256>.png
```

Таким образом одинаковый нормализованный PNG получает один физический content ID независимо от исходного clipboard image representation.

Для разрешённого custom binary payload SHA-256 считается от точных сохраняемых байтов, без обязательного преобразования.

Повторный идентичный payload физически не дублируется.

БД содержит ссылку по SHA-256 и служебные сведения о физическом размещении.

Внешние payload-файлы сознательно не шифруются MasterKey.

## 19. Trash

После удаления последней допустимой ссылки внешний файл переносится в Trash.

`TrashRetentionDays = 30` по умолчанию.

По истечении срока файл физически удаляется.

Перед перемещением требуется глобальная проверка ссылок по Current и всем Archive, которые могут ссылаться на SHA-256.

## 20. Политики форматов

```text
GlobalCapturePolicy
        ↓ inherited/overridden by
ApplicationCapturePolicy
```

Для отдельных параметров рекомендуется модель:

- Inherit;
- Allow;
- Deny.

Unknown registered/private binary format:

- имя формата фиксируется в справочнике обнаруженных возможностей приложения;
- binary payload по умолчанию не сохраняется;
- пользователь может включить его позднее;
- старые отвергнутые payload восстановить нельзя.

## 21. Очистка после изменения политики

Для текстовых DB payload:

- удалять соответствующие данные из Current;
- Archive по общему правилу не переписывать.

Для внешних файловых payload:

- удалять ссылки из Current;
- удалять ссылки и из Archive;
- необходимые Archive временно переводятся в Maintenance/read-write;
- после пересчёта ссылок невостребованные файлы перемещаются в Trash.

Такая операция является полноценной `PolicyCleanupOperation` и должна быть возобновляемой.

## 22. Файлы из Проводника

`CF_HDROP` не превращается в файловый архив.

В БД хранятся текстовые сведения:

- FullPath;
- FileName;
- Extension;
- IsDirectory;
- ItemIndex;
- PreferredAction = Copy / Move / Link / Unknown.

Физические файлы не копируются.

## 23. Maintenance Coordinator

Только `DatabaseMaintenanceCoordinator` может открывать архивные БД на запись.

Основные операции:

- ArchiveTransfer;
- SplitArchive;
- Cleanup;
- IntegrityCheck;
- CatalogRebuild;
- Optimize;
- Vacuum;
- Migration;
- PolicyCleanup;
- StorageRelocation.

Каждая операция должна иметь явный scope по периоду и/или выбранным архивным сегментам.

После изменения архивов выполняется актуализация `storage-catalog.db`.

## 24. Catalog Rebuild

При отсутствии или повреждении `storage-catalog.db`:

```text
unlock with MasterKey
   ↓
open current.db
   ↓
scan Archive directory
   ↓
open each archive and read DatabaseIdentity
   ↓
validate real event periods
   ↓
rebuild ArchiveSegment catalog
   ↓
rebuild ArchiveDayOwnership
   ↓
rebuild external file reference index
   ↓
consistency validation
```

Если обнаружены конфликты периодов, каталог всё равно создаётся, но StorageState получает состояние, требующее обслуживания.

## 25. Локализация

Русские строки являются встроенным fallback.

UI использует ключи локализации через единый `ILocalizationService`.

Внешний файл локализации содержит language metadata и словарь `key -> translated text`.

Неполный перевод допустим: отсутствующие строки берутся из встроенного русского набора.

## 26. Journal Shell и глобальная горячая клавиша

Основное окно приложения — `JournalWindow`/Journal Shell на WinUI 3.

```text
Journal Shell
  ├── Журнал
  ├── Приложения
  ├── Обслуживание
  ├── Настройки
  └── О программе
```

Из журнала доступны все функции управления Clipensk; отдельные окна или страницы могут использоваться для специализированных операций, но не должны создавать альтернативный изолированный контур управления.

Глобальная горячая клавиша хранится в пользовательских настройках и обслуживается `IGlobalHotKeyService`. Win32-реализация использует `ResidentMessageWindow` и `RegisterHotKey`/`WM_HOTKEY`.

При смене комбинации последовательность должна быть безопасной:

1. запомнить действующую комбинацию;
2. снять её регистрацию;
3. попытаться зарегистрировать новую;
4. при неуспехе восстановить прежнюю комбинацию;
5. только после успешной регистрации считать новое значение действующим.

Конкретная комбинация по умолчанию пока не зафиксирована.
