# Application identity contract

Этот документ фиксирует durable identity приложений для policy/history и отделяет её от runtime process metadata.

## 1. Durable key

Единственным долговечным ключом приложения в данных Clipensk является `ApplicationId` — непустой GUID, создаваемый и управляемый самим Clipensk.

Ни PID, ни HWND, ни executable path, ни display name, ни publisher, ни file hash, ни AUMID не являются primary key таблиц policy/history.

Следствие: обновление runtime metadata не требует переписывать durable references на приложение.

## 2. Runtime observations

`ApplicationIdentityObservation` содержит только наблюдаемые признаки, по которым registry может найти или создать `ApplicationId`:

- `ApplicationUserModelId`;
- `ExecutablePath`.

`SourceApplication` и `InvocationApplication` остаются разными runtime concepts. Оба могут быть разрешены в один `ApplicationId`, но только через общий identity registry; сами runtime records не объединяются и не подменяют друг друга.

## 3. Packaged applications

Для packaged application AUMID является сильным resolution alias. Если AUMID уже известен registry, возвращается связанный `ApplicationId`. Если он наблюдается впервые, registry может создать новый `ApplicationId` и привязать к нему этот AUMID.

Если одновременно доступны AUMID и executable path, AUMID имеет приоритет как resolution evidence. Path может сохраняться как дополнительный observation/alias, но не должен переопределять существующую AUMID binding.

## 4. Unpackaged Win32 applications

Для процесса без AUMID exact executable path, полученный runtime resolver-ом, допускается как resolution alias, но не как durable key.

На текущем contract boundary path alias сравнивается как точное наблюдаемое строковое значение. Registry не должен молча вводить case-folding, path rewriting, symlink/final-path equivalence или install-location heuristics как доказательство тождества приложений. Более широкая canonicalization может быть добавлена только отдельным решением с conflict semantics.

При первом наблюдении незнакомого path registry может создать новый `ApplicationId` и связать с ним этот exact path alias. Повторное наблюдение того же exact alias возвращает тот же `ApplicationId`.

Перемещение, переименование или иное изменение наблюдаемого executable path **не** должно автоматически считаться тем же приложением. Новый path является новым identity candidate до явного merge/rebind либо до будущего отдельно утверждённого equivalence mechanism.

Это намеренно fail-closed относительно ошибочного объединения разных Win32 applications. Цена такого решения — возможные duplicate candidates после move/update; они исправляются merge/rebind, а не silent heuristic merge.

## 5. Запрещённые автоматические эвристики

Без отдельного архитектурного решения нельзя автоматически объединять identities только по:

- одинаковому display name;
- publisher/company name;
- имени файла без полного path;
- file hash;
- version resource;
- install directory prefix;
- PID/process lifetime;
- HWND;
- изменению регистра/формы path, если это не покрыто отдельным alias-canonicalization contract;
- внутреннему Windows heuristic AppUserModelID, который Clipensk не может надёжно наблюдать как стабильный contract.

Эти признаки в будущем могут использоваться как UI hints для ручного merge/rebind, но не как silent durable equivalence.

## 6. Registry result

`IApplicationIdentityRegistry.ResolveOrCreateAsync` возвращает `ApplicationIdentityResolution`:

- `ApplicationId` — durable Clipensk key;
- `Basis` — evidence, по которому выполнено разрешение (`PackagedApplicationUserModelId` или `ExecutablePathAlias`);
- `WasCreated` — был ли создан новый durable identity на этом вызове.

Если observation не содержит ни AUMID, ни executable path, registry возвращает `null`; он не создаёт анонимную durable application identity из PID/HWND.

`RepositoryApplicationIdentityRegistry` действует fail-closed:

- известный AUMID имеет приоритет;
- если AUMID и path уже связаны с разными `ApplicationId`, выбрасывается `ApplicationIdentityConflictException`;
- новый AUMID не привязывается автоматически к path, уже принадлежащему другой identity;
- известному AUMID можно добавить ранее неизвестный path alias, если repository подтверждает uniqueness;
- path-only observation использует существующий exact path alias либо создаёт новую identity.

## 7. Persistence implication

`ApplicationId` уже является concrete durable FK boundary для защищённых данных Current.

- Current v2+ хранит `ApplicationIdentity` и exact aliases.
- Current v3+ хранит индивидуальные capture policy overrides через FK на `ApplicationId`.
- `storage-catalog.db` не является source of truth для identity или policy.
- runtime `ClipboardSourceApplication` не передаётся в policy repository как ключ: capture pipeline сначала разрешает durable `SourceApplicationId`, и только после этого выполняется per-application policy lookup.

Explicit merge/rebind workflow специфицирован в §9 (продуктовое решение принято, реализация — отдельный заход). Optional future alias canonicalization и UI для управления найденными приложениями отдельно от §9 остаются нереализованными. Ни одна из этих задач не разрешает менять durable key на path/AUMID.

## 8. Repository atomicity

`IApplicationIdentityRepository` является persistence boundary для identity registry.

Его write operations обеспечивают alias uniqueness атомарно:

- `CreateAndBindAsync` создаёт `ApplicationId` и все разрешённые aliases observation как одну логическую операцию;
- `BindExecutablePathAliasAsync` не может молча перепривязать path, уже принадлежащий другому `ApplicationId`;
- concurrent resolve/create для одного alias не должен создавать две durable identities;
- race/conflict завершается `ApplicationIdentityConflictException`, а не last-write-wins.

Concrete SQLite repository хранит эту информацию в `current.db`, использует unique keys/transactions и работает только внутри активного `ProtectedStorageSessionLease`.

## 9. Merge/rebind identity, группы приложений и ретроактивная очистка

Продуктовое решение принято (2026-09-22), не реализовано. Реализация — отдельный заход,
сопоставимый по архитектурной сложности и цене ошибки с Archive Rotation/Split, поскольку включает
ретроактивное безвозвратное удаление зашифрованной истории (Current + Archive) и внешних файлов.
Техническая реализация, durable-фазы и этапы — `APPLICATION_GROUP_PROTOCOL.md`.

### 9.1 Группа — не новая сущность

Группа не получает собственный durable-ключ. Роль identity группы исполняет `ApplicationId`
родителя. У application identity появляется nullable `ParentApplicationId` (ссылка на
`ApplicationId` другой identity):

- `ParentApplicationId = null` — приложение обычное, отдельное, не состоит в группе;
- `ParentApplicationId` задан — приложение является ребёнком группы, чей `ApplicationId`
  совпадает с `ParentApplicationId`.

Физически `ParentApplicationId` хранится не колонкой `ApplicationIdentity`, а в отдельной таблице
Current `ApplicationGroupMember`: схема `ApplicationIdentity` общая для Current и Archive, и колонка
в ней потребовала бы миграции всех архивов (`APPLICATION_GROUP_PROTOCOL.md` §2).

Группа **плоская**: у identity, которая сама уже является чьим-то родителем (то есть на неё есть
ссылки `ParentApplicationId` от других identity), `ParentApplicationId` обязан оставаться `null`.
Больше одного уровня вложенности не допускается.

### 9.2 Эффективная capture policy ребёнка

Per-application capture policy repository по-прежнему индексируется по `ApplicationId` (§7), но для
identity с непустым `ParentApplicationId` эффективной политикой при разрешении capture всегда
является политика `ParentApplicationId`, а не собственная запись ребёнка (если она вообще
существует — см. 9.4). Один `ApplicationId` внутри группы не может иметь действующую политику,
отличную от политики родителя.

### 9.3 Триггеры ретроактивной очистки

Ретроактивная очистка истории запускается **только** в двух случаях:

1. первое назначение персональной policy приложению, у которого её раньше не было (переход из
   Inherit-без-персональной-policy в explicit персональную policy);
2. merge (§9.5) — применение к дочерним записям итоговой policy группы.

Редактирование уже существующей персональной policy и редактирование глобальной policy очистку
**не** запускают — оба случая действуют только на будущий capture.

Это **изменение** прежнего поведения, а не его сохранение: до этого решения `REQUIREMENTS.md` §18
требовал очистку при любом отключении параметра сбора, и это реализовано в
`ProtectedCurrentPolicyMaintenanceService` (глобальная policy) и
`ProtectedCurrentApplicationPolicyMaintenanceService` (policy приложения) с последующими фазами
Archive/Catalog/Trash. Решение принято пользователем 2026-09-22 повторно, уже с верным описанием
текущего поведения (первоначальный вариант был выбран по ошибочному описанию ассистента, будто правка
policy и так действует только на будущее). Очистка при правке уже настроенной и глобальной policy
подлежит снятию в рамках реализации §9; `REQUIREMENTS.md` §18 обновлён.

### 9.4 Гранулярность очистки

Очистка оценивает каждое **представление формата** внутри записи истории отдельно против
действующей policy — только по правилу Capture (Allow/Deny) эффективной policy для формата:

- представление, которое эффективная policy не разрешает, удаляется;
- `MaxBytes` при очистке **не** учитывается: лимит размера — gate только при захвате (решение
  пользователя 2026-09-22, совпадает с прежним поведением очистки);
- прошедшие представления той же записи не трогаются;
- если после удаления не прошедших представлений в записи не остаётся ни одного представления —
  удаляется вся запись целиком. Это правило действует для любой очистки по policy (до этого решения
  очистка оставляла пустые записи без представлений).

Очистка охватывает Current и **Archive полностью**, включая встроенные данные (текст, HTML, RTF,
ссылки, списки файлов), а не только ссылки на внешние файлы, как было до этого решения; архивы
временно открываются на запись как операция обслуживания.

Удаление внешних файлов (изображения, custom binary) выполняется существующим путём
`Files → Trash → retention` (`ProtectedExternalPayloadTrashCollector` /
`ProtectedExternalPayloadTrashRetentionService`), а не отдельным механизмом: файл переносится в Trash
только когда на него не осталось допустимых ссылок ни в Current, ни в Archive, и удаляется оттуда по
обычному сроку хранения Trash (`TrashRetentionDays`, по умолчанию 30 дней). До этого файл физически
остаётся на диске в незашифрованном виде — это осознанный выбор пользователя (2026-09-22) в пользу
запаса на случай ошибки. (Первоначально ассистент ошибочно описал этот путь как «неотличимый от
немедленного безвозвратного удаления»; ошибка исправлена, решение принято уже с верной вводной.)

Встроенные данные в зашифрованных БД удаляются с `PRAGMA secure_delete = ON`, чтобы удалённое
содержимое затиралось и в свободных страницах файла.

Перед любой очисткой — и при первом назначении policy, и при merge — обязателен экран подтверждения
с итоговыми цифрами (сколько представлений и записей будет удалено, сколько записей исчезнет
целиком) и возможностью по запросу посмотреть затронутые записи.

### 9.5 Merge — явное, направленное, подтверждаемое действие

Merge инициируется только пользователем и никогда не выполняется автоматически или по эвристике
(§5 остаётся в силе без изменений — совпадение имени/hash/издателя/пути само по себе не может быть
основанием ни для merge, ни для silent policy inheritance).

- merge всегда направлен: пользователь явно выбирает **дочернее** (`Child`) и **родительское**
  (`Parent`) приложение; после merge у `Child.ApplicationId` записывается
  `ParentApplicationId = Parent.ApplicationId`;
- итоговая policy группы — всегда policy `Parent` на момент подтверждения merge;
- если у `Child` была собственная персональная policy, экран merge обязан показать обе policy рядом
  и позволить пользователю до подтверждения перенести отдельные правила из policy `Child` в policy
  `Parent` (редактируя итоговую policy `Parent`); после подтверждения собственная policy `Child`
  удаляется — действует только 9.2;
- если `Parent` был ненастроен и на экране merge получает первую персональную policy, это
  одновременно первое назначение policy для группы `Parent` (§9.3, триггер 1): очистка
  распространяется и на записи самого `Parent` и его уже существующих детей;
- `Child`, который сам является родителем (у него есть дети), присоединить нельзя — это нарушило бы
  плоскость группы (§9.1);
- перед фактическим применением Clipensk обязан посчитать по всей истории `Child` (Current +
  Archive), какие представления не пройдут итоговую policy `Parent`, и показать пользователю
  итоговые цифры (сколько представлений/записей будет удалено, сколько записей исчезнет целиком) с
  возможностью по запросу развернуть список конкретных затронутых записей — прежде чем пользователь
  подтвердит операцию;
- после подтверждения выполняется очистка по правилам 9.4, применённым к истории `Child`.

### 9.6 Split — обратимость identity, необратимость уже выполненной очистки

Пользователь может выделить текущего ребёнка группы обратно в отдельное приложение:

- у ребёнка `ParentApplicationId` сбрасывается в `null`;
- **`ApplicationId` ребёнка не создаётся заново** — используется тот же durable-ключ, что был у него
  до merge (merge никогда не уничтожает и не заменяет identity ребёнка, только помечает её
  принадлежность к группе через `ParentApplicationId`);
- Clipensk спрашивает, переносить ли в отделяемую identity записи, источником которых является этот
  ребёнок и которые сейчас принадлежат группе — и принесённые им при merge, и накопленные позже (см.
  9.7). При отказе эти записи остаются в группе родителя, сохраняя происхождение от ребёнка;
- split не восстанавливает записи, безвозвратно удалённые на этапе merge (9.5) — эта очистка уже
  необратимо совершена, split откатывает только группировку identity, а не историю;
- после split выделенное приложение — ненастроенный корень: его собственная policy, удалённая при
  merge, не восстанавливается (§9.8).

Split **с переносом** записей — одна транзакция: у ребёнка снимается членство, и его записи сразу
следуют за ним, потому что принадлежность группе вычисляется из членства, а не хранится в записях.

Split **без переноса** реализуется через **удерживающую identity**: Clipensk создаёт новую identity
R без aliases (она никогда не разрешается при захвате), делает её членом группы родителя с пометкой
«удержана от ребёнка C» и переводит на неё прежние записи C в Current и во всех Archive. Так записи
остаются в группе родителя и сохраняют происхождение (в журнале — «<имя C> (до выделения)»), а
новые записи C принадлежат уже отдельному C. Архивы при этом не мигрируются
(`APPLICATION_GROUP_PROTOCOL.md` §7.2). В первой реализации удерживающую identity нельзя выделить из
группы или присоединить к другой группе.

### 9.7 Провенанс записей в журнале и фильтрация по группе

Запись истории сохраняет точное происхождение — исходный `ApplicationId` источника (родителя или
конкретного ребёнка), даже после merge. Merge не переписывает `SourceApplicationId` ни у записей
`Parent`, ни у записей `Child` — иначе 9.6 (перенос атрибутируемых ребёнку записей при split) была
бы невозможна, а разрез аналитики по бывшим детям внутри группы терялся бы. Единственная операция,
которая переписывает `SourceApplicationId`, — split без переноса: записи C переходят на
удерживающую identity, которая сама хранит ссылку на C, так что происхождение не теряется.

Журнал фильтрует «по группе» как по identity родителя и всех его текущих детей одновременно, с
возможностью включать/выключать в этом фильтре отдельных членов группы по отдельности.

### 9.8 Не настроенные приложения

Приложение без персональной policy (`ParentApplicationId = null`, собственной policy-записи нет)
работает по глобальной policy. Записи такого приложения в журнале явно помечены как не имеющие
персональных настроек. Список таких приложений доступен из журнала — это точка входа, из которой
пользователь либо задаёт приложению персональную policy (§9.3, триггер 1), либо присоединяет его как
`Child` к уже настроенному `Parent` (§9.5, триггер 2).
