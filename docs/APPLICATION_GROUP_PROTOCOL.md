# Протокол групп приложений и очистки истории по политике

Статус: **спроектировано, не реализовано.** Продуктовые решения — `APPLICATION_IDENTITY.md` §9,
`OPEN_QUESTIONS.md` §11, `REQUIREMENTS.md` §18 (решения пользователя от 2026-09-22). Этот документ
фиксирует техническую реализацию: модель данных, durable-фазы, восстановление после сбоя и порядок
этапов (slices).

Протокол устроен как roll-forward: после commit первой фазы операция только доводится до конца,
отката нет. Каждая фаза идемпотентна и пересчитывает свой набор изменений из зафиксированного
правила, а не из запомненного списка строк.

## 1. Инварианты

1. Durable-ключ приложения остаётся `ApplicationId` (`APPLICATION_IDENTITY.md` §1). Ни группа, ни
   merge/split не меняют durable-ключи и не выводят тождество из пути/имени/hash.
2. Группа — не отдельная сущность. Группу представляет `ApplicationId` её **корня** (родителя).
   Членство хранится только в Current.
3. Группа плоская: корень не может быть членом другой группы, член не может иметь своих членов.
4. У члена группы нет собственной capture policy. Эффективная policy при захвате —
   `Merge(global, policy(корень))`.
5. Историю по политике чистят **только** две операции: первое назначение персональной policy
   ненастроенному корню и merge. Правка глобальной policy и уже заданной персональной policy
   историю не трогает.
6. Правило очистки — только Allow/Deny эффективной policy по `FormatName` представления. `MaxBytes`
   при очистке не учитывается.
7. Очистка охватывает Current и **все** Archive, включая встроенные представления. Запись без
   оставшихся представлений удаляется целиком.
8. Внешний файл уходит в Trash через существующий last-reference путь (Catalog rebuild →
   `ProtectedExternalPayloadTrashCollector`) и удаляется оттуда по обычному сроку хранения.
9. Пока существует durable marker любой из операций этого протокола, capture не работает:
   protected delivery composition fail-closed при наличии `PendingPolicyMaintenance` (уже
   существующее поведение), а App держит runtime в приостановке до снятия marker. Поэтому набор
   записей в области операции между фазами не пополняется.
10. Пока marker существует, изменение policy, членства и повторный merge/split отклоняются
    (`PendingPolicyMaintenanceException`), как и для существующей policy maintenance.
11. Удаление в SQLite выполняется с `PRAGMA secure_delete = ON`: удалённое содержимое затирается и
    в свободных страницах зашифрованного файла.
12. Archive schema остаётся v1. Операции этого протокола не требуют миграции архивов.

## 2. Модель данных: Current v11

### 2.1 Таблица `ApplicationGroupMember`

```sql
CREATE TABLE ApplicationGroupMember (
    ApplicationId TEXT NOT NULL PRIMARY KEY,
    ParentApplicationId TEXT NOT NULL,
    RetainedFromApplicationId TEXT NULL,
    JoinedAtUtc TEXT NOT NULL,
    CHECK (ApplicationId <> ParentApplicationId),
    CHECK (RetainedFromApplicationId IS NULL OR RetainedFromApplicationId <> ApplicationId),
    FOREIGN KEY (ApplicationId) REFERENCES ApplicationIdentity(ApplicationId),
    FOREIGN KEY (ParentApplicationId) REFERENCES ApplicationIdentity(ApplicationId),
    FOREIGN KEY (RetainedFromApplicationId) REFERENCES ApplicationIdentity(ApplicationId)
);

CREATE INDEX IX_ApplicationGroupMember_ParentApplicationId
    ON ApplicationGroupMember(ParentApplicationId);
```

- строка есть — приложение является членом группы `ParentApplicationId`;
- строки нет — приложение само является корнем (одиночным либо с членами);
- `RetainedFromApplicationId` задан только у **удерживающей identity** (§7.2);
- FK без каскада: identity в Clipensk не удаляются, а попытка удалить identity, участвующую в
  группе, должна завершаться ошибкой, а не молча менять группу.

Таблица отдельная, а не колонка в `ApplicationIdentity`, потому что `ApplicationIdentitySqlSchema`
общий для Current и Archive: колонка в `ApplicationIdentity` потребовала бы миграции всех архивов.

Валидация (fail-closed при чтении любого контракта v11):

- canonical GUID во всех трёх колонках, UTC `JoinedAtUtc`;
- плоскость: нет строки, у которой `ParentApplicationId` сам присутствует как `ApplicationId`;
- у члена группы нет строк в `ApplicationCapturePolicy`/`ApplicationFormatCapturePolicy`;
- у удерживающей identity нет aliases (она никогда не разрешается при захвате).

### 2.2 Миграция v10 → v11

По образцу существующих шагов (`CURRENT_DATABASE_SCHEMA.md`): валидировать все контракты v10,
создать пустую `ApplicationGroupMember` и индекс, `DatabaseIdentity.SchemaVersion 10 → 11`,
`PRAGMA user_version = 11`, проверка отмены и COMMIT. Новое хранилище создаётся сразу как v11.
Existing identity/policy/history не переписываются.

### 2.3 Производные понятия

- `Root(X)` = `ParentApplicationId` строки X, иначе X.
- `Members(R)` = `{R}` ∪ все `ApplicationId` с `ParentApplicationId = R`.
- «Настроен» корень R ⇔ существует строка `ApplicationCapturePolicy` для R. Персональную policy
  можно задать только корню; редактор policy для члена группы открывает policy корня.
- Запись истории с `SourceApplicationId = NULL` не принадлежит ни одному приложению и операциями
  этого протокола не затрагивается.

## 3. Изменение policy после решения 2026-09-22

| Действие | Что происходит |
|---|---|
| Правка глобальной policy | одна транзакция: публикация policy; очистки нет; marker не создаётся |
| Правка policy **настроенного** корня | одна транзакция: публикация policy (и custom-binary mappings); очистки нет |
| Первое назначение policy **ненастроенному** корню | операция `ApplicationHistoryPurge` (§5), причина `FirstAssignment`, область — `Members(R)` |
| Merge | операция `ApplicationHistoryPurge` (§6), причина `Merge` |
| Split с переносом записей | одна транзакция (§7.1) |
| Split без переноса | операция `ApplicationGroupSplit` (§7.2) |

Runtime на время любого из этих действий приостанавливается тем же App-механизмом, что сейчас
(`TryQuiesceClipboardRuntimeAsync`), и возобновляется только когда marker отсутствует.

Существующие виды marker `GlobalCapturePolicyMaintenance` и `ApplicationCapturePolicyMaintenance`
больше **не создаются**, но их resume-путь сохраняется без изменений: marker, оставшийся после сбоя
в старой версии, доводится до конца по старым правилам.

## 4. Предпросмотр очистки

`ProtectedApplicationHistoryPurgePreviewService` (только чтение) получает область — набор
`SourceApplicationId` — и эффективную policy, открывает Current и каждый Archive ReadOnly и
возвращает:

- число удаляемых представлений по каждому `FormatName`;
- число записей, которые будут усечены (часть представлений остаётся);
- число записей, которые исчезнут целиком;
- число удаляемых ссылок на внешние файлы;
- всё это отдельно для Current и Archive.

Детальный просмотр «записи для выбранных представлений» — обычное чтение журнала
(`ProtectedUnifiedClipboardHistoryRepository`) с фильтром по набору источников и по `FormatName`.

Предпросмотр — снимок на момент показа. Применение пересчитывает затронутые строки тем же правилом
уже под приостановленным runtime; фактические цифры возвращаются в результате и показываются после
завершения. Записи, успевшие появиться между предпросмотром и подтверждением, подпадают под то же
правило, которое пользователь подтвердил.

## 5. Операция `ApplicationHistoryPurge`

### 5.1 Marker

Используется существующий singleton `PendingPolicyMaintenance` с новым `OperationKind =
"ApplicationHistoryPurge"`. `StateJson` v1:

```json
{
  "version": 1,
  "reason": "FirstAssignment | Merge",
  "rootApplicationId": "<guid>",
  "childApplicationId": "<guid> | null",
  "sourceApplicationIds": ["<guid>", "..."],
  "effectivePolicy": { "capture": "Allow|Deny", "allowedFormats": ["..."] },
  "rootPolicyFingerprint": "<sha256 | null>",
  "archive": "pending|completed",
  "catalog": "pending|completed",
  "trash": "pending|completed"
}
```

- `sourceApplicationIds` — отсортированный набор источников области, зафиксированный при старте;
- `effectivePolicy` — только то, что нужно правилу очистки: общее правило и множество форматов с
  эффективным Allow. Фазы Archive/Catalog/Trash не перечитывают policy — правило берётся из marker;
- `rootPolicyFingerprint` — отпечаток сохранённой персональной policy корня (или `null`, если корень
  остаётся ненастроенным). Каждая фаза сверяет его с Current и завершается fail-closed при
  расхождении;
- фазы идут строго по порядку; завершённая фаза не может стоять после незавершённой.

### 5.2 Правило очистки

Представление `p` записи `e` удаляется, если `e.SourceApplicationId ∈ sourceApplicationIds` и
**не** выполняется `effectivePolicy.capture = Allow ∧ p.FormatName ∈ allowedFormats`. После удаления
представлений удаляется каждая запись `e` из области, у которой не осталось ни одного представления
(включая записи, пустые ещё до операции).

### 5.3 Фазы

1. **Current** — инициирующая транзакция под mutation lease (`deferred: false`, `secure_delete`):
   - проверить предусловия (§5.4 для `FirstAssignment`, §6.1 для `Merge`) и отсутствие любого
     pending marker;
   - опубликовать policy корня (и новые custom-binary mappings, как в существующем mapping-aware
     пути);
   - для `Merge` — изменения членства (§6.2);
   - удалить представления и опустевшие записи в Current по правилу §5.2;
   - создать marker (все остальные фазы `pending`);
   - проверка отмены непосредственно перед COMMIT; после COMMIT поздняя отмена результат не
     понижает.
2. **Archive** — под mutation lease: перечислить canonical архивы, каждый полностью валидировать
   ReadOnly, открыть ReadWrite, удалить представления и опустевшие записи по правилу §5.2 одной
   транзакцией на архив (`secure_delete`), повторно валидировать identity. Набор архивов не должен
   меняться за время фазы. Затем отметить фазу в marker.
3. **Catalog** — `ProtectedExternalPayloadCatalogRebuildService` из authoritative Current+Archive,
   затем отметка фазы.
4. **Trash** — `ProtectedExternalPayloadTrashCollector` с датой удаления, затем отметка фазы.
5. **Completion** — сверить marker и отпечаток policy, удалить marker.

Фазы 2–5 выполняются общим resume-координатором; инициатор вызывает его сразу после COMMIT фазы 1.
`ProtectedPolicyMaintenanceResumeDispatcher` направляет новый вид marker в этот координатор, а
startup-последовательность (`ProtectedStorageStartupRecoveryCoordinator`) доводит его до конца до
возобновления capture.

### 5.4 Предусловия `FirstAssignment`

- R существует и является корнем (не член группы);
- у R нет строки `ApplicationCapturePolicy`;
- глобальная policy существует;
- область = `Members(R)` на момент транзакции.

## 6. Merge

### 6.1 Предусловия

- C ≠ P; обе identity существуют;
- C — корень без членов (не состоит в группе и сам не является родителем);
- P — корень (не член группы);
- ни C, ни P не являются удерживающей identity;
- отсутствует любой pending marker.

### 6.2 Действия фазы Current

- вставить `ApplicationGroupMember(C → P)`;
- удалить собственную policy C (`ApplicationCapturePolicy`/`ApplicationFormatCapturePolicy`) — после
  merge она не действует; правила, которые пользователь захотел сохранить, он переносит в policy P
  на экране merge до подтверждения;
- опубликовать итоговую policy P, если она задана/изменена на экране merge;
- область очистки:
  - всегда `{C}`;
  - плюс `Members(P)`, если P был ненастроен и получает первую policy в этой же операции (это одновременно
    первое назначение для P);
- эффективная policy = `Merge(global, итоговая policy P)`; если P остаётся ненастроенным — `global`.

После split (§7) бывший член группы — ненастроенный корень: его прежняя собственная policy не
восстанавливается.

## 7. Split

### 7.1 С переносом записей

Одна транзакция Current: удалить строку `ApplicationGroupMember` ребёнка. Записи с
`SourceApplicationId = C` сразу принадлежат C как отдельному корню, потому что принадлежность группе
вычисляется из членства, а не хранится в записях. Marker не нужен.

### 7.2 Без переноса записей — удерживающая identity

Записи должны остаться в группе P, сохранив происхождение от C, а новые записи C — принадлежать
отдельному C. Архивы при этом не мигрируются: записи C переводятся на новую **удерживающую
identity** R.

Операция `ApplicationGroupSplit`, marker `StateJson` v1:
`{ "version": 1, "parentApplicationId", "childApplicationId", "retainedApplicationId", "archive": "pending|completed" }`.

1. **Current** (одна транзакция): создать `ApplicationIdentity` R без aliases; вставить
   `ApplicationGroupMember(R → P, RetainedFromApplicationId = C)`; перевести записи Current
   `SourceApplicationId C → R`; удалить членство C; создать marker.
2. **Archive**: в каждом архиве, где есть записи C, — вставить строку R в `ApplicationIdentity`
   архива (если её нет) и перевести записи `C → R`; затем отметить фазу.
3. **Completion**: удалить marker.

Корректность фазы Archive опирается на инвариант 9: пока marker существует, новых записей C нет,
поэтому все записи C в архивах на этой фазе — созданные до split.

В журнале удерживающая identity показывается как «<имя C> (до выделения)». В первой реализации её
нельзя выделить из группы или присоединить к другой группе.

## 8. Журнал

- Запись, у которой `Root(SourceApplicationId)` не настроен, помечается «нет индивидуальных
  настроек».
- Список ненастроенных корней доступен из журнала; из него открывается первое назначение policy
  (§5) или merge (§6).
- Фильтр по приложению становится фильтром по группе: `SourceApplicationId ∈ выбранные члены`.
  Репозитории Current/Archive/unified принимают набор идентификаторов вместо одного `Guid?`.
  Архивы при этом не меняются: членство разрешается в Current до запроса.

## 9. Отмена и сбои

- Каждая фаза держит mutation lease и проверяет отмену перед COMMIT; COMMIT не понижается поздней
  отменой.
- Сбой после COMMIT фазы Current оставляет marker; runtime остаётся приостановленным до ручного или
  стартового resume.
- Любое расхождение (identity архива, набор архивов, отпечаток policy, членство, неожиданный вид
  marker) — fail-closed без частичного «исправления».

## 10. Этапы реализации

Каждый этап промотируется отдельно по exact-SHA evidence. Этапы, меняющие `src/Clipensk.Storage/**`
или `src/Clipensk.Core/Storage/**`, требуют Build **и** Native SQLCipher; этапы только `Clipensk.App`
— Build и ручной dispatch Native на том же SHA (он единственный проверяет publish-путь App).

1. Этот протокол + согласование `APPLICATION_IDENTITY.md` §9.
2. Current v11: `ApplicationGroupMember`, миграция v10→v11, валидация, repository групп (чтение +
   `…InTransaction`-помощники).
3. Эффективная capture policy через корень группы; API чтения групп и «настроенности» для UI.
4. Правка глобальной и уже заданной персональной policy без очистки (publish-only); App-обвязка;
   resume старых marker сохраняется.
5. Движок `ApplicationHistoryPurge`: codec marker, фаза Current для `FirstAssignment`, фазы
   Archive/Catalog/Trash/Completion, resume-координатор, маршрутизация в dispatcher и startup.
6. Предпросмотр очистки + фильтр журнала по `FormatName`.
7. UI первого назначения: предпросмотр → подтверждение → применение; пометка «нет индивидуальных
   настроек» и список ненастроенных приложений в журнале.
8. Merge на движке этапа 5.
9. UI merge: выбор родителя, обе policy рядом, перенос правил, предпросмотр, подтверждение.
10. Split: с переносом (одна транзакция) и без переноса (`ApplicationGroupSplit`, удерживающая
    identity, resume).
11. UI групп: состав группы в настройках корня, split с вопросом о переносе, отображение
    удерживающей identity.
12. Фильтр журнала по группе с переключением членов (набор источников в репозиториях Current,
    Archive и unified).
13. Финальное обновление документации и handoff.

### Accepted

Все этапы ниже промотированы в `main` fast-forward (`b431d82..f88d524`, 2026-09-22); evidence —
dispatch-прогоны на feature-ветках на тех же exact SHA, до промоушена.

| Этап | Commit | Build | Native SQLCipher |
|---|---|---|---|
| 1. Протокол | `b431d82` | `35758391270` — SUCCESS (push, docs-only) | не требовался |
| 2. Current v11 + repository групп | `d57523e` | `35759180102` — SUCCESS | `35759183138` — SUCCESS |
| 3. Эффективная policy через корень | `922cdd8` | `35760096683` — SUCCESS | `35760100602` — SUCCESS |
| 4. Правка policy без очистки | `94f2459` | `35760103390` — SUCCESS | `35760106905` — SUCCESS |
| 5. Движок `ApplicationHistoryPurge` | `f88d524` | `35761173067` — SUCCESS | `35761176193` — SUCCESS |

Этап 5 намеренно не подключён к App: первое назначение персональной policy в App пока идёт прежним
путём `ProtectedCurrentApplicationPolicyMaintenanceService` (очистка без подтверждения, Archive —
только ссылки на файлы). Переключение на движок — в этапе 7 вместе с предпросмотром и
подтверждением.
