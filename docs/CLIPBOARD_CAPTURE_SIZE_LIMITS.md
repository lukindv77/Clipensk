# Clipboard capture size-limit semantics

Этот документ фиксирует техническую семантику `ClipboardFormatCapturePolicy.MaxBytes`. Конкретные числовые defaults форматов остаются отдельным продуктовым решением.

## 1. Общий принцип

`MaxBytes` ограничивает размер **канонического capture representation**, которое Clipensk принимает для дальнейшей нормализации/сохранения как payload данного clipboard-формата.

В лимит не входят:

- служебные поля записи журнала;
- `EventTimeContext`, SourceApplication/InvocationApplication и другие metadata;
- производный `SearchText` для HTML/RTF;
- FTS-индексы;
- SQLite page/row/index overhead;
- физический overhead внешней файловой системы.

Проверка включительная: payload допустим, если `CanonicalByteCount <= MaxBytes`. `MaxBytes = null` означает отсутствие настроенного лимита. Нулевые и отрицательные значения недопустимы в policy model.

## 2. Стандартные текстовые форматы

Для Unicode/plain text, HTML и RTF canonical bytes — UTF-8 кодировка **точной строки representation, возвращённой clipboard reader**.

Следовательно:

```text
CanonicalByteCount = UTF8.GetByteCount(originalRepresentation)
```

`SearchText` является отдельной производной проекцией и не участвует в `MaxBytes`. Он вычисляется только после того, как raw representation прошёл canonical size check.

Для plain text `SearchText` совпадает с исходной строкой.

Для HTML Windows boundary сохраняет исходный CF_HTML representation без изменений, затем получает static fragment через `HtmlFormatHelper.GetStaticFragment` и преобразует fragment в plain text через `Windows.Data.Html.HtmlUtilities.ConvertToText`. Результат хранится отдельно как `SearchText`; raw HTML и его `CanonicalByteCount` не изменяются.

Для RTF raw representation уже читается и измеряется корректно, но отдельный production-safe SearchText extractor пока не зафиксирован. `Microsoft.UI.Text.RichEditTextDocument` предоставляет RTF conversion APIs, однако documented acquisition идёт через `RichEditBox.TextDocument`; до решения UI/thread hosting RTF extractor не подменяется самописным parser и может оставлять `SearchText = null`.

## 3. WebLink / ApplicationLink

Windows reader возвращает `Uri`. Canonical representation для лимита — `Uri.OriginalString`, закодированная UTF-8:

```text
CanonicalByteCount = UTF8.GetByteCount(uri.OriginalString)
```

Это же строковое representation является базой для последующего сохранения URL как текстового payload.

## 4. Изображения

Изображение сначала декодируется и обязательно нормализуется в PNG. `MaxBytes` применяется к точным байтам **нормализованного PNG**, то есть к тем же байтам, от которых затем вычисляется SHA-256 внешнего payload:

```text
clipboard image
  -> decode
  -> normalize PNG
  -> MaxBytes check
  -> SHA-256(normalized PNG bytes)
  -> external file persistence
```

Raw clipboard bitmap bytes до декодирования не являются основанием для `MaxBytes`.

## 5. Explicitly enabled custom binary

Registered/private format не становится доступным автоматически. Он попадает в read plan только если exact format name присутствует в effective policy с `Capture=Allow`; standard readers имеют приоритет, а custom binary reader используется только как fallback для явно выбранного неизвестного формата.

Безусловно запрещённые requirements форматы не могут быть переопределены custom policy: WinRT format IDs `RiffAudio` (CF_RIFF), `WaveAudio` (CF_WAVE) и shell virtual-file payload `FileContents` не маршрутизируются в custom binary reader даже при ошибочном `Capture=Allow`.

Windows custom binary boundary использует `DataPackageView.GetDataAsync(formatName)`. Полученный object обязан быть `IRandomAccessStream`; никакая произвольная object serialization или reinterpretation не выполняется. Если custom format не предоставляет random-access binary stream, capture этого route завершается fail-closed.

Canonical bytes — точные bytes stream, которые Clipensk собирается сохранить во внешний файл. Дополнительной нормализации нет:

```text
CanonicalByteCount = exact preserved binary byte count
```

Если `MaxBytes` задан, Windows reader сначала сравнивает объявленный `IRandomAccessStream.Size` с лимитом и отклоняет заведомо oversized stream **до выделения полного byte buffer**. После фактического чтения Core повторно измеряет exact captured bytes и ещё раз применяет `MaxBytes`; preflight не заменяет canonical post-read check.

Captured payload владеет собственной копией byte snapshot, чтобы последующее изменение исходного buffer не меняло принятое clipboard event representation.

Unknown registered/private binary formats остаются выключенными по умолчанию; этот reader не вводит default allow-list и не задаёт default size limits.

## 6. CF_HDROP / StorageItems

Для `CF_HDROP` сейчас зафиксированы сами metadata-поля, но ещё не зафиксировано их canonical persisted text representation. Поэтому размерный лимит `CF_HDROP` нельзя вычислять произвольной сериализацией.

До утверждения canonical metadata representation production execution обязан действовать fail-closed для `StorageItems` route с ненулевым `MaxBytes`: такой лимит нельзя молча игнорировать или измерять по случайному runtime object size. Route без `MaxBytes` может быть прочитан последующим stage, но persistence semantics остаётся отдельным tranche.

## 7. Enforcement boundary

`MaxBytes` проверяется после получения/обязательной нормализации canonical representation, но до persistence. Если payload превышает лимит, он не передаётся в persistence и факт отклонения не создаёт запись журнала, в соответствии с `REQUIREMENTS.md`.

Эта семантика не задаёт числовые defaults и не включает автоматически ни один формат.
