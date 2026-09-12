# Crash-safe protocol разделения Archive

Этот документ фиксирует execution contract для обязательной операции «Разделить архив» из `REQUIREMENTS.md`.

Цель протокола — разрешить физическое разделение одного существующего Archive на несколько календарных сегментов без состояния, в котором единственная durable-копия события может быть потеряна. `storage-catalog.db` остаётся rebuildable projection и не является журналом незавершённой операции.

## 1. Основные инварианты

1. Разделение выполняется только после успешной разблокировки и только под `ProtectedStorageMutationLease`.
2. Источник и весь физический Archive set валидируются до первого write.
3. Исходная назначенная coverage разбивается на непрерывные непустые `JournalDateRange`, которые:
   - покрывают исходный диапазон полностью;
   - не имеют gaps и overlaps;
   - состоят только из целых календарных дней.
4. Каждое событие источника по `CalendarDate` принадлежит ровно одному будущему сегменту.
5. До публикации replacement исходного файла все будущие сегменты должны быть полностью построены и проверены.
6. Новый сегмент никогда не становится единственной durable-копией данных до завершения проверки.
7. Старый полный исходный Archive сохраняется как backup до полной физической проверки нового набора и успешного rebuild/validation каталога.
8. Поздняя cancellation после необратимой publication-фазы не должна сообщаться как rollback. После начала publication операция должна либо завершиться, либо остаться durable pending и быть продолжена recovery path.
9. `storage-catalog.db` обновляется только после того, как физический набор Archive самодостаточен и валиден. Потеря Catalog после split не должна препятствовать его обычному rebuild из Current + Archive.
10. Новая split-операция не начинается, пока существует незавершённый pending split.

## 2. Имена и identity

Если выбран файл `archive_BBBBBB[_SSSS].db`, все новые файлы сохраняют тот же `BaseNumber`.

- Файл-источник сохраняет своё текущее canonical имя и становится первым по времени сегментом результата.
- Его `DatabaseId` сохраняется, но `CoverageStartDate/CoverageEndDate` изменяются на coverage первого результата.
- Каждый дополнительный сегмент получает новый непустой `DatabaseId`.
- Для дополнительных файлов выделяются следующие свободные положительные `SplitSequence` в семье `BaseNumber`, начиная после максимального уже существующего sequence.
- Вложенные имена (`archive_000025_0002_0001.db`) запрещены.
- Выделенные имена являются частью durable split plan и не пересчитываются при retry/recovery.

Пример при разделении `archive_000025.db` на три диапазона:

```text
archive_000025.db
archive_000025_0001.db
archive_000025_0002.db
```

Если в семье уже существуют `_0001` и `_0002`, новые результаты получают `_0003`, `_0004`, ... независимо от того, какой член семьи разделяется.

## 3. Durable pending marker

Split требует защищённого durable marker в Current. Он не хранится только в Catalog.

Первый backend slice должен добавить Current schema migration и таблицу `PendingArchiveSplit` с максимум одной активной операцией на storage.

Минимальный marker содержит:

- `OperationId` — GUID;
- исходный canonical `SourceFileName`;
- ожидаемый `SourceDatabaseId`;
- исходные `CoverageStartDate/CoverageEndDate`;
- сериализованный ordered plan будущих сегментов: canonical filename, DatabaseId и coverage;
- `Phase`;
- UTC timestamp создания marker.

Plan immutable после первого commit. Phase может двигаться только вперёд.

Рекомендуемые фазы:

1. `Planned`
2. `ReadyToPublish`
3. `PhysicalPublished`
4. `CatalogPublished`

Marker удаляется только после финальной validation и cleanup backup/staging.

Если Current содержит pending split, unlock/maintenance continuation должен сначала продолжить или безопасно остановить эту операцию; обычная новая split-команда должна завершаться fail-closed.

## 4. Staging

Для операции создаётся каталог на том же filesystem/volume, что и `Archive`:

```text
Archive/.clipensk-archive-split-<OperationId>/
```

В нём строится полный будущий набор для выбранного источника. Leaf filenames совпадают с canonical будущими именами, но staging directory не участвует в обычном Archive enumeration (`Archive/archive_*.db`).

Staging не является durable source of truth сам по себе: durable plan находится в Current marker, а до publication полный старый источник остаётся доступным.

## 5. Планирование (`Planned`)

Под mutation lease:

1. Проверить `Current + storage-catalog.db` current-version contract.
2. Перечислить и полностью валидировать все `Archive/archive_*.db`.
3. Проверить отсутствие overlaps через `StorageQueryPlanner.ValidateArchiveCoverage`.
4. Проверить, что выбранный source всё ещё совпадает с ожидаемыми `DatabaseId + coverage` из UI/command request.
5. Проверить split ranges как точное partition исходной coverage.
6. Выделить новые family suffixes без коллизий.
7. Сформировать immutable plan с фиксированными DatabaseId новых сегментов.
8. Commit `PendingArchiveSplit` в Current в фазе `Planned`.

До commit marker никакие Archive-файлы не изменяются.

## 6. Построение shadow set

После marker commit и всё ещё под mutation lease:

1. Создать/очистить только staging directory exact `OperationId`; чужой staging не удалять.
2. Построить отдельную SQLCipher Archive v1 DB для каждого planned segment.
3. Для первого сегмента использовать source `DatabaseId`; для остальных — IDs из plan.
4. Скопировать `ApplicationIdentity`/aliases и history rows, необходимые соответствующему сегменту.
5. События маршрутизировать только по уже сохранённому `CalendarDate`; timestamp не пересчитывать.
6. Не изменять external payload files: split меняет только DB references/placement.
7. Полностью валидировать каждую staged DB обычным Archive contract, включая identity, schema, coverage, foreign keys и `quick_check`.
8. Выполнить cross-check источника и union staged results:
   - exact EventId set совпадает;
   - ни один EventId не встречается более чем в одном output;
   - для каждого события/format row значения, нужные для истории, совпадают с source;
   - каждая строка находится внутри assigned coverage своего output.
9. Повторно проверить, что final canonical filenames ещё не заняты неожиданными файлами.
10. Перевести marker в `ReadyToPublish`.

Если ошибка возникает до `ReadyToPublish`, final Archive остаётся без изменений. Retry может удалить только staging exact operation и построить его заново из полного source.

## 7. Publication: copy-first, roll-forward only

После `ReadyToPublish` cancellation больше не используется как сигнал rollback.

Публикация выполняется в следующем порядке:

### 7.1. Дополнительные сегменты

Каждый staged дополнительный segment перемещается `File.Move` без overwrite из staging в `Archive/<canonical name>` на том же volume.

После каждого move final file повторно открывается и валидируется по exact planned identity/coverage.

Исходный Archive в этот момент ещё содержит полный набор данных. Следовательно, crash может временно оставить duplicates/overlapping physical coverage, но не потерю единственной копии.

### 7.2. Исходное имя

Когда все дополнительные final segments присутствуют и валидны:

1. staged first segment полностью валидируется ещё раз;
2. `File.Replace` заменяет исходный canonical source;
3. старые точные source bytes сохраняются как backup внутри staging operation directory;
4. replacement source повторно валидируется по planned first identity/coverage.

После этого physical Archive должен представлять final split set без coverage overlaps.

### 7.3. Physical validation

До изменения Catalog заново перечислить весь `Archive/archive_*.db`, валидировать каждую DB и выполнить global coverage validation.

Дополнительно проверить planned files и IDs/coverage exact plan.

Только после этого marker переходит в `PhysicalPublished`.

## 8. Catalog publication

После `PhysicalPublished` rebuild обеих Catalog v3 projections выполняется из authoritative Current + физического Archive тем же fail-closed mechanism, который уже используется maintenance/recovery code.

После rebuild обязательно:

1. `ProtectedArchiveSegmentCatalog.ValidateConsistencyAsync` либо эквивалентная полная проверка;
2. проверка, что planned segment descriptors exact match physical identities;
3. marker → `CatalogPublished`.

Catalog никогда не используется как источник planned split metadata.

## 9. Завершение

После `CatalogPublished`:

1. ещё раз проверить final physical planned set;
2. удалить backup старого полного source;
3. удалить staging directory operation;
4. удалить `PendingArchiveSplit` marker одним Current transaction;
5. вернуть caller final ordered descriptors.

После удаления backup rollback больше невозможен и не нужен: authoritative physical Archive + self-describing identities + rebuilt Catalog уже подтверждены.

## 10. Crash recovery

Recovery всегда начинается под mutation lease с чтения pending marker и проверки файлов, а не с предположения о фазе только по marker.

### `Planned`

Final Archive должен быть исходным. Staging может быть неполным. Безопасное действие: удалить/rebuild exact operation staging и снова построить shadow set.

### `ReadyToPublish`

Некоторые дополнительные files уже могут быть перемещены в final Archive. Source может всё ещё быть старым полным файлом либо уже replacement.

Recovery — **roll-forward**, не rollback:

- валидные уже опубликованные outputs переиспользуются только при exact planned identity/coverage;
- отсутствующие outputs берутся из staging либо заново строятся из сохранённого полного source/backup;
- если source всё ещё старый, publication дополнительных outputs завершается и затем выполняется `File.Replace`;
- если source уже replacement, старый backup обязан оставаться доступным до завершения physical/catalog validation.

Любой unexpected canonical file/identity collision — fail-closed, без overwrite.

### `PhysicalPublished`

Final physical set уже должен соответствовать plan. Recovery только повторно валидирует physical set и rebuild/validates Catalog.

### `CatalogPublished`

Recovery повторяет финальную physical/catalog validation, затем очищает backup/staging и marker.

Marker phase может отставать от фактической filesystem publication из-за crash между файловой операцией и Current commit; поэтому каждое действие recovery обязано быть idempotent и сначала сравнивать actual file identity/coverage с immutable plan.

## 11. Fail-closed случаи

Операция не должна продолжать publication автоматически, если обнаружено хотя бы одно из состояний:

- source DatabaseId/coverage отличается от marker plan и не соответствует уже опубликованному first segment;
- unexpected file занял reserved canonical filename;
- final/staged output имеет другой DatabaseId или coverage;
- source backup после replacement отсутствует до подтверждённого `CatalogPublished`;
- event-set cross-check не сходится;
- physical Archive coverage имеет overlap/gap, не объясняемый текущей `ReadyToPublish` phase;
- Current/Catalog/Archive не открываются активным MasterKey;
- SQLCipher/schema/foreign-key/quick-check validation не проходит.

Такие случаи требуют явной diagnostic/recovery операции; нельзя угадывать, какой файл «правильный».

## 12. Cancellation contract

- До `ReadyToPublish`: cancellation может остановить работу; final Archive должен остаться неизменным.
- После `ReadyToPublish`: cancellation не инициирует rollback опубликованных файлов. Операция либо завершает publication в текущем вызове, либо оставляет durable pending marker для roll-forward recovery.
- После durable completion поздняя cancellation не превращает success в failure.

## 13. Implementation slices

Реализацию следует делать последовательно:

1. Current schema + `PendingArchiveSplit` repository/validation/migration/tests.
2. Pure split planner: partition validation, family suffix allocation, immutable plan tests.
3. Shadow builder + exact source/output cross-check tests.
4. Publication/recovery state machine с fault-injection tests на границах каждой durable phase.
5. Catalog rebuild integration и end-to-end storage tests.
6. Maintenance UI: выбор Archive, split boundaries, explicit confirmation, progress/error reporting.

Storage slices требуют exact-main Build и Native SQLCipher evidence по правилам проекта.

## 14. Не входит в первый implementation tranche

- автоматическая ротация по size/count/span thresholds;
- изменение default rotation policy;
- background split scheduler;
- удаление внешних payload (split не меняет live reference set);
- hot backup/snapshot protocol.
