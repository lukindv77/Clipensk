# Глобальная capture policy — введена в Current v5

Latest Current schema — **v6**. Global capture policy остаётся storage-scoped контрактом, введённым в v5; v6 добавляет exact custom-binary file-extension configuration. Первичный product setup сохраняет policy и относящиеся к ней custom mappings атомарно.

## Принятый контракт

Глобальная policy принадлежит выбранному хранилищу и сохраняется в зашифрованной `Current/current.db`. Она не является общей настройкой процесса в JSON и не хранится только в rebuildable Catalog. Индивидуальные overrides остаются привязаны к `ApplicationId`.

При создании или миграции хранилища policy **не настроена**: policy tables пусты. `CustomBinaryFormatConfiguration` в новом/migrated v6 также изначально пуста. Отсутствие policy не превращается в `Allow`, `Deny`, пустую разрешающую policy или format/size defaults. Пользователь должен явно выполнить первичную настройку.

До настройки чтение существующей истории после unlock допустимо, но обработка новых clipboard payload не запускается. После successful initial COMMIT JournalWindow отправляет App post-COMMIT notification; App пересобирает protected composition и запускает resident runtime только если persisted policy читается как non-null для той же active session.

## Schema

Current v5 добавила:

| Таблица | Данные и ограничения |
|---|---|
| `GlobalCapturePolicy` | `SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (=1)`; `CaptureRule TEXT NOT NULL`, только exact `Allow` / `Deny` |
| `GlobalFormatCapturePolicy` | `SingletonId INTEGER NOT NULL CHECK (=1)`; `FormatName TEXT NOT NULL`; `CaptureRule TEXT NOT NULL`; nullable положительный `MaxBytes`; PK `(SingletonId, FormatName)`; FK к global header с `ON DELETE CASCADE` |

Current v6 отдельно добавила `CustomBinaryFormatConfiguration(FormatName, FileExtension)`. Этот mapping не является частью merge semantics policy, но product initial setup может писать его в той же Current transaction, что global policy.

Global rules не имеют родительской policy: `Inherit` и неизвестные enum значения не принимаются при первичной настройке. Для индивидуальных overrides `Inherit` сохраняет существующую семантику. Имена форматов сравниваются ordinal/BINARY, без нормализации. Пустые/whitespace имена отклоняются.

`MaxBytes = null` означает explicit unlimited только для разрешённого формата после соответствующего user choice. Численный `MaxBytes` обязан быть положительным Int64. Для Deny численный limit не сохраняется. Exact size semantics определены в `CLIPBOARD_CAPTURE_SIZE_LIMITS.md`.

В policy tables попадают только переданные caller rules. Selector читает формат только при итоговом `Capture = Allow` и явном `Formats[name].Capture = Allow` после merge. Global `Deny` является наследуемой базой; application override может заменить его. Это не безусловный kill switch.

## Individual repository

`IGlobalClipboardCapturePolicyRepository` реализован в `SqliteGlobalClipboardCapturePolicyRepository`:

- `ReadAsync(token)` возвращает immutable policy либо `null` для не настроенного хранилища;
- `InitializeAsync(policy, token)` атомарно сохраняет **первую** policy как отдельную low-level operation;
- любая повторная инициализация, включая тем же значением, завершается ошибкой;
- update/delete API отсутствуют: изменение требует отдельного cleanup workflow согласно REQUIREMENTS §18.

Constructor не открывает БД. Read использует ReadOnly и единый snapshot для header + formats, включая orphan-row validation. Individual initialize использует ReadWrite, immediate transaction, проверку отсутствия policy и INSERT header + formats без upsert.

Операции привязаны к `ProtectedStorageSessionLease`, связывают caller/session cancellation и проверяют отмену до COMMIT. Ошибка/отмена до COMMIT откатывает write. После successful COMMIT late cancellation не превращает durable success в cancellation failure.

Repository принимает Current **v5+**, проверяет storage identity/Current role, `user_version`, table/PK/FK shape и persisted rules. Ошибка schema/data не отображается как `null`/Allow/Deny.

## Атомарная product initial setup

JournalWindow не выполняет global policy и custom-binary mappings отдельными durable writes. Product first-run policy path использует `SqliteInitialClipboardCaptureConfigurationService` на той же active `ProtectedStorageSessionLease`.

Aggregate service принимает полностью валидированную `ClipboardCapturePolicy` и zero-or-more explicit `InitialCustomBinaryFormatConfiguration`. Он требует Current v6, валидирует обе schema families и начинает immediate transaction. Перед INSERT service проверяет, что initial-configuration tables ещё не содержат durable rows:

- `GlobalCapturePolicy`;
- `GlobalFormatCapturePolicy`;
- `CustomBinaryFormatConfiguration`.

После этого в одной transaction записываются global header, format rules и custom mappings. Если mapping invalid, prohibited, не относится к explicit Allow, конфликтует с existing partial setup, insert падает или token отменяется до COMMIT — policy и новые mappings не остаются в split state.

Это особенно важно из-за first-write-only контрактов: UI не может сначала навсегда записать policy, а потом обнаружить failure extension mapping. Partial state, созданный другим low-level/manual path, не «исправляется» aggregate service автоматически; требуется будущий cleanup contract.

## Первичная настройка в JournalWindow

Раздел «Приложения и правила сбора» содержит initial editor и read-only summary. После создания active protected session policy читается off UI thread с session cancellation и generation guards. Отсутствие policy отображается как unconfigured; ошибка чтения не трактуется как absence.

### Standard formats

UI всегда показывает exact Windows `StandardDataFormats`:

- Text;
- Html;
- Rtf;
- Bitmap;
- WebLink;
- ApplicationLink;
- StorageItems.

Эти строки не являются defaults: global selector и каждый format selector первоначально не выбраны. Каждый standard format требует явного Allow/Deny. Для Allow пользователь обязан выбрать positive Int64 limit либо explicit unlimited. Для Deny `MaxBytes` не задаётся.

### Custom binary formats

UI позволяет **явно добавить** zero-or-more custom binary rows. Никакие discovered/private formats не добавляются, не выбираются и не включаются автоматически.

Каждая custom row требует exact `FormatName` и explicit Allow/Deny. `WaveAudio`, `RiffAudio` и `FileContents` запрещены capture guard. Exact duplicate имени — включая collision со standard row — отклоняется.

Для custom `Allow` обязательны:

- explicit size limit или explicit unlimited;
- canonicalizable physical file extension.

Extension нормализуется через `ExternalPayloadAddressFactory.NormalizeCustomBinaryExtension` и сохраняется агрегатно вместе с initial policy. Для custom `Deny` mapping не создаётся.

Read-only summary для non-standard Allow дополнительно читает exact extension mapping. Если policy была создана старым/ручным путём без mapping, UI показывает missing mapping как fail-closed состояние; fallback extension не подставляется.

Для выбранного приложения UI показывает persisted runtime-discovered exact format names без эвристического переименования. Discovery сам по себе остаётся read-only observation и не включает неизвестный формат. В application formats editor discovered non-standard rows доступны для explicit `Inherit`/`Allow`/`Deny`; explicit `Allow` требует canonicalizable extension и проходит через mapping-aware application maintenance. Existing exact mapping переиспользуется и не может быть rebound через этот UI; `Inherit`/`Deny` новый mapping не создают. Таким образом enable всегда является отдельным явным действием пользователя, а не следствием discovery.

## Composition, worker lifecycle и maintenance quiescence

Persisted policy подключена к `ProtectedClipboardDeliveryServices.TryCreateAsync`. Boundary возвращает `null` только при действительно отсутствующей global policy; storage/schema errors остаются errors.

App выполняет composition после active protected session и публикует результат только при совпадающих generation/window/host/lifecycle/session guards. Если policy отсутствует, listener остаётся выключенным и worker не создаётся.

После successful aggregate initial COMMIT JournalWindow вызывает existing post-COMMIT notification. App повторно compose-ит runtime. Notification failure не демотирует уже committed configuration.

Для non-null composition App создаёт exact-session single-reader `ClipboardAcceptedCaptureWorker`. Новый worker ждёт завершения previous generation; Windows listener запускается только после ready-reader gate. Lock останавливает listener, инвалидирует capture epoch, worker generation и composition; linked App/session cancellation завершает blocked/active worker.

Перед изменением уже настроенного application override App имеет отдельный runtime-quiescence boundary. `TryQuiesceClipboardRuntimeAsync` атомарно захватывает unique suspension owner token, блокирует новые composition/worker/listener paths, останавливает listener, отменяет current worker и ждёт завершения exact worker task до возврата caller'у. Это закрывает stale-policy race: старый delivery graph не может оставаться активным во время destructive cleanup/policy publication.

После successful quiesce maintenance caller обязан передать exact owner token в `TryResumeClipboardRuntimeAfterMaintenance`; только тогда App снимает suspension и для всё ещё current protected session строит **fresh composition**, заново читая persisted policy. Lock/reopen ABA защищён owner-token semantics: stale old-session caller не может снять suspension новой session.

Application-policy maintenance реализован как durable workflow Current → Archive → Catalog → Trash → Completion с `PendingPolicyMaintenance` marker. Mapping-aware Current phase публикует новые exact custom mappings, application policy, Current cleanup и v2 marker одной transaction. Legacy operations продолжают использовать v1 marker; v2 отдельно фиксирует fingerprint полного custom-binary configuration snapshot. Rebind/update/delete mapping не входят в этот contract.

Application discovered-format editor подключён к этому mapping-aware path через отдельный App boundary с теми же recovery, quiescence и resume semantics. Global-policy update/cleanup остаётся отдельным maintenance contract.

Контракты подробно описаны в `PROTECTED_CLIPBOARD_DELIVERY_COMPOSITION.md`, `CLIPBOARD_WORKER_LIFECYCLE.md` и `CUSTOM_BINARY_FORMAT_CONFIGURATION.md`.

## Migration и latest schema

`ProtectedStorageDatabaseService` создаёт новую pair как **Current v6 / Catalog v3**.

Migration sequence сохраняет отдельные durable steps:

- v4 → v5: global-policy tables, без seed/defaults;
- v5 → v6: пустая `CustomBinaryFormatConfiguration`, без переписывания policy.

Catalog отдельно дошёл до v3 с rebuildable Archive segment projection; это не меняет source-of-truth semantics global policy.

До mutation валидируется Current/Catalog pair. Ошибка/отмена v4→v5 оставляет полноценный v4; ошибка/отмена v5→v6 оставляет полноценный v5. Повторное открытие может безопасно продолжить migration.

## Проверки

Existing global policy tests покрывают absence/defaults, exact round-trip через новую session, explicit Deny, repeated initialization rejection, malformed rules/schema/data, orphan rows, lock/dispose/cancellation, connection cleanup и rollback.

Aggregate initial-configuration tests дополнительно покрывают:

- единый COMMIT policy + normalized custom extension;
- rollback policy + mappings при injected custom insert failure;
- отказ поверх partial existing mapping без записи policy;
- prohibited format validation до DB open;
- требование, чтобы mapping относился к explicit allowed format.

Application-maintenance tests дополнительно покрывают atomic custom mapping + application policy publication, v2 marker/fingerprint, transaction rollback, exact retry, same-extension reuse, different-extension rebind rejection и полный v2 Resume flow через Archive → Catalog → Trash → Completion.

Application discovered-format UI tranche меняет `src/Clipensk.App/**`, localization и contract docs, но не `src/Clipensk.Storage/**`. Feature Build должен подтверждать full test suite на exact feature SHA, а после promotion обязателен exact-main Build. Native SQLCipher остаётся path-filtered workflow и не становится отдельным обязательным gate только из-за UI-only changes.

**Manual WinUI/real-clipboard smoke остаётся UNVERIFIED.** Unit/CI tests не эмулируют настоящий foreground application, `WM_CLIPBOARDUPDATE`, WinRT `DataPackageView`, suspension во время active capture и пользовательскую работу dynamic custom rows.

Global-policy cleanup/update и mapping rebind/update/delete остаются отдельными этапами. Format/size defaults по-прежнему не назначены.
