# Crash-safe protocol ротации Archive

Этот документ фиксирует execution contract для автоматической ротации Current → новые Archive по требованиям `REQUIREMENTS.md` и `ARCHITECTURE.md`.

Ротация здесь означает создание **новых base Archive** (`archive_000001.db`, `archive_000002.db`, ...), перенос в них завершённых календарных дней из `current.db`, а затем безопасное удаление подтверждённых копий из Current. Это отдельная операция от `Archive Split`: split меняет уже существующий Archive, rotation создаёт новые Archive из Current.

Цель протокола — поддержать threshold policy по record count, физическому размеру Archive DB и календарному span без состояния `Current=no, Archive=no`, с roll-forward recovery после любого crash/cancellation boundary.

## 1. Основные инварианты

1. Rotation работает только после успешной разблокировки и только с active `ProtectedStorageSessionLease`.
2. В Archive переносятся только **завершённые** календарные дни: planned coverage не может включать текущий local `CalendarDate`.
3. Граница любого результата проходит только между календарными днями; один persisted `CalendarDate` никогда не делится между Archive.
4. Для любого `CalendarDate` сохраняется `ArchiveOwnerCount <= 1`.
5. Временное состояние `Current=yes, Archive=yes` допустимо и является основной crash-safe точкой восстановления.
6. Состояние `Current=no, Archive=no` недопустимо.
7. До удаления любой строки из Current соответствующий final Archive должен быть опубликован, полностью валиден и логически совпадать с подготовленным shadow output.
8. `storage-catalog.db` остаётся rebuildable projection и не является журналом незавершённой rotation operation.
9. Staging outputs сохраняются до завершения source purge и Catalog publication; после начала final publication recovery выполняется только roll-forward.
10. Новая rotation operation не начинается при существующей pending rotation, pending Archive Split или pending policy maintenance.
11. Конкурирующие maintenance operations не должны мутировать Current/Archive пока существует pending rotation.
12. После durable completion поздняя cancellation не должна демотировать успешную операцию в failure.

## 2. Threshold policy

`ArchiveRotationSettings` задаёт один или несколько положительных thresholds:

- `MaxRecordCount`;
- `MaxBytes`;
- `MaxCalendarDays`.

Если включено более одного threshold, `ThresholdMode` обязан быть задан явно:

- `Any` — rotation boundary срабатывает, когда candidate segment превысил хотя бы один включённый threshold;
- `All` — boundary срабатывает только когда candidate segment превысил все включённые thresholds.

Никакой hidden default для multi-threshold policy не используется.

### 2.1. Semantics превышения

Threshold считается превышенным только при строгом `value > limit`.

Ровно `value == limit` допустимо и само по себе не закрывает segment.

Если один календарный день сам превышает configured trigger combination, этот день остаётся целиком в одном Archive. День не дробится ради соблюдения count/size limit.

### 2.2. Record count

`MaxRecordCount` измеряет количество `ClipboardHistoryEvent`, а не число payload rows.

### 2.3. Calendar span

`MaxCalendarDays` измеряет inclusive calendar span между первой и последней **датой с переносимыми событиями** в candidate segment:

`lastEventDate.DayNumber - firstEventDate.DayNumber + 1`.

История может быть sparse. Отсутствующие event days не материализуются как synthetic records и сами по себе не создают пустые Archive.

Если большой gap между двумя event days приводит к превышению span threshold, boundary проходит перед более поздним event day. Gap между двумя Archive coverage может остаться без владельца.

Внутри одного Archive coverage пустые дни допустимы, если они находятся между первой и последней датой событий этого segment.

### 2.4. Physical size

`MaxBytes` — **физический размер SQLCipher Archive database file**, а не сумма `CanonicalByteCount`, `ExternalSizeBytes` или других logical payload sizes.

Размер измеряется только для полностью materialized staged Archive v1:

1. все write transactions завершены;
2. writer connection закрыт;
3. DB проходит обычный full Archive validator;
4. нет незавершённого WAL/shm state, который требуется для содержимого DB;
5. измеряется `FileInfo.Length` именно staged `.db` файла.

External payload bytes из `Files/` не входят в `MaxBytes`: Archive history хранит только их metadata/address references.

Pure Core planner не должен имитировать physical-size planning через additive payload metrics. Size-enabled planning принадлежит storage-backed staging layer.

## 3. Какие Current rows участвуют

Rotation рассматривает только дни, для которых в Current есть хотя бы одно событие и `CalendarDate < currentLocalDate`.

Input inventory содержит ordered unique event dates с record count. Calendar gaps разрешены и учитываются через разницу дат при `MaxCalendarDays`.

Текущий день никогда не включается даже если threshold уже превышен.

### 3.1. Append-only automatic boundary

Автоматическая rotation создаёт новые Archive после уже существующей исторической Archive области.

Перед preparation:

1. полностью валидируется весь physical Archive set;
2. coverage не должна пересекаться;
3. определяется maximum существующий `CoverageEndDate`;
4. если Archive set не пуст, любой eligible closed Current event с `CalendarDate <= maximum CoverageEndDate` завершает automatic rotation fail-closed; rotation является append-only и не заполняет старые gaps;
5. Current event, который дополнительно попадает внутрь конкретной существующей Archive coverage, трактуется как возможный interrupted/duplicate transfer state и также требует explicit recovery;
6. automatic rotation не используется для произвольного заполнения historical gaps между уже существующими Archive.

Сложные historical gap/repair cases остаются explicit maintenance/recovery задачей, а не скрытой автоматической эвристикой.

## 4. Ready segments и незакрытый tail

Threshold-driven rotation не должна автоматически архивировать последний under-threshold tail только потому, что operation была запущена.

Planning разделяет closed Current history на:

- zero or more **ready segments** — ranges, boundary которых реально сработала по policy;
- optional **tail** — самый новый candidate range, который ещё не достиг rotation boundary и остаётся в Current.

Пример для `MaxCalendarDays = 30` и 61 последовательного event day:

- ready: day 1..30;
- ready: day 31..60;
- tail: day 61.

Tail не получает Archive file и не входит в pending rotation marker.

Если первый event day сам превышает trigger combination, он является ready single-day segment.

Если ни один segment не ready, automatic rotation завершается no-op без marker и без final filesystem writes.

Существующий explicit manual Current → Archive transfer остаётся отдельной maintenance boundary. Этот protocol не вводит скрытый `force flush tail`; если такой UX потребуется, он должен быть оформлен как отдельная явная команда/contract.

## 5. Storage-backed greedy planning

Для count/span-only policy решение может быть получено pure planner.

При включённом `MaxBytes` planning строится на staged Archive candidates:

1. начать candidate с самого старого eligible event day;
2. выделить будущие canonical filename и DatabaseId для текущего segment;
3. построить validated staged Archive для текущего accepted range;
4. измерить physical `.db` size;
5. проверить trigger combination;
6. если single-day candidate уже trigger-ится, зафиксировать его как ready segment;
7. иначе попробовать extension до следующего event day, построив operation-local probe/shadow;
8. вычислить record count, inclusive calendar span и measured physical bytes extended candidate;
9. если extended candidate trigger-ится:
   - предыдущий accepted candidate становится ready segment;
   - более поздний day начинает следующий candidate;
10. если trigger не сработал:
   - extended candidate становится новым accepted shadow;
11. после последнего event day незакрытый candidate остаётся tail, если он сам не trigger-ится.

Последний accepted staged file каждого ready segment должен уже иметь exact final identity/coverage и не перестраиваться из приблизительных size estimates.

## 6. Имена и identity новых Archive

Rotation создаёт только unsplit base filenames:

`archive_BBBBBB.db`

с `SplitSequence = 0`.

Allocation:

1. перечислить все existing canonical Archive filenames;
2. взять maximum существующий `BaseNumber` независимо от split suffix;
3. первый новый base = `max + 1`, либо `1`, если Archive set пуст;
4. каждый следующий ready segment получает следующий base number;
5. gaps в старых base numbers автоматически не переиспользуются;
6. если canonical six-digit namespace исчерпан, operation завершается fail-closed.

Filename и новый non-empty `DatabaseId` становятся immutable частью rotation plan.

## 7. Preparation и staging

Preparation выполняется под `ProtectedStorageMutationLease`.

До первого authoritative write:

1. валидировать Current и Catalog current-version contracts;
2. валидировать весь physical Archive set;
3. проверить отсутствие Archive coverage overlap;
4. проверить отсутствие pending rotation/split/policy maintenance;
5. snapshot validated `ArchiveRotationSettings`;
6. построить closed Current event-day inventory;
7. построить ready-segment plan;
8. выделить final canonical filenames и DatabaseId;
9. создать operation id;
10. создать same-volume staging directory:

`Archive/.clipensk-archive-rotation-<OperationId>/`

11. построить полный staged Archive v1 для каждого ready segment;
12. staged DB должна содержать exact assigned coverage, exact planned identity и только соответствующие Current events;
13. referenced `ApplicationIdentity` rows копируются по существующему transfer contract;
14. external payload files не копируются и не перемещаются;
15. каждый staged Archive полностью валидируется;
16. staged outputs cross-check-ятся против Current:
   - exact EventId set по planned coverages;
   - no duplicate EventId между staged outputs;
   - exact event envelope/source metadata;
   - exact ordered payload rows;
   - persisted CalendarDate внутри assigned coverage;
17. повторно проверить, что reserved final filenames свободны;
18. только после готовности всего shadow set commit-ится durable pending marker.

До marker commit authoritative Current, final Archive и Catalog не меняются.

Crash/cancellation до marker commit может оставить только orphan staging directory. Такой staging не имеет права публиковаться и может быть удалён последующей explicit preparation cleanup после подтверждения отсутствия matching marker.

## 8. Durable pending marker

Rotation требует отдельного protected marker в Current. Catalog не хранит operation journal.

Предлагаемый следующий Current schema version добавляет максимум одну active `PendingArchiveRotation` и ordered `PendingArchiveRotationSegment`.

### Operation row

Immutable operation fields:

- `OperationId`;
- snapshot nullable thresholds: record count / bytes / calendar days;
- explicit threshold mode, когда thresholds > 1;
- `Phase`;
- `CreatedAtUtc`.

### Segment rows

Для каждого ready segment:

- `OperationId`;
- `SegmentOrder`;
- canonical unsplit `FileName`;
- planned `DatabaseId`;
- `CoverageStartDate`;
- `CoverageEndDate`;
- planned `RecordCount`;
- measured staged `PhysicalSizeBytes`.

Segment plan immutable после marker commit.

Coverage rows должны быть strictly ordered, non-overlapping и непустыми по событиям; gaps между segments разрешены.

### Durable phases

Initial marker создаётся только после полного staging, поэтому отдельная persisted `Planned` phase не требуется.

Фазы:

1. `ReadyToPublish`
2. `PhysicalPublished`
3. `CurrentPurged`
4. `CatalogPublished`

Phase двигается только вперёд и ровно на один шаг.

Marker очищается только после `CatalogPublished` и повторной final validation.

## 9. Physical publication

После `ReadyToPublish` operation становится roll-forward only.

Staging сохраняется как verified durable backup до конца operation.

Для каждого planned segment:

1. проверить staged file exact plan;
2. если canonical final file отсутствует:
   - скопировать staged DB в operation-specific temporary path на том же volume;
   - закрыть/flush file;
   - полностью валидировать temporary DB против planned identity/coverage;
   - атомарно `File.Move` без overwrite в final canonical filename;
3. если final canonical file уже существует:
   - переиспользовать его только если identity/coverage и logical history exact match соответствующему staged output;
   - иначе fail-closed без overwrite;
4. final DB повторно полностью валидируется.

После publication всех outputs:

- перечислить весь physical Archive set;
- валидировать every Archive;
- проверить global no-overlap;
- для каждого planned final выполнить logical exact cross-check со staged copy.

Только после этого marker → `PhysicalPublished`.

Current на этой фазе ещё не должен удаляться, поэтому даже crash после partial publication сохраняет полную исходную копию history в Current плюс verified staged outputs.

## 10. Source purge

После `PhysicalPublished` staged и final outputs сначала повторно cross-check-ятся.

Удаление Current должно переиспользовать существующую exact Current→Archive transfer механику, а не вводить вторую несовместимую purge implementation.

Implementation может потребовать refactor существующего `ProtectedCurrentToArchiveTransferService`, чтобы его exact compare/purge core мог использовать уже удерживаемый mutation lease без nested lease deadlock.

Для каждого planned segment:

1. target final Archive валиден и exact staged output;
2. reread оставшиеся Current events внутри segment coverage;
3. каждое оставшееся Current событие обязано exact match уже существующей Archive row;
4. только exact verified Current rows удаляются одной transaction;
5. concurrent mismatch откатывает purge;
6. cancellation непосредственно перед Current COMMIT;
7. successful Current COMMIT не демотируется late cancellation.

Partial purge между segments допустим.

Recovery повторяет purge idempotently: уже удалённые Current rows не восстанавливаются, потому что staging + final Archive остаются полными authoritative copies.

После обработки всех segments:

- ни одного Current event внутри planned coverages не должно остаться;
- final Archive ↔ staging logical equality повторно подтверждается;
- marker → `CurrentPurged`.

## 11. Catalog publication

Catalog rebuild выполняется **после** source purge.

Это важно для корректного `IsSealed`: до purge новые Archive имеют pending Current duplicates и Catalog закономерно считал бы их unsealed.

После `CurrentPurged`:

1. повторно validate Current + весь physical Archive set;
2. выполнить full Catalog replacement/rebuild из authoritative Current + Archive через существующий `ProtectedStorageCatalogReplacementService` или эквивалентный принятый mechanism;
3. полностью проверить `ArchiveSegmentIndex` consistency;
4. полностью проверить external payload address projection;
5. planned descriptors должны exact match final physical identities/coverage;
6. closed planned segments не должны иметь Current rows;
7. marker → `CatalogPublished`.

Catalog не является источником planned filenames, DatabaseId или coverage.

## 12. Завершение

После `CatalogPublished`:

1. повторно validate final planned Archive set;
2. validate rebuilt Catalog;
3. убедиться, что Current не содержит events внутри planned coverages;
4. удалить operation-local temporary publication files, если есть;
5. удалить staging directory;
6. очистить `PendingArchiveRotation` marker одной Current transaction.

Staging нельзя удалять раньше durable `CatalogPublished`, потому что при partial source purge он остаётся независимым verified recovery source.

## 13. Crash recovery

Recovery всегда читает durable marker, затем повторно проверяет actual filesystem/DB state. Phase может отставать от фактических side effects.

### Нет marker

Operation не существует.

Operation-local rotation staging без matching marker считается orphan pre-commit preparation state и не может публиковаться. Его допустимо удалить только после проверки отсутствия matching marker.

### `ReadyToPublish`

Staging для каждого planned segment обязателен и должен exact match plan.

Final Archive может содержать zero or more уже опубликованных planned files.

Recovery:

- exact matching finals переиспользуются;
- missing finals публикуются из staging;
- unexpected filename/identity/content collision — fail-closed;
- после полного physical validation phase → `PhysicalPublished`.

### `PhysicalPublished`

Все final planned Archive и staging outputs должны существовать и logical exact match.

Current может быть ещё полным либо частично purged, если crash произошёл между purge commits и phase advance.

Recovery idempotently продолжает exact source purge до отсутствия Current events в planned coverages, затем phase → `CurrentPurged`.

### `CurrentPurged`

Final/staging set должен быть полным, Current rows внутри planned coverages отсутствуют.

Recovery rebuild/validates Catalog и phase → `CatalogPublished`.

### `CatalogPublished`

Recovery повторяет final physical/Current/Catalog validation, затем удаляет staging и marker.

## 14. Fail-closed cases

Automatic continuation запрещена при любом из состояний:

- malformed/noncanonical Archive filename;
- Archive identity/schema/SQLCipher/quick-check failure;
- existing Archive coverage overlap;
- Current event day уже попадает в existing Archive coverage до новой rotation;
- reserved final filename занят unrelated file;
- final planned file отличается от staged identity/coverage/history;
- required staged output отсутствует до `CatalogPublished`;
- staged source cross-check не совпадает с Current до marker commit;
- Current remaining row конфликтует с final Archive row во время purge;
- Current содержит planned rows после заявленной `CurrentPurged`;
- Catalog projection не совпадает с authoritative Current + Archive;
- external payload address projection конфликтует;
- больше одного несовместимого durable maintenance marker активно одновременно;
- settings/plan содержат invalid threshold mode или non-positive limit;
- base filename namespace исчерпан.

Recovery не выбирает «правильный» файл по timestamp, размеру или имени эвристически.

## 15. Cancellation contract

- До marker commit: cancellation может остановить preparation; authoritative Current/Archive/Catalog остаются неизменными.
- После marker commit: cancellation не инициирует rollback published outputs или purged Current rows.
- Каждая durable phase остаётся resumable.
- После успешного irreversible COMMIT поздняя cancellation не превращает success в failure.
- Lock/session revocation всегда остаётся fail-closed boundary; recovery выполняется при следующей active session.

## 16. Конфликтующие maintenance operations

Pending rotation должна стать global maintenance boundary для операций, которые могут менять Current history, Archive topology/history или Catalog.

Минимально:

- новая Archive Split не стартует при pending rotation;
- новая rotation не стартует при pending split;
- policy-maintenance mutation не стартует при pending rotation;
- rotation не стартует при pending policy maintenance;
- explicit Current→Archive mutation не должна обходить pending rotation;
- startup recovery не должна молча выполнять несколько несовместимых pending mutations в неизвестном порядке.

Если после crash обнаружено более одного несовместимого durable marker, runtime остаётся suspended и требуется fail-closed diagnostic/recovery path.

## 17. Startup/runtime integration

Pending rotation recovery выполняется после unlock внутри существующей pre-runtime clipboard suspension.

Clipboard capture runtime возобновляется только после successful rotation recovery и остальных допустимых maintenance continuations.

Rotation recovery failure оставляет runtime suspended.

Maintenance UI должна уметь:

- показывать pending rotation phase;
- разрешать explicit «Продолжить ротацию»;
- блокировать конкурирующие mutations до завершения/диагностики.

Обычные read paths при stale physical/Catalog layout продолжают fail-closed; protocol не ослабляет unified history validation.

## 18. Scheduled/manual invocation

Scheduled rotation:

- запускается только при active unlocked protected session;
- использует local current date, зафиксированную в начале planning;
- не включает текущий календарный день;
- no-op, если ready segments отсутствуют;
- не создаёт скрытый empty Archive.

Ручной запуск rotation policy использует те же thresholds и recovery contract.

Explicit manual transfer конкретного range в уже существующий Archive остаётся отдельной существующей command boundary и не переопределяется этим protocol.

## 19. Implementation slices

Рекомендуемый порядок реализации:

1. **Planner semantics v2**
   - sparse ordered event days вместо synthetic contiguous day rows;
   - inclusive calendar span по датам;
   - ready segments + under-threshold tail;
   - ANY/ALL;
   - single-day oversize;
   - no physical-size approximation в Core.
2. **Current next schema + pending rotation repository**
   - operation/segment tables;
   - strict validation;
   - forward-only phases;
   - migration/new-storage initialization;
   - interaction guards с pending split/policy maintenance.
3. **Storage-backed preparation/shadow builder**
   - Current day inventory;
   - deterministic base filename allocation;
   - fixed DatabaseId;
   - physical-size probe/measurement;
   - staged Archive build;
   - Current ↔ staged exact cross-check;
   - marker commit only after full staging.
4. **Physical publisher**
   - retained staging;
   - temp-copy + atomic final move;
   - exact final/staging validation;
   - partial-publication recovery.
5. **Current purge phase**
   - refactor/reuse existing Current→Archive exact compare/purge mechanics;
   - idempotent partial purge recovery.
6. **Catalog finalizer + recovery coordinator**
   - full Catalog replacement after purge;
   - external projection validation;
   - cleanup/marker clear.
7. **Startup + maintenance integration**
   - pre-runtime recovery;
   - competing-operation gates;
   - pending UI/resume.
8. **Scheduler**
   - safe unlocked-session invocation;
   - no-op when policy has no ready segment.
9. **Settings UI**
   - record count / physical MB or bytes / calendar span;
   - explicit ANY/ALL when >1 condition enabled;
   - validation/error reporting.

## 20. Текущий implementation status

На момент введения этого protocol существуют foundation slices:

- additive application settings contract для Archive rotation;
- explicit ANY/ALL threshold mode contract;
- pure count/calendar planner foundation;
- существующий copy-first/idempotent Current→Archive transfer в уже созданный Archive;
- Archive v1 validation/create contract;
- Catalog replacement/rebuild mechanisms;
- Archive Split durable marker/recovery pattern.

Storage-backed automatic rotation, pending rotation marker, size-based planning, scheduler и UI ещё не считаются реализованными только из-за наличия этого документа.
