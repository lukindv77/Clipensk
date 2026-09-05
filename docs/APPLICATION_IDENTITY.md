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

Для процесса без AUMID exact normalized executable path допускается как resolution alias, но не как durable key.

При первом наблюдении незнакомого path registry может создать новый `ApplicationId` и связать с ним этот exact path alias. Повторное наблюдение того же alias возвращает тот же `ApplicationId`.

Перемещение или переименование executable на ранее неизвестный path **не** должно автоматически считаться тем же приложением. Новый path является новым identity candidate до явного merge/rebind либо до будущего отдельно утверждённого equivalence mechanism.

Это намеренно fail-closed относительно ошибочного объединения разных Win32 applications.

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
- внутреннему Windows heuristic AppUserModelID, который Clipensk не может надёжно наблюдать как стабильный contract.

Эти признаки в будущем могут использоваться как UI hints для ручного merge/rebind, но не как silent durable equivalence.

## 6. Registry result

`IApplicationIdentityRegistry.ResolveOrCreateAsync` возвращает `ApplicationIdentityResolution`:

- `ApplicationId` — durable Clipensk key;
- `Basis` — evidence, по которому выполнено разрешение (`PackagedApplicationUserModelId` или `ExecutablePathAlias`);
- `WasCreated` — был ли создан новый durable identity на этом вызове.

Если observation не содержит ни AUMID, ни executable path, registry может вернуть `null`; он не должен создавать анонимную durable application identity из PID/HWND.

## 7. Persistence implication

Concrete policy/history schema может ссылаться на `ApplicationId` и больше не должна ждать универсального Windows-native durable key.

Отдельно остаются задачи реализации registry storage, alias uniqueness/conflict handling, explicit merge/rebind workflow и UI для управления найденными приложениями. Эти задачи не разрешают менять durable key на path/AUMID.
