# External payload Catalog rebuild

Этот документ фиксирует production maintenance boundary для полного восстановления `ExternalPayloadAddressIndex` в `storage-catalog.db` из durable clipboard history.

## Purpose

`ExternalPayloadAddressIndex` — rebuildable accelerator:

```text
SHA-256(exact stored bytes) -> RelativePath + SizeBytes
```

Источником истины являются persisted external references в `ClipboardHistoryPayload` Current и Archive. Сам Catalog не является durable reference и не является единственным владельцем адреса.

`ProtectedExternalPayloadCatalogRebuildService` работает только внутри active `ProtectedStorageSessionLease` и полностью заменяет external-address projection после успешного preflight.

## Source set

Rebuild не использует paged unified journal API. Он исчерпывающе сканирует physical storage:

1. `Current/current.db` ReadOnly;
2. canonical top-level `Archive/archive_*.db` в ordinal filename order;
3. `Current/storage-catalog.db` открывается ReadWrite только после полного successful scan/preflight.

Current читается первым намеренно. Поддерживаемый Current→Archive transfer имеет durable order:

```text
Archive COMMIT -> Archive validation -> Current purge
```

Поэтому конкурентный transfer не может скрыть durable external reference от rebuild:

- если Current scan произошёл до purge, reference уже попал в projection;
- если Current scan произошёл после purge, Archive COMMIT уже состоялся и последующий Archive scan увидит reference;
- временный Current+Archive duplicate collapses только при exact одинаковом address metadata.

## Validation

Перед Catalog mutation обязательно проверяются:

- active protected session и StorageId;
- Current role/schema/user_version и `ClipboardHistorySqlSchema`;
- canonical exact Archive filename;
- каждый Archive полностью через `ProtectedArchiveDatabaseService.ValidateAsync`;
- Archive role/schema/user_version/history tables;
- Archive DatabaseId остаётся неизменным вокруг physical scan;
- archive filename set не меняется между preflight enumerations;
- supported payload-kind representation;
- external SHA lowercase 64-hex;
- non-negative size;
- `ExternalSizeBytes == CanonicalByteCount`;
- relative path не rooted и не выходит за configured `Files` root.

Malformed history, identity mismatch, archive layout instability или cancellation завершают rebuild fail-closed до replacement transaction.

## Projection collision rules

Projection keyed by exact SHA-256.

- exact повтор `SHA + RelativePath + SizeBytes` разрешён и схлопывается;
- один SHA с другим `RelativePath` или `SizeBytes` — `InvalidDataException`;
- один relative path для разных SHA — `InvalidDataException`;
- collision detection выполняется до открытия Catalog ReadWrite.

Таким образом rebuild не выбирает Current-wins/Archive-wins и не придумывает новый path.

## Physical Files semantics

Rebuild восстанавливает metadata projection из durable history и **не требует**, чтобы соответствующий physical file сейчас существовал.

Это принципиально: потеря или временное отсутствие `Files/...` объекта не должно уничтожать persisted content address. Восстановление самого файла выполняется отдельным external-store path, когда exact payload bytes снова доступны.

Rebuild также:

- не читает external file bytes;
- не пересчитывает SHA по filesystem contents;
- не перемещает файлы в Trash;
- не удаляет orphan physical files;
- не меняет `ArchiveSegmentIndex`.

Stale/orphan Catalog reservations, на которые больше нет history references, исчезают из rebuilt projection. Их physical cleanup реализован отдельным `ProtectedExternalPayloadTrashCollector`: collector сначала запускает этот authoritative rebuild, затем под новым session mutation lease перечитывает Catalog только как conservative race-safe keep-set и переносит verified history-unreferenced canonical objects из `Files` в date-grouped `Trash`. Полный contract зафиксирован в `EXTERNAL_PAYLOAD_TRASH_GC.md`.

## Mutation coordination

`ProtectedStorageSessionLease` предоставляет session-wide `AcquireMutationLeaseAsync`.

Тот же lease охватывает три критических boundary:

1. capture history sink — от момента **до external SHA reservation** до успешного Current history COMMIT;
2. external Catalog rebuild — от начала physical scan до завершения Catalog replacement;
3. external Trash GC publication — после предварительного authoritative rebuild, от post-rebuild Catalog keep-set materialization до завершения planned Files→Trash publications.

Это исключает окно, в котором rebuild мог бы удалить только что созданную SHA reservation до того, как соответствующая Current history row стала durable.

Для Trash GC lease ordering намеренно двухфазный: rebuild сначала удаляет stale reservations, затем collector получает новый lease и перечитывает Catalog. Capture, завершившийся между этими lease ownership, уже успевает создать reservation под тем же mutation lease, поэтому новый live payload попадает в conservative keep-set. Capture crash после reservation и до history COMMIT может дать только временный safe leak до следующего pass, но не data loss.

Lease wait объединяет caller cancellation с protected-session cancellation. Lock/dispose отменяет waiters через session token. `ProtectedStorageMutationLease.Dispose()` idempotent; semaphore намеренно живёт вместе с session object и не dispose-ится отдельно, поэтому поздний release после lock безопасен.

Поддерживаемый Current→Archive transfer не обязан брать этот lease для correctness external projection: его archive-first durable order вместе с Current-first rebuild scan уже предотвращает logical miss. Maintenance operations, которые удаляют или переписывают durable external references, обязаны либо участвовать в этом mutation coordination, либо доказать эквивалентный ordering contract до реализации.

## Catalog replacement transaction

После successful preflight:

1. Catalog identity/schema/user_version и v2/v3 tables валидируются;
2. начинается одна SQLite transaction;
3. `ExternalPayloadAddressIndex` очищается;
4. validated projection вставляется целиком;
5. cancellation проверяется непосредственно перед COMMIT;
6. COMMIT.

`ArchiveSegmentIndex` не затрагивается.

Если операция отменена или падает до COMMIT, прежняя external projection остаётся durable. После успешного COMMIT результат остаётся success и не демотируется поздней cancellation.

## Recovery scope

Этот service реализует **rebuild содержимого external-address projection внутри существующего валидного Catalog v3**.

Missing-Catalog recreation и existing-Catalog replacement реализованы отдельными explicit pre-session recovery boundaries. Оба строят Catalog из authoritative Current + Archive state и не превращают прежний Catalog в source of truth.

## Regression coverage

Storage/Core tests покрывают:

- Current-only rebuild;
- Archive-only reference recovery;
- missing physical external file;
- exact Current+Archive duplicate collapse;
- same-SHA metadata conflict before Catalog mutation;
- relative-path collision between different SHA before Catalog mutation;
- `ArchiveSegmentIndex` preservation;
- rebuild waiting on existing mutation lease;
- capture sink holding mutation lease before external resolution through Current COMMIT;
- mutation lease serialization, caller cancellation и protected lock cancellation.

Связанный Trash GC regression suite дополнительно покрывает stale reservation cleanup, Current/Archive live-reference preservation, physical SHA corruption fail-closed, cancellation, lease serialization и reparse-point containment.

Manual WinUI/real-clipboard smoke не относится к этому maintenance service и остаётся отдельным evidence.
