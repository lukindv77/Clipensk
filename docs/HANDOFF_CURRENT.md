# NEW CHAT HANDOFF — Clipensk

Дата проверки: 2026-09-07 UTC. Работа остановлена после успешного CI и подготовки перехода.

## A. Project identity и источники

Clipensk — резидентный Windows clipboard-history manager, .NET 10, WinUI 3 / Windows App SDK, только x64/AMD64. Репозиторий: https://github.com/lukindv77/Clipensk. Каноническая ветка — main. GitHub — source of truth.

Этот актуализированный checkpoint сохраняется как docs/HANDOFF_CURRENT.md в отдельной ветке docs/handoff-2026-09-07-delivery. В main файл HANDOFF_CURRENT.md содержит исторические слои A–P: старые состояния Current v4 и «источник policy неизвестен» там superseded. Ветка перехода содержит только документацию, не новый implementation; её CI не заявляется. Не требуется автоматически сливать её в main.

Обязательно прочитать AGENTS.md и docs/WORKFLOW_NEW_CHAT_HANDOFF.md. Для продолжения: docs/REQUIREMENTS.md, ARCHITECTURE.md, GLOBAL_CAPTURE_POLICY.md, PROTECTED_CLIPBOARD_DELIVERY_COMPOSITION.md, CLIPBOARD_CAPTURE_SIZE_LIMITS.md, APPLICATION_IDENTITY.md, CLIPBOARD_HISTORY_SCHEMA.md, STORAGE_CATALOG_SCHEMA.md, CURRENT_DATABASE_SCHEMA.md, CRYPTOGRAPHY.md, NATIVE_SQLCIPHER_BUILD.md, OPEN_QUESTIONS.md — все в docs/.

## B. User intent

Пользователь просит завершить текущие Actions и подготовить переход. Новую разработку в этой задаче не начинать. Новый чат сначала восстанавливает контекст и сверяет GitHub, сообщает main/CI/завершённое/блокеры, затем ждёт команды. Завершить восстановление фразой: «Контекст восстановлен. Готов продолжать. Жду вашей команды.»

После каждой задачи, остановки и сессии рекомендовать модель, уровень сложности и кратко объяснять выбор. Пользователь выбирает сам; не переключать модель автоматически. Различать Низкая, Средняя, Высокая, Очень высокая и Максимальная. Пользователь сообщил Plus и доступ к Astra; точные квоты не предполагать.

## C. Current authoritative state

Фактический main: 7a1b1c306e2d95438c95898a20681b3a8346e7e5.
Commit: feat: compose protected clipboard delivery from persisted policy.
Ветка реализации: feat/protected-delivery-composition; её commit уже входит в main.
Main обновлён force:false после свежего сравнения: behind=0, merge base=1b9bb53361663c8399034149a62d0b0bd7b81b37.

Exact Build #137, run 34069061781, тот же head_sha: completed / SUCCESS.
https://github.com/lukindv77/Clipensk/actions/runs/34069061781
Проверены SUCCESS: Verify x64-only implementation scope, Restore, Build, Test. Это подтверждённый PASS.

Exact Native SQLCipher #37, run 34069061764, тот же head_sha: completed / SUCCESS.
https://github.com/lukindv77/Clipensk/actions/runs/34069061764
Проверены native x64 build, provenance, smoke host, encrypted storage verification, unpackaged x64 runtime publish, runtime SQLCipher loading verification и uploads — SUCCESS.

Открытые PR/issues: 0 на момент fresh проверки. Оба текущих Actions завершены; CI fix не нужен. Ручной WinUI smoke, финальный installer и end-to-end capture этим evidence не подтверждены.

Локальные папки /workspace/scratch/a7ce8b8d8338/clipensk-delivery и прежние snapshots — частичные копии, не полный git checkout и не источник истины. Локального .NET SDK не было; C# проверен GitHub Actions. Несохранённого implementation текущей задачи нет. Нужна fresh TOCTOU-проверка перед следующей записью.

## D. Current owner / active task

Текущая задача — закрыть проверку Build #137 / Native #37 и передать checkpoint — DONE. Нового активного implementation нет. Acceptance выполнен: exact SHA и обязательные шаги проверены, ошибки отсутствуют, состояние и ограничения сохранены.

Protected delivery composition DONE; автоматический вызов из App, worker lifecycle, источник custom-binary extensions и готовый журнал остаются NOT READY. Их нельзя объявлять закрытыми по результату этих тестов.

## E. What has been completed

- Protected lifecycle/session, cancellation leases/epochs, password → MasterKey, SQLCipher bootstrap; signal queue, retained clipboard snapshot, readers, time/source metadata, policy/size gates.
- Durable ApplicationId registry/aliases, per-application policies.
- Current history schema, transactional sink, SHA external storage, Catalog v2 index/migration/resolver; lazy ProtectedClipboardHistoryServices.
- Current history read repository с обязательными периодом и положительным limit; подключение HistoryRepository к protected services; keyset pagination UTC + EventId без OFFSET. Последнее исправление тестовой FK-фикстуры: 19fcc793cbc6c22ba641dbc447e7fcad07ae3f08, Build #133 SUCCESS. Production pagination при этом не менялась.
- Global policy persistence: 8906e4b154e3b231eca138e578bf799686da98db, Build #134 / Native #36 SUCCESS. Current v5, Catalog v2.
- Первичная policy UI: 52e889128378056ec01f22e98a3691fe31b434da; Build #135 упал на StackPanel.IsEnabled. Исправлено ContentControl-обёрткой: 1b9bb53361663c8399034149a62d0b0bd7b81b37, Build #136 SUCCESS. Ручной UI smoke не выполнен.
- ProtectedClipboardDeliveryServices и Core IClipboardAcceptedCaptureDeliveryFactory; Windows ResidentWindowsHost adapter; тесты и контракт. Текущий main, Build #137 / Native #37 SUCCESS.

## F. Current conclusions и контракты

Источник global policy уже определён: зашифрованный Current выбранного storage. Новая пара и миграция оставляют policy ненастроенной, без seed/defaults. ReadAsync возвращает null только для отсутствия настройки; ошибка не превращается в null/Allow/Deny. InitializeAsync допускает только первую запись; последующее изменение требует cleanup по REQUIREMENTS §18, update API отсутствует. Global Deny — базовое наследуемое правило, application override может его заменить; это не абсолютный выключатель.

UI предлагает Text, Html, Rtf, Bitmap, WebLink, ApplicationLink, StorageItems без предвыбранных правил/размеров. Нужны явные Allow/Deny и для разрешённого формата positive Int64 bytes либо явно «Без лимита». Custom formats автоматически не добавляются. SQLite выполняется вне UI thread; generation/session guards отклоняют stale results; lock/close очищает поля. Успешное сохранение не запускает worker.

ProtectedClipboardDeliveryServices.TryCreateAsync принимает active session, explicit delivery factory, обязательный IClipboardCustomBinaryFileExtensionProvider, optional connection factory и caller token. Читает persisted policy; при null не создаёт delivery. Capture/history services используют одну session/factory; ResidentWindowsHost создаёт существующий identity-aware pipeline без Windows → Storage dependency. Factory строит inert graph без обработки очереди, clipboard reads или отдельного ресурса teardown. Результат обёрнут ProtectedClipboardAcceptedCaptureDelivery. Проверяется cancellation до/после factory; creation token не становится lifetime результата. Lock/dispose/reopen блокирует старую delivery. Успех после COMMIT сохраняется при поздней отмене. App этот boundary ещё не вызывает.

History reads: Current ReadOnly, явные включительные CalendarDate period и limit>0; LIMIT до JOIN payload; сортировка EventUtc DESC, EventId BINARY DESC, payload по PayloadOrder. Detached results сохраняют полный time/source context и persisted addresses; external bytes не читаются. ReadBeforeAsync использует exclusive UTC/EventId cursor с периодом; смена периода требует нового ReadAsync. EventId — canonical lowercase D. Каждая страница — отдельный snapshot, общего snapshot просмотра нет. Caller отвечает за очистку уже возвращённых данных при lock. SQLite не обещает preemptive interruption синхронного вызова.

## G. Important invariants

- Только Windows x64/AMD64: Platforms=x64, RuntimeIdentifiers=win-x64. ARM64 не добавлять.
- WM_CLIPBOARDUPDATE WndProc только signal/enqueue. Monitoring только при unlocked protected access; worker не запускать до готовности lifecycle/policy/sink/dependencies.
- Один DataPackageView от format discovery до reader calls; повторный Clipboard.GetContent() на reader stage запрещён.
- Полный EventTimeContext: UTC timestamp, local offset, Windows timezone ID, calendar date. SourceApplication != InvocationApplication.
- Durable identity — Clipensk-owned ApplicationId; PID/HWND/path/display name/AUMID не primary key. Не вводить silent merge/alias heuristics; конфликты fail closed.
- Не придумывать format/size defaults, archive/catalog schema или новый extension fallback.
- MaxBytes: Text/HTML/RTF — UTF-8 exact string; WebLink/ApplicationLink — UTF-8 Uri.OriginalString; Bitmap — normalized PNG; StorageItems — versioned canonical UTF-8 JSON v1; custom binary — exact bytes. SearchText/metadata/FTS/overhead не считаются. Null — нет лимита, заданный лимит положительный.
- HTML SearchText после size gate; RTF SearchText пока null. CF_WAVE/CF_RIFF/virtual file contents запрещены; CF_HDROP не копирует содержимое файлов.
- Все external payload разрешаются до history SQL transaction; byte counts/address валидируются; cancellation до COMMIT, без превращения committed success в late cancellation.
- Catalog v2: SHA exact bytes → persisted RelativePath + SizeBytes, путь unique; первый persisted path выигрывает. Same-SHA size conflict/path collision fail closed. Duplicate не получает дату нового capture как firstStoredDate.
- External storage: root containment, exact size+SHA existing file, temp+atomic move; reservation до exact-address ensure. Existing custom SHA не требует extension provider; новый требует explicit provider, без скрытого .bin. PNG использует .png. External files не шифруются MasterKey.
- Whole-pair validation до migration mutation. Current v5 / Catalog v2; history layout введён в v4. Пароль не persistится, storage-crypto.json не содержит пароль/ключ; cleanup ключей best-effort.

Durable workflow: feature/fix/docs branches; перед каждым main update свежий main, compare, behind=0, merge base=current main; update_ref(force:false). После main update exact Build по head_sha; PASS только по четырём обязательным SUCCESS steps. Failure → первый failed step/log → минимальный fix. Во время CI только запланированная независимая работа; пустого polling нет. Если полезной работы нет — точные run/SHA/status и остановка, не ждать свыше 2 минут. Для runs использовать actions/runs?head_sha=...: fetch_commit_workflow_runs может показывать только pull_request runs.

## H. Known risks / unresolved questions

Источник расширений новых custom binary payload ещё не определён; нельзя подставить .bin. App composition после unlock/первичной настройки и worker teardown/restart не подключены. Ручное WinUI испытание отсутствует. Нет доказанной end-to-end записи реального clipboard через приложение.

Отдельные будущие направления: history UI/FTS/source filters; policy cleanup; auto-lock teardown; Current→Archive schema/lifecycle/read path; Catalog rebuild/reference tracking/GC; password/MasterKey change и recovery; identity merge/rebind UI; финальная упаковка/лицензия/product support floor. Не начинать их при восстановлении.

Архивный принцип уже принят: целые календарные дни, один archive owner дня; copy/verify перед purge, временный дубль допустим, потеря обеих копий запрещена. Архивы обычно ReadOnly, запись maintenance; Catalog rebuild из Current+Archive; Trash требует глобальной проверки ссылок, утверждённый retention 30 дней. Детальные schema/lifecycle не реализованы.

Старые status-summary в HANDOFF_CURRENT.md main и частях CRYPTOGRAPHY/общих docs могут отставать. Current v4 или «неизвестная global policy» не считать текущим состоянием. При противоречиях проверять последние предметные контракты и код; не повторять закрытые этапы.

## I. Remaining work

В этой сессии ничего не разрабатывать. После восстановления и отдельной команды: определить requirements-backed источник custom-binary extensions; затем app-level вызов готовой composition после unlock/первичной настройки. Полный worker lifecycle — отдельный tranche с проверками lock/unlock, cancellation, старой session, закрытия и COMMIT. Предварительно сверить актуальные requirements; новые продуктовые решения не выдумывать.

## J. Exact resume point

Следующий чат должен начать с fresh проверки main и exact CI его SHA, затем прочитать актуальный checkpoint из docs/handoff-2026-09-07-delivery и authoritative contracts. Для 7a1b1c306e2d95438c95898a20681b3a8346e7e5 Build #137 / Native #37 уже SUCCESS; если main изменился, проверить новые runs отдельно. Сообщить восстановленное состояние и ждать команды, не запускать разработку автоматически.

## K. First-turn bootstrap

Прочитать переход полностью; mutable GitHub state важнее snapshot. Не повторять выполненное исследование/реализацию без причины. Не подгонять репозиторий под старый handoff. Не смешивать подтверждённые контракты, CI evidence и отсутствующий runtime/UI evidence. Полные старые чаты не гарантированно доступны.

Для восстановления рекомендованы GPT-5.6 Sol · Средняя; для будущей app composition — GPT-6 Astra · Высокая; для полного конкурентного worker lifecycle — GPT-6 Astra · Очень высокая. Максимальная сейчас не обоснована. Выбор модели остаётся за пользователем.

Инструкция пользователю: загрузить этот файл в новый чат и написать «Продолжай с указанной точки». Эта точка означает восстановление, fresh проверку и ожидание следующей команды.
