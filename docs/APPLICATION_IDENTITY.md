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

Отдельно остаются explicit merge/rebind workflow, optional future alias canonicalization и UI для управления найденными приложениями. Эти задачи не разрешают менять durable key на path/AUMID.

## 8. Repository atomicity

`IApplicationIdentityRepository` является persistence boundary для identity registry.

Его write operations обеспечивают alias uniqueness атомарно:

- `CreateAndBindAsync` создаёт `ApplicationId` и все разрешённые aliases observation как одну логическую операцию;
- `BindExecutablePathAliasAsync` не может молча перепривязать path, уже принадлежащий другому `ApplicationId`;
- concurrent resolve/create для одного alias не должен создавать две durable identities;
- race/conflict завершается `ApplicationIdentityConflictException`, а не last-write-wins.

Concrete SQLite repository хранит эту информацию в `current.db`, использует unique keys/transactions и работает только внутри активного `ProtectedStorageSessionLease`.
