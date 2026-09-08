# External payload Trash GC

Этот документ фиксирует production maintenance boundary для переноса history-unreferenced external payload objects из `Files` в `Trash`.

## Scope

Production implementation: `ProtectedExternalPayloadTrashCollector.CollectAsync`.

Операция работает только внутри active `ProtectedStorageSessionLease` и получает explicit `deletionDate` + cancellation token.

Этот boundary **не удаляет clipboard history rows**. Он предназначен для безопасного physical cleanup после того, как durable references уже исчезли из Current/Archive, либо для orphan objects, оставшихся после crash до history COMMIT.

## Authoritative live set

Source of truth для существования external payload reference — только persisted `ClipboardHistoryPayload` в Current + Archive.

Catalog `ExternalPayloadAddressIndex` остаётся rebuildable accelerator и сам по себе не считается durable reference.

Collector использует двухфазный coordination contract:

1. `ProtectedExternalPayloadCatalogRebuildService.RebuildAsync` под session mutation lease выводит projection только из authoritative Current + Archive history и удаляет stale reservations;
2. после rebuild collector получает тот же session mutation lease;
3. уже под своим lease collector повторно читает Catalog как **conservative keep-set**.

Это закрывает capture race между rebuild и GC:

- capture резервирует external address до Current history COMMIT, удерживая тот же mutation lease;
- если capture успел завершиться между двумя lease ownership, его новая reservation присутствует в post-lease keep-set и physical file не удаляется;
- если capture упал после reservation, но до history COMMIT, stale reservation может временно сохранить orphan file до следующего GC pass — это safe leak, а не data loss.

Поддерживаемый Current→Archive transfer не создаёт новый content address и сохраняет archive-first durable order, поэтому не создаёт новый last-reference race для collector.

## Managed file eligibility

Collector рассматривает только Clipensk-managed canonical objects:

```text
Files/<yyyy-MM-dd>/<64 lowercase hex SHA-256>.<lowercase extension>
```

Дополнительно:

- date directory должен быть exact canonical `yyyy-MM-dd`;
- объект должен находиться непосредственно внутри date directory;
- extension должен быть непустым;
- source bytes перед publication обязаны exact хэшироваться в SHA из filename;
- canonical-looking object с несовпадающим SHA завершает operation fail-closed;
- noncanonical/foreign files игнорируются и не перемещаются.

Catalog keep-set сравнивается по persisted relative path. Collector не придумывает новый Files address и не меняет Catalog rows после rebuild.

## Trash layout

Publication path:

```text
Trash/<deletion-date>/<original Files relative path>
```

Например:

```text
Trash/2026-09-08/2026-08-31/<sha>.png
```

Deletion-date grouping соответствует storage requirement, а сохранение исходного Files relative path не создаёт collision между одинаковыми content-addressed именами из разных historical placement dates.

Retention purge содержимого Trash является отдельным maintenance boundary. Этот collector не реализует 30-day purge сам по себе.

## Reparse-point containment

Managed filesystem paths не должны использовать junction/symlink/reparse traversal внутри `Files` или `Trash`.

Collector fail-closed проверяет:

- configured `Files` root;
- canonical source date directory;
- source file;
- существующие компоненты destination chain внутри `Trash`;
- newly created Trash directories после создания;
- существующий destination object.

Это предотвращает обычный reparse traversal за configured data root через managed subdirectories. Boundary не заявляет защиту от произвольного hostile kernel-level TOCTOU, способного заменить path component между последней userspace проверкой и filesystem syscall; такой threat model потребовал бы отдельного handle-based Windows filesystem contract.

## Preflight and publication

После получения mutation lease collector:

1. валидирует Catalog identity/schema/user_version;
2. materializes conservative keep-set;
3. перечисляет canonical managed Files objects;
4. для каждого orphan candidate проверяет exact source SHA/size;
5. если destination уже существует, проверяет exact destination SHA/size;
6. только после полного candidate preflight переходит к filesystem publication.

Для каждого planned object непосредственно перед publication source fingerprint выводится повторно. Изменившийся source завершается fail-closed.

Если exact destination уже существует, duplicate source может быть удалён только после повторной проверки destination SHA/size. Иначе source перемещается через `File.Move` в prepared Trash path.

## Cancellation and crash semantics

Cancellation проверяется до каждого individual filesystem publication и связана с protected-session cancellation.

Каждый successful `File.Move` или duplicate-source `File.Delete` является отдельным durable publication boundary. После уже выполненного move/delete late cancellation не демотирует этот individual result и не пытается перемещать объект обратно.

Следовательно batch может быть partially progressed, если cancellation/process failure происходит между объектами. Повторный запуск безопасно продолжает с оставшимися Files objects; уже перемещённые objects больше не enumerated из Files.

## Explicit non-goals

Этот boundary не реализует:

- удаление clipboard history references;
- изменение global/per-application capture policy;
- multi-DB Current+Archive cleanup перед policy mutation;
- retention purge самого Trash;
- восстановление missing external payload bytes;
- UI maintenance/recovery flow;
- hostile-kernel race-proof filesystem traversal.

Policy mutation для external formats должна сначала удалить запрещённые references из Current + Archive с корректным transaction/coordination contract, а затем использовать last-reference Trash cleanup; Current-only shortcut недопустим.

## Regression coverage

Storage tests покрывают:

- stale Catalog reservation + orphan physical object;
- live Current reference;
- live Archive reference;
- canonical object с неправильными bytes;
- игнорирование noncanonical files;
- existing exact Trash copy;
- cancellation до filesystem publication;
- ожидание session mutation lease;
- canonical Files date-directory symlink fail-closed на Windows runner, когда OS разрешает создание symbolic link.

Manual WinUI flow не относится к этому backend maintenance boundary и остаётся отдельным evidence.
