# NEW CHAT HANDOFF — Clipensk

Дата checkpoint: 2026-09-06 (UTC).

## A. Проект и источники

Clipensk — Open Source резидентный Windows clipboard-history manager с WinUI 3.
Репозиторий: https://github.com/lukindv77/Clipensk
Каноническая ветка: main. GitHub и exact GitHub Actions evidence — source of truth.
Платформа: только Windows x64/AMD64; .NET 10, WinUI 3 / Windows App SDK.
Development/runtime host — unpackaged. Финальная схема MSIX/portable и лицензия ещё не выбраны.

Прочитать: AGENTS.md; docs/WORKFLOW_NEW_CHAT_HANDOFF.md; docs/REQUIREMENTS.md;
docs/ARCHITECTURE.md; docs/CLIPBOARD_CAPTURE_SIZE_LIMITS.md;
docs/APPLICATION_IDENTITY.md; docs/CLIPBOARD_HISTORY_SCHEMA.md;
docs/STORAGE_CATALOG_SCHEMA.md; docs/CURRENT_DATABASE_SCHEMA.md;
docs/CRYPTOGRAPHY.md; docs/NATIVE_SQLCIPHER_BUILD.md; docs/OPEN_QUESTIONS.md.

## B. Намерение пользователя и границы нового чата

Восстановить проект с сохранением решений, правил, истории этапов и последней рабочей точки.
При первом получении этого handoff выполнить восстановление и fresh сверку GitHub,
дать краткий отчёт о main, CI, завершённых этапах и блокерах, затем ждать команды пользователя.
Не начинать новую разработку, исправления, исследование или подключение worker автоматически.
Фраза для завершения первоначального восстановления:
«Контекст восстановлен. Готов продолжать. Жду вашей команды.»

Полный текст старых чатов доступен не гарантированно. В предыдущей сессии восстановлены
документы и история main от инициализации репозитория; соседний проект WebClip к Clipensk не относится.

## C. Подтверждённая функциональная точка и CI

Последний функциональный main:
072b31e428f0202f1fa08c679e6c565a0d6740a7
feat: read Current clipboard history with explicit period and limit

Exact Build #129:
- run 34006675571;
- https://github.com/lukindv77/Clipensk/actions/runs/34006675571
- head_sha 072b31e428f0202f1fa08c679e6c565a0d6740a7;
- completed / SUCCESS;
- Verify x64-only implementation scope, Restore, Build, Test — все SUCCESS.

Exact Native SQLCipher #33:
- run 34006675566;
- https://github.com/lukindv77/Clipensk/actions/runs/34006675566
- тот же head_sha 072b31e428f0202f1fa08c679e6c565a0d6740a7;
- completed / SUCCESS;
- pinned SQLCipher x64 build, provenance, smoke host, encrypted storage verification,
  unpackaged runtime publish, runtime SQLCipher loading verification и uploads — SUCCESS.

Оба запуска перепроверены при подготовке перехода. На этом SHA активных workflow нет.
Статус «CI ещё выполняется» из предыдущего ответа теперь superseded.
PASS managed Build не равен доказательству готовности финального installer или UI.

Этот файл публикуется ПОСЛЕ функциональной точки отдельным docs commit.
Поэтому SHA фактического main после публикации будет другим: его и exact docs Build
нужно определить свежим запросом GitHub. Не приписывать Build #129 будущему docs SHA.
При наличии отдельного приложения с publication checkpoint использовать его как указатель,
но mutable state всё равно перепроверять.

## D. Правила durable работы

1. Перед durable работой использовать свежий фактический main.
2. Работать через feature/fix branches; для документации допустима docs branch.
3. Перед КАЖДЫМ обновлением main:
   - свежий fetch/read main;
   - compare feature head vs фактический main;
   - behind=0;
   - merge base равняется этому main;
   - обновление только fast-forward через update_ref(... force:false).
4. После изменения main найти exact Actions Build по точному head_sha.
5. PASS разрешён только после SUCCESS всех четырёх обязательных шагов:
   Verify x64-only implementation scope / Restore / Build / Test.
6. При failure: первый failed step → его log → минимальный failure-driven fix;
   не добавлять unrelated изменения и speculative fixes.
7. Во время CI допустима только уже запланированная независимая полезная работа.
   При её отсутствии ожидать не более 2 минут; затем точный status/run/SHA и остановка.
   Пустой polling запрещён. Не запускать новые задачи для заполнения ожидания.
8. Если connector не даёт необходимого evidence, дать пользователю точную инструкцию,
   какой run/job/log предоставить. Отсутствие evidence не заменять предположением.

Постоянное правило рекомендации модели записано в AGENTS.md коммитом
5050d2c0a5ed60f5af5265c295c138dd5d16bc8e, Build #128 run 34006152047 — SUCCESS.
После каждой задачи, остановки/итогов сессии и handoff сообщать модель, сложность и краткое
обоснование для следующего конкретного шага. Окончательный выбор — за пользователем;
самостоятельно модель не переключать. Пользователь сообщил подписку Plus и доступ вплоть
до Astra с максимальной сложностью; доступность и квоты могут измениться.
Различать: Низкая, Средняя, Высокая (High), Очень высокая (xHigh), Максимальная.
Рекомендации не являются автоматическим разрешением следующей задачи.

## E. Последние завершённые этапы

До текущей сессии:
- protected lifecycle, password → MasterKey, SQLCipher Current/Catalog bootstrap;
- clipboard signal queue, time/source metadata, retained snapshot, readers,
  explicit policy evaluation, canonical MaxBytes enforcement;
- cancellation leases/epochs, protected storage session и protected delivery boundaries;
- durable ApplicationId registry/aliases, SQL repository и per-application policies;
- Current history schema v4 и миграции;
- transactional SqliteClipboardHistorySink;
- SHA-based external storage, exact-address ensure/restore;
- Catalog v2, migration v1→v2 и SqliteExternalPayloadAddressIndex;
- CatalogClipboardExternalPayloadAddressResolver;
- ProtectedClipboardHistoryServices: lazy index → resolver → sink;
- JournalWindow.TryGetActiveProtectedStorageSession(out ProtectedStorageSessionLease? session).
  Accessor возвращает только активную lease, не выделяет master key отдельно и не продлевает lifecycle.
  Commit 35207c2d9d46e6b53abe3287fceb8fa2a45f25b2, Build #127 run 34004958174 — SUCCESS.

В текущей сессии:
1. Восстановлена доступная история и сверены устаревшие документы с кодом.
2. Постоянное правило рекомендации модели/сложности добавлено в AGENTS.md.
3. Реализован Current read repository — commit 072b31e428f0202f1fa08c679e6c565a0d6740a7.
   Ветка feat/current-clipboard-history-read-repository.
   Перед продвижением: ahead=1, behind=0, merge base=5050d2c0a5ed60f5af5265c295c138dd5d16bc8e;
   main обновлён через force:false.
4. Подтверждены Build #129 и Native SQLCipher #33. Известного failed CI на функциональной
   точке нет; failure-driven fix для этого tranche не требуется.

## F. Точный контракт нового Current read repository

Изменены/добавлены:
- src/Clipensk.Core/History/ICurrentClipboardHistoryRepository.cs
- src/Clipensk.Core/History/ClipboardHistoryEntry.cs
- src/Clipensk.Storage/History/SqliteCurrentClipboardHistoryRepository.cs
- tests/Clipensk.Storage.Tests/SqliteCurrentClipboardHistoryRepositoryTests.cs
- docs/CLIPBOARD_HISTORY_SCHEMA.md

API: ReadAsync(JournalDateRange period, int limit, CancellationToken).
Период обязателен и включителен по сохранённой CalendarDate; limit обязателен и >0.
Никаких default periods/limits.
SQL сортирует EventUtc DESC, EventId COLLATE BINARY DESC.
LIMIT применяется к events в CTE ДО LEFT JOIN payloads.
Один SELECT возвращает единый read snapshot envelope + payloads, порядок payload — PayloadOrder.
Возвращаются detached read models: EventId, полный EventTimeContext, durable source ApplicationId,
runtime source snapshot, inline canonical text/SearchText или external SHA/path/size.
UTC/offset/timezone сохраняются; calendar date проверяется на согласованность без обращения
к текущей Windows time zone. StorageItems JSON и URI string не пересериализуются.
External bytes не читаются, Catalog и extension provider не вызываются.
Даже при отсутствующем файле сохраняется исторический persisted path/date.
Открывается только Current/current.db в ReadOnly. Schema не создаётся и не мигрирует.
Проверяются StorageId/role/schema/user_version и history schema shape.
Caller/session cancellation связаны; проверки до/после открытия, между rows и перед возвратом.
При ошибке или отмене частичный результат не возвращается; reader/connection освобождаются.
Constructor не открывает БД, repository не кэширует результаты и не продлевает session.

Тесты покрывают sink→read representations/time/source, границы периода и UTC/local date,
лимит целых events, tie order, null source, пустой результат, обязательный positive limit,
caller cancellation / lock / dispose, отмену при открытии с закрытием connection,
неверные identity/schema и malformed persisted data.
Локально проверен actual SQL через SQLite/Python. .NET SDK в scratch отсутствовал;
C# build/tests подтверждены GitHub Build #129.

Ограничения (НЕ объявлять реализованными):
- это Current-only read boundary, не готовый журнал;
- нет archive reads, FTS, source filters, keyset continuation и UI composition;
- limit ограничивает число events, не суммарные payload bytes;
- Microsoft.Data.Sqlite вызовы синхронны; preemptive interruption отдельного вызова не обещана;
- future host должен явно определить execution/cancellation lifecycle перед UI;
- уже возвращённые данные принадлежат вызывающей стороне, UI/cache clear при lock — её обязанность;
- repository подключён в ProtectedClipboardHistoryServices (см. раздел L), но ещё не подключён к UI.

## G. Продуктовые и архитектурные инварианты

- Только Windows x64/AMD64. Platforms=x64, RuntimeIdentifiers=win-x64.
  ARM64 вне scope и не является будущей незавершённой задачей.
- WM_CLIPBOARDUPDATE WndProc — только signal/enqueue; никаких clipboard reads,
  source resolution, persistence или тяжёлой работы.
- Monitoring разрешён только после разблокировки и готовности active protected storage session.
  Listener и payload worker — разные вещи: listener boundary есть, автоматический worker не запущен.
- Один Windows DataPackageView от format discovery до всех reader calls.
  Никакого повторного Clipboard.GetContent() на reader stage.
- Полный EventTimeContext: UTC timestamp, local offset, Windows timezone ID, calendar date.
- SourceApplication и InvocationApplication — разные сущности. Invocation не источник history event.
- Durable identity — Clipensk-owned непустой GUID ApplicationId.
  PID/HWND/path/display name/AUMID не primary key. Exact path и packaged AUMID — aliases/evidence.
  Не вводить silent identity merge/case folding/path heuristics; конфликты fail closed.
- Не выбирать format defaults, значения size limits, global Allow/Deny без утверждённого источника.
- Worker не запускать до готовности lifecycle cancellation, policy, sink и composition dependencies.
- Archive/catalog schema и поведение сверх зафиксированных контрактов не придумывать заранее.

MaxBytes canonical semantics:
- Text/HTML/RTF: UTF-8 bytes exact reader string.
- WebLink/ApplicationLink: UTF-8 Uri.OriginalString.
- Bitmap: normalized PNG bytes.
- StorageItems: versioned canonical UTF-8 JSON v1.
- Registered/private custom binary: exact preserved bytes.
- SearchText, служебные metadata, FTS и storage overhead не входят в лимит.
- При наличии лимит положительный, проверка <=; null означает отсутствие заданного лимита.
- HTML SearchText строится managed projection после size gate; raw HTML сохраняется.
  Для RTF SearchText пока null; UI RichEdit/parser без отдельного контракта не добавлять.
- CF_WAVE, CF_RIFF, virtual file contents запрещены; CF_HDROP не копирует содержимое файлов.

## H. Storage/history и более широкая архитектура

Фактические версии: Current v4, Catalog v2.
Current: v1 identity БД → v2 Application identities/aliases → v3 policies → v4 history.
Catalog v2 — rebuildable ExternalPayloadAddressIndex: SHA-256 exact bytes → RelativePath + SizeBytes;
RelativePath unique. First persisted path выигрывает; same-SHA size conflict и path collision fail closed.
Whole-pair validation выполняется до migration mutation; новая пара сразу v4/v2.
Catalog не единственный источник адресов: history rows сохраняют SHA/path/size.

History sink разрешает ВСЕ external payload до SQL transaction; inline byte counts валидирует.
В одной transaction event и ordered payloads. Отмена проверяется до COMMIT.
После успешного COMMIT не превращать успех в late cancellation.
Event→payload ON DELETE CASCADE; Application→event ON DELETE SET NULL.

External store: temp + atomic move, containment внутри Files root, existing file exact size+SHA.
Duplicate не получает текущую дату capture как новую firstStoredDate.
Resolver резервирует address в Catalog до exact-address ensure; recovery повторяемый.
Новый custom binary требует explicit IClipboardCustomBinaryFileExtensionProvider, без скрытого .bin.
Существующий custom SHA использует persisted address без вызова extension provider.
PNG — normalized bytes и .png. External files сознательно не шифруются MasterKey.

Password не persistится. Argon2id v1.3: 64 MiB / 3 iterations / 4 lanes / salt 16 bytes /
MasterKey 32 bytes. Один MasterKey на защищённые БД. storage-crypto.json не содержит пароль/ключ.
SQLCipher raw key через sqlite3_key, cipher_compatibility=4, cipher_memory_security=ON;
runtime gates cipher_version>=4.12.0, cipher_status=1, quick_check и DatabaseIdentity.
Pinned SQLCipher 4.17.0 commit 810db22f575ee7cf94ea96a3e91622b5fcece3dc, OpenSSL 3.5.8.
Управляемая очистка ключей best-effort; immutable password strings не гарантируют физическое стирание.

Архивная архитектура утверждена концептуально, но lifecycle/schema не завершены:
владение целыми календарными днями, не более одного archive owner для дня;
Current→Archive сначала copy/verify, затем purge, временный дубль разрешён, потеря обеих копий запрещена.
Архивы обычно ReadOnly; запись только через maintenance. Query period ограничивает открываемые архивы.
Catalog должен перестраиваться из Current + Archive.
External Trash требует глобальной проверки допустимых ссылок; утверждённый retention default — 30 дней.
Журнал — главный UI shell, русский fallback; hotkey настраивается пользователем.

## I. Известные блокеры, незавершённая работа и расхождения docs

Главный ближайший blocker:
global ClipboardCapturePolicy — mandatory explicit dependency, но requirements-backed источник
и default policy ещё не утверждены. Не выбирать Allow/Deny самостоятельно.
Следующий основной этап после решения — app-level accepted capture delivery после unlock.
Не объявлять worker, UI history или end-to-end capture готовыми по наличию repository/sink.

Другие открытые направления:
- execution/cancellation/UI composition для Current reads; query continuation/FTS/source filters;
- безопасный RTF→SearchText contract;
- Current→Archive lifecycle, Archive schema/read path, Catalog rebuild, external reference tracking/GC;
- password/MasterKey change, crypto metadata и partial storage recovery;
- auto-lock end-to-end teardown;
- identity user merge/rebind, alias retention и discovered-app UI;
- финальная упаковка, installer/installed-launch evidence, byte-for-byte reproducibility;
- hot backup/snapshot отложен;
- Windows product support floor, лицензия, format defaults/limits, journal period, rotation defaults,
  localization file schema и paste/focus behavior требуют отдельных решений.

Устаревшие status summaries нельзя принимать за current:
- прежний docs/HANDOFF_CURRENT.md описывал Build #65; этот checkpoint его заменяет;
- docs/CURRENT_DATABASE_SCHEMA.md ещё содержит Catalog v1/new pair v4/v1:
  фактический код ProtectedStorageDatabaseService и docs/STORAGE_CATALOG_SCHEMA.md задают v4/v2;
- части docs/CRYPTOGRAPHY.md перечисляют уже сделанные history/migration/runtime-delivery этапы
  как отсутствующие. Native #33 подтверждает unpackaged runtime, но не финальный installer.
- README/общая ARCHITECTURE также содержат ранние status/planned/candidate формулировки.
Исправление остальных status summaries отдельно не выполнялось; не открывать закрытые задачи
по таким старым формулировкам и не смешивать их cleanup с новой implementation задачей.

## J. Ветки, рабочая среда и отсутствие скрытой незавершённой работы

До публикации этого docs checkpoint проверены 103 ветки, open PR/issues — 0.
Последние:
- feat/current-clipboard-history-read-repository → 072b31e428f0202f1fa08c679e6c565a0d6740a7;
- docs/model-recommendation-rule → 5050d2c0a5ed60f5af5265c295c138dd5d16bc8e;
- feat/journal-active-storage-session-accessor → 35207c2d9d46e6b53abe3287fceb8fa2a45f25b2.
Остались исторические ветки, включая 15 вершин вне истории main при восстановлении;
их наличие не означает active task или обязательство merge. Branch hygiene не начиналась.
Публикация этого документа добавляет docs branch; fresh список получать с pagination.

Среда предыдущей сессии: /workspace/scratch/a7ce8b8d8338/Clipensk —
частичная локальная копия нужных файлов, НЕ полный git checkout. Не использовать как source of truth.
Все изменения функционального tranche сохранены в GitHub. Несохранённого implementation нет.
Работа шла GitHub connector API; .NET SDK локально отсутствовал. Windows build — GitHub Actions.
Для runs по SHA использовался GET actions/runs?head_sha=...; wrapper
fetch_commit_workflow_runs ограничен pull_request event и может пропускать main push Build.
Для окончательного evidence читать run metadata и job steps, не один общий commit status.

## K. Exact resume point и bootstrap

Следующий чат должен начать с:
1. Fresh main → exact Actions Build по его head_sha; отдельно проверить relevant Native SQLCipher.
2. Прочитать AGENTS.md и authoritative contracts, учесть publication checkpoint и расхождения docs.
3. Сообщить, что Current read repository уже DONE и Build #129 / Native #33 SUCCESS.
   Если новый docs Build ещё идёт — назвать точный run/SHA/status, PASS ему не приписывать.
4. Дать краткий отчёт о восстановлении и ждать команды пользователя.
5. Только по новой команде разработки перейти к согласованию источника global capture policy
   или другой явно выбранной независимой задаче. Не повторять реализованный read repository.

Рекомендация модели:
- для полного восстановления контекста и сверки документов — GPT-5.6 Sol, Средняя;
- для одной проверки exact CI — GPT-5.6 Sol, Низкая;
- для источника global policy и app-level composition — GPT-6 Astra, Высокая;
- Очень высокая уместна для отдельной сложной проработки конкурентного lifecycle.
Это рекомендации, выбор всегда за пользователем; Максимальная сейчас не требуется.

## L. Последующее продолжение: protected history read composition

После подготовки исходного перехода пользователь дал новую команду «Продолжай разработку».
Docs checkpoint 2041b246454f108fd47b05aba40b7cbbb56ea56f был подтверждён:
Build #130 run 34007846980 — SUCCESS всех четырёх обязательных шагов.

Следующий небольшой tranche:
- ProtectedClipboardHistoryServices теперь предоставляет HistoryRepository через
  ICurrentClipboardHistoryRepository;
- Create собирает repository с той же active session и connection factory;
- creation по-прежнему не открывает БД и не вызывает extension provider;
- добавлены проверки caller cancellation, lock и dispose через полученный repository;
- контракт описан в docs/CLIPBOARD_HISTORY_SCHEMA.md;
- UI, worker и global policy не подключены и не выбраны.

Ветка: feat/protected-history-read-composition.
Этот раздел публикуется вместе с изменением кода. Его exact SHA и CI нужно получить свежим
запросом GitHub; Build #130 относится к ПРЕДЫДУЩЕМУ docs checkpoint и не подтверждает этот tranche.
Для продолжения сначала проверить exact Build и relevant Native run фактического main,
затем вернуться к разделу K. Основной blocker global policy остаётся открытым.
