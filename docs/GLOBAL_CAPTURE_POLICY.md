# Глобальная capture policy — Current v5

## Принятый контракт

Глобальная policy принадлежит выбранному хранилищу и сохраняется в зашифрованной
`Current/current.db`. Она не является общей настройкой процесса в JSON и не хранится
только в rebuildable Catalog. Индивидуальные overrides остаются привязаны к `ApplicationId`.

При создании или миграции хранилища policy **не настроена**: обе таблицы пусты.
Отсутствие policy не превращается в `Allow`, `Deny`, пустую разрешающую policy или значения
форматов/лимитов по умолчанию. Пользователь должен явно выполнить первичную настройку.
До этого чтение истории после unlock допустимо, но обработка новых clipboard payload
не должна запускаться. Само сохранение policy не запускает worker.

Этот контракт принят по команде пользователя продолжить разработку после предложения
хранить policy в Current и выполнять явную первичную настройку без defaults.

## Schema

Current v5 добавляет:

| Таблица | Данные и ограничения |
|---|---|
| `GlobalCapturePolicy` | `SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (=1)`; `CaptureRule TEXT NOT NULL`, только exact `Allow` / `Deny` |
| `GlobalFormatCapturePolicy` | `SingletonId INTEGER NOT NULL CHECK (=1)`; `FormatName TEXT NOT NULL`; `CaptureRule TEXT NOT NULL`; nullable положительный `MaxBytes`; PK `(SingletonId, FormatName)`; FK к global header с `ON DELETE CASCADE` |

Global rules не имеют родительской policy: `Inherit` и неизвестные enum значения не
принимаются при первичной настройке. Для индивидуальных overrides `Inherit` сохраняет
существующую семантику. Имена форматов сравниваются ordinal/BINARY, без нормализации.
Repository дополнительно отклоняет пустые/whitespace имена и нецелые размеры.
`MaxBytes = null` сохраняет отсутствие заданного лимита; численные лимиты не выбираются.
Размер измеряется по `CLIPBOARD_CAPTURE_SIZE_LIMITS.md`.

В таблицы попадают только переданные caller rules. Для неуказанного формата repository
ничего не добавляет. Существующий selector читает формат только при итоговых
`Capture = Allow` и явном `Formats[name].Capture = Allow` после merge.
Global `Deny` является базовым правилом наследования; существующий application override
может заменить его. Это не новый безусловный выключатель всего мониторинга.

## Repository и границы операций

`IGlobalClipboardCapturePolicyRepository` реализован в
`SqliteGlobalClipboardCapturePolicyRepository`:

- `ReadAsync(token)` возвращает immutable policy либо `null` для не настроенного хранилища;
- `InitializeAsync(policy, token)` атомарно сохраняет **первую** policy;
- любая повторная инициализация, в том числе тем же значением, завершается ошибкой;
- update/delete API отсутствуют: отключение форматов требует отдельного policy cleanup
  согласно REQUIREMENTS §18, его нельзя заменить простой перезаписью rules.

Constructor не открывает БД. Read использует ReadOnly и один SELECT/snapshot для header
и всех formats, включая проверку orphan rows. Запись использует ReadWrite, immediate
transaction, проверку отсутствия policy и INSERT header + formats без upsert.
Конкурирующие первичные записи сериализуются SQLite; перезаписи победившей policy нет.

Операции привязаны к `ProtectedStorageSessionLease`, связывают caller/session cancellation,
проверяют отмену до/после открытия, при чтении rows и перед COMMIT. Ошибка или отмена до
COMMIT откатывает всю запись. После успешного COMMIT нет late-cancellation проверки,
превращающей сохранённую policy в ошибку отмены. Connections/readers освобождаются.
SQLite calls синхронны; preemptive interruption отдельного SQL-вызова не обещается.

Проверяются storage identity/Current role, точная поддерживаемая версия v5,
`user_version`, table/PK/FK shape и persisted rules. Некорректная policy не трактуется
как отсутствие настройки. Repository не создаёт и не мигрирует schema.
Возвращённые policy snapshots принадлежат caller; repository их не кэширует и не продлевает session.

## Миграция

`ProtectedStorageDatabaseService` создаёт новую пару как Current v5 / Catalog v2.
Для существующей пары сначала полностью проверяются обе БД. Затем Current v1/v2/v3
проходит существующие шаги до v4; новый v4→v5 отдельно создаёт пустые policy tables,
обновляет identity/version и commit-ит одну transaction. Catalog v1→v2 сохраняет отдельный шаг.

Не используются IF NOT EXISTS, seed или перенос rules из JSON. History, application identity,
индивидуальные policies и persisted external addresses не переписываются.
Отмена/ошибка в шаге v4→v5 оставляет v4; повторное открытие продолжает миграцию.
Malformed v4 history по-прежнему отклоняется до mutation; повышение latest version до v5
не отменяет обязательную validation history начиная с v4.

## Проверки и оставшийся этап

Тесты покрывают отсутствие defaults, exact round-trip через новую session, изоляцию
хранилищ, явный Deny, повторную настройку, неверные rules/schema/data, orphan rows,
отмену/lock/dispose, освобождение connection, rollback записи и повтор после сбоя.
Migration tests покрывают новую пару, сохранность v4 history/policies, повторное открытие,
некорректную вторую БД, malformed v4/v5 schema, SQL failure и отмену внутри migration.
Существующие v1/v2/v3 migration tests проверяют продвижение до latest Current.

Это storage boundary. UI первичной настройки, загрузка persisted policy в app composition,
custom-binary extension configuration, worker lifecycle и policy cleanup ещё не реализованы
этим этапом. `ProtectedClipboardCaptureServices` продолжает принимать явный policy snapshot;
последующий host должен загрузить его из этого repository и обработать `null` как NOT READY.
Конкретные format/size defaults по-прежнему не утверждены.
