# Archive Rotation protocol

Status: **DESIGN CONTRACT — implementation pending beyond the existing settings/pure-planner foundation.**

This document defines crash-safe automatic Archive rotation for Clipensk. It complements
`ARCHIVE_DATABASE_SCHEMA.md`, `STORAGE_CATALOG_SCHEMA.md`,
`ARCHIVE_SPLIT_PROTOCOL.md`, and the existing Current→Archive transfer services.

The protocol is intentionally copy-first and roll-forward. A threshold decision may choose where
a new Archive boundary belongs, but it must never make a partially published Archive the only
durable copy of clipboard history.

## 1. Scope and invariants

Archive rotation moves a contiguous prefix of **closed** Current calendar days into one or more
new base Archive databases.

Hard invariants:

1. Only days with `CalendarDate < currentLocalDate` are eligible.
2. Rotation boundaries are always between whole calendar days.
3. A single calendar day is never split because a threshold is reached or exceeded.
4. Existing Archive assigned coverage is authoritative and is never extended in-place by rotation.
5. New rotation outputs use new unsplit base Archive names. Split suffixes remain reserved for
   Archive Split.
6. Current remains the durable source until every new Archive target has been built, published,
   and validated.
7. Source rows are purged only after exact Archive verification.
8. Catalog remains rebuildable projection, never the durable operation journal.
9. Normal uninterrupted execution is serialized by `ProtectedStorageMutationLease`. Application
   integration must keep clipboard capture suspended while a pending rotation is being executed or
   recovered.
10. After publication begins, recovery is roll-forward only.
11. A new rotation does not start while another pending rotation or pending Archive Split exists.

Manual Current→Archive transfer remains a separate explicit maintenance operation. This protocol
defines automatic/configured rotation.

## 2. Threshold semantics

`ArchiveRotationSettings` can enable:

- `MaxRecordCount`;
- `MaxBytes`;
- `MaxCalendarDays`;
- explicit `ThresholdMode = Any | All` when more than one threshold is enabled.

For one enabled threshold, `Any` and `All` are equivalent and no mode is required.

Thresholds are evaluated only after a **complete calendar day** has been added to the candidate
segment.

For a candidate segment:

- record metric = number of `ClipboardHistoryEvent` rows in the segment;
- calendar metric = inclusive day span
  `EndDate.DayNumber - StartDate.DayNumber + 1`, including zero-record days inside the range;
- byte metric = physical length of the closed, validated SQLCipher Archive `.db` file. Clipboard
  canonical payload byte counts are not a substitute for this metric.

A threshold predicate is reached when:

```text
metric >= configured threshold
```

Combination rule:

```text
Any => at least one enabled predicate is reached
All => every enabled predicate is reached
```

Once the combination rule is true after a day, that whole segment becomes **ready for rotation**.
The next eligible day starts a new candidate segment.

Consequences:

- exact equality reaches the threshold;
- one unusually large day may push record count or physical file size beyond the configured value,
  but remains indivisible and is still one segment;
- under `All`, one metric may exceed its threshold while the segment remains open until all enabled
  predicates are reached;
- the final candidate whose combination rule is still false is an **open tail** and stays in Current.

The existing pure `ArchiveRotationPlanner` on main is only a foundation. Before it is used by
automatic rotation it must be corrected to return ready ranges separately from the open tail and to
use the post-day `>=` semantics above. Its current `MaxBytes` fail-closed behavior remains
correct: physical-size planning belongs to storage-backed shadow construction.

## 3. Eligible Current window

Before planning:

1. validate Current, Catalog, and every canonical Archive database;
2. reject any Archive coverage overlap;
3. reject an active pending Archive Split or Archive Rotation;
4. determine the latest assigned Archive coverage end, if any;
5. detect closed Current history rows.

Automatic rotation considers only the chronological Current suffix after existing Archive
ownership. If Current still contains rows inside already assigned Archive coverage, automatic
rotation fails closed; the existing transfer/recovery path must resolve that state first.

The candidate window starts at the earliest eligible closed Current row and ends at the latest
eligible closed Current row. Zero-record dates **inside** that window are included in calendar-span
measurement and coverage. Idle dates after the last eligible row do not create trailing empty
Archive coverage by themselves.

If no segment reaches the configured combination rule, rotation is a no-op and Current is
unchanged.

## 4. New Archive names and identities

Rotation creates unsplit base files only:

```text
archive_000101.db
archive_000102.db
...
```

Allocation is deterministic:

1. inspect every canonical Archive filename, including split family members;
2. take the maximum existing `BaseNumber`;
3. allocate consecutive base numbers starting at `max + 1`;
4. use `SplitSequence = 0`;
5. fail closed if the canonical six-digit base-number space is exhausted.

Gaps are not reused automatically. This keeps allocation monotonic and avoids interpreting an old
gap as evidence that a crashed operation may overwrite it.

Each planned output also receives a new non-empty `DatabaseId`. Filename, DatabaseId, coverage,
segment order, policy snapshot, and planned metrics become immutable once the durable marker is
committed. Recovery never recalculates names or IDs.

The current `ProtectedArchiveDatabaseService.CreateAsync` generates `DatabaseId` internally and
publishes immediately, so it is not by itself the rotation publication boundary. Rotation requires
a shadow builder capable of constructing an Archive v1 database with a **planned** DatabaseId and
coverage before final publication.

## 5. Durable pending marker

Rotation requires a protected marker in Current, conceptually introduced by the next Current schema
version after v9.

At most one `PendingArchiveRotation` operation may exist.

Operation row:

- `OperationId`;
- durable `Phase`;
- policy snapshot: nullable record/byte/day thresholds plus threshold mode;
- `CreatedAtUtc`.

Ordered target rows:

- `SegmentOrder`;
- canonical `FileName`;
- planned `DatabaseId`;
- `CoverageStartDate`;
- `CoverageEndDate`;
- expected record count;
- measured shadow physical size.

The immutable target rows are recovery metadata, not Catalog projection.

Durable phases:

1. `Planned`
2. `ReadyToPublish`
3. `PhysicalPublished`
4. `SourcePurged`
5. `CatalogPublished`

Phase advances only forward by one step. Normal cleanup removes the marker only after
`CatalogPublished`.

## 6. Rotation staging

Each operation owns a hidden staging directory on the Archive filesystem:

```text
Archive/.clipensk-archive-rotation-<OperationId>/
```

It is excluded from ordinary `Archive/archive_*.db` enumeration.

Staging files are full SQLCipher Archive v1 shadows containing the exact history that would be
published. They are disposable before final publication, but after source purge begins they are
retained as recovery backup until Catalog publication completes.

An orphan staging directory with no durable marker is never treated as authoritative history. It may
be cleaned only after confirming that no matching pending operation exists and no canonical final
file depends on it.

## 7. Planning and shadow construction

For an uninterrupted start, the authoritative Current snapshot, threshold planning, shadow build,
marker commit, publication, and source purge are serialized against capture/maintenance mutation.
The initial implementation may hold one mutation lease for this critical interval; internal
transfer code must therefore support a lease-aware path rather than recursively acquiring the same
lease.

Planning proceeds chronologically by whole day.

### 7.1 Count/day-only configuration

The corrected pure planner may derive ready ranges and the open tail from per-day record counts.
Zero-record dates inside the candidate window are included explicitly.

### 7.2 Configuration containing `MaxBytes`

Physical size is decided by the actual staged Archive database, not payload estimates.

For the current candidate segment:

1. create or extend its hidden Archive v1 shadow with the next complete day;
2. set staging-only coverage to the candidate inclusive range;
3. commit and close the SQLCipher connection;
4. fully validate the shadow;
5. read the physical `.db` file length;
6. evaluate record/day/file metrics with the configured `Any`/`All` rule;
7. if the combination is reached, finalize that shadow as one ready segment and start a new
   candidate with the next day.

Only staging coverage may evolve while planning. Once a shadow becomes a planned output and its
marker is committed, final assigned coverage is immutable.

A candidate tail that does not reach the rule is not published and its rows stay in Current.

Before the first final Archive write:

1. the complete ready-range plan is known;
2. all corresponding shadows are built and validated;
3. exact source→shadow cross-check succeeds;
4. the marker is committed in `Planned`;
5. the planned shadows are revalidated against the durable marker;
6. phase advances to `ReadyToPublish`.

If there are no ready ranges, no marker is committed.

## 8. Source→shadow cross-check

For every planned segment:

- each source event whose persisted `CalendarDate` is inside coverage appears exactly once;
- no shadow event belongs outside assigned coverage;
- event envelope and ordered payload rows are exact;
- referenced `ApplicationIdentity` rows required by the history FK contract are present;
- no EventId appears in two planned outputs.

Across the whole operation, the union of shadow events must equal the Current event set inside all
planned ranges.

External payload files are not copied or rewritten. Rotation moves only history DB ownership of
their persisted references.

## 9. Publication

After `ReadyToPublish`, cancellation no longer means rollback.

For each planned segment in order:

1. revalidate the hidden shadow;
2. copy it to an operation-specific temporary file in the top-level Archive directory;
3. validate the copied temporary database;
4. atomically move the temporary file to its canonical final filename with no overwrite;
5. re-open and validate the final database against the exact planned filename, DatabaseId, and
   coverage.

The original hidden shadow remains until operation completion. This preserves a backup while Current
rows are later purged.

Existing canonical files are never overwritten. An unexpected file occupying a reserved name is a
fail-closed collision.

After every planned final file exists and the whole physical Archive set has valid non-overlapping
coverage, phase advances to `PhysicalPublished`.

At this point Current still contains the source rows, so temporary Current+Archive duplication is
expected and safe. Catalog may be stale; application integration must keep the normal runtime
suspended until recovery/rotation completes.

## 10. Verified Current purge

After `PhysicalPublished`, each planned range is completed using the existing
Current→Archive copy-first verification semantics.

The implementation should refactor/reuse `ProtectedCurrentToArchiveTransferService` so rotation
can invoke its exact compare-and-purge core while already holding the rotation mutation lease.

Because the final Archive already contains the shadow rows, the transfer path should:

1. materialize the exact Current batch for the range;
2. compare existing Archive events/payload rows exactly;
3. insert only if an expected row is genuinely absent and doing so still matches the immutable
   target plan;
4. verify Archive durability;
5. re-read the exact Current batch under the same mutation serialization;
6. purge Current only if it is unchanged.

Recovery may encounter ranges already purged by a previous attempt. In that case the final Archive
must still validate against the planned target and retained staging shadow before the range is
accepted as complete.

After all planned ranges have no remaining Current rows and every final target is revalidated, phase
advances to `SourcePurged`.

No Current row belonging to the open tail is removed.

## 11. Catalog publication

After `SourcePurged`:

1. enumerate and validate the entire physical Archive set;
2. verify exact planned target identities/coverage;
3. rebuild `ArchiveSegmentIndex` from authoritative Archive databases;
4. validate Catalog consistency against the physical set.

Rotation does not change the logical set of external payload addresses, so it does not require a
separate external-payload projection mutation merely because references moved from Current to
Archive.

When Catalog validation succeeds, phase advances to `CatalogPublished`.

Final cleanup then:

1. revalidates final targets and Catalog once more;
2. deletes retained rotation staging/shadow files;
3. removes the pending marker;
4. allows clipboard runtime to resume.

## 12. Crash recovery

Recovery runs before clipboard capture is allowed to resume and always acquires the storage mutation
gate before changing Current/Archive/Catalog state.

It treats the durable plan as immutable and inspects actual filesystem/DB state before every action.

### `Planned`

No final Archive publication is allowed to have started. Staging may be incomplete. Rebuild or
revalidate exact shadows from Current, then advance to `ReadyToPublish`.

A planned canonical final file already present in this phase is unexpected and fails closed.

### `ReadyToPublish`

A subset of planned final files may already exist because phase can lag filesystem operations.
Exact planned files are reused; missing files are published from validated shadows. Any identity,
coverage, or filename mismatch fails closed.

Current must still retain all planned source rows.

### `PhysicalPublished`

All planned final files exist. Current may contain all, some, or none of the planned rows because a
crash can occur between per-range purge commits.

Recovery validates final targets against retained shadows and idempotently completes exact
compare/purge for every range.

### `SourcePurged`

Current has no rows inside planned coverage. Recovery validates finals and rebuilds/validates Catalog.

### `CatalogPublished`

Recovery repeats final physical/Catalog validation, removes staging, and clears the marker.

If staging is unexpectedly lost after source purge and exact final data can no longer be proven from
the immutable plan/available durable evidence, recovery fails closed rather than inventing missing
history.

## 13. Cancellation and failure contract

- Before durable `ReadyToPublish`, cancellation may stop work with Current authoritative and no
  final Archive mutation. A planned marker may be safely abandoned only after proving no planned
  canonical final file was published.
- After `ReadyToPublish`, operation is roll-forward only.
- Session revocation always stops further mutation and leaves the durable marker for recovery.
- A late caller cancellation after an irreversible durable step must not be reported as if that step
  rolled back.

Fail-closed cases include:

- malformed/unsupported rotation settings;
- Current rows inside existing Archive ownership;
- unexpected Archive filename or DatabaseId collision;
- exhausted canonical base-number namespace;
- source/shadow event mismatch;
- staging/final identity or coverage mismatch;
- physical Archive overlap;
- final physical size/record count differing from the planned shadow evidence unexpectedly;
- Current changing between exact verification and purge;
- Current/Catalog/Archive SQLCipher, schema, FK, or `quick_check` failure;
- Catalog mismatch after rebuild.

## 14. Concurrency with other maintenance

Archive Rotation, Archive Split, policy cleanup, manual Current→Archive transfer, and other history
mutations must serialize through the protected storage mutation gate.

A pending Archive Rotation blocks starting Archive Split and other operations that could change
Archive layout or the same Current history ranges. A pending Archive Split blocks starting
rotation.

Read-only Journal queries may fail closed while physical Archive publication is ahead of Catalog;
the application-level rotation/recovery flow should keep normal clipboard runtime suspended until
the marker is cleared.

## 15. Implementation slices

Current accepted foundation on exact main `ef96c1aba2eaad7a2af853a85981ac1be9d4a45e`:

- `ArchiveRotationSettings` persists count/physical-size/day thresholds;
- multi-threshold configuration requires explicit `Any` or `All`;
- pure planner supports count/day inputs and fails closed on `MaxBytes`;
- exact-main Build #438 and Native SQLCipher #81 are **SUCCESS**.

Remaining slices, in order:

1. **Protocol document** — this file.
2. **Pure planner correction** — ready ranges vs open tail, post-day `>=` trigger semantics,
   whole-day oversized cases, zero-record span tests.
3. **Current schema + pending rotation repository** — durable operation/target plan and migrations.
4. **Rotation source scanner + shadow builder** — closed-day metrics, physical-size measurement,
   source/shadow cross-check, deterministic base-name allocation.
5. **Copy-first publication** — retained staging backup and exact planned final validation.
6. **Lease-aware transfer integration** — reuse exact compare/purge logic without nested mutation
   lease acquisition.
7. **Catalog publication + recovery coordinator + atomic start service**.
8. **Startup/runtime integration** — recover pending rotation before clipboard capture resumes.
9. **Scheduler/manual trigger and Settings UI** for thresholds/mode.
10. **Manual production WinUI/storage smoke evidence** remains separate and must stay
    `UNVERIFIED` until actually performed.
