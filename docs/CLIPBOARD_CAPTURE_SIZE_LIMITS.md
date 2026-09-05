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

`SearchText` является отдельным производным индексируемым representation и не участвует в `MaxBytes`. Его вычисление начинается только после того, как исходное representation прошло canonical size gate.

Для plain text `SearchText` совпадает с исходным текстом.

Для HTML сохраняется исходная строка `CF_HTML`, а `SearchText` строится managed parser-ом из fragment region. Fragment сначала определяется по `<!--StartFragment-->` / `<!--EndFragment-->`; если marker pair отсутствует, используются `StartFragment` / `EndFragment` как UTF-8 byte offsets согласно CF_HTML specification. HTML DOM parsing не выполняет scripts и network access; `script`, `style`, `noscript` и `template` не попадают в поисковый текст. Границы структурных элементов сохраняются как whitespace, HTML entities декодируются, последовательности whitespace нормализуются до одного пробела.

`Windows.Data.Html.HtmlUtilities` намеренно не используется: для WinUI 3 packaged applications Windows App SDK документирует этот legacy Trident-based API как неподдерживаемый. Managed HTML parser сохраняет одинаковую capture semantics для unpackaged и возможного packaged delivery.

Для RTF исходное representation уже может проходить capture/MaxBytes, но безопасный non-UI `RTF -> SearchText` boundary пока не реализован; до этого `SearchText` для RTF остаётся `null`. Raw RTF control syntax нельзя индексировать как подмену пользовательского текста.

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

Для `CF_HDROP` canonical capture representation v1 — компактный UTF-8 JSON без BOM и без форматирующих пробелов. Representation является частью принятого payload и сохраняется вместе с структурированными metadata, чтобы persistence не пересериализовывал одно и то же clipboard event по другим правилам.

Top-level property order фиксирован:

```json
{"version":1,"items":[...]}
```

Каждый item записывается в исходном clipboard-порядке и содержит свойства строго в таком порядке:

```text
order
fullPath
name
extension
isDirectory
preferredOperation
```

`order` обязан быть zero-based и contiguous (`0..N-1`) в том же порядке, в котором items вернул clipboard reader. `preferredOperation` имеет стабильные lowercase значения `unknown`, `copy`, `move`, `link`. Строковые значения сохраняются без нормализации регистра/пути; JSON escaping выполняется стандартным UTF-8 writer, а Unicode не заменяется произвольной ASCII-транслитерацией.

Пример:

```json
{"version":1,"items":[{"order":0,"fullPath":"C:\\Temp\\a.txt","name":"a.txt","extension":".txt","isDirectory":false,"preferredOperation":"copy"}]}
```

`CanonicalByteCount` равен точному количеству UTF-8 bytes этой canonical JSON строки:

```text
CanonicalByteCount = UTF8.GetByteCount(canonicalStorageItemsJson)
```

Проверка `MaxBytes` выполняется после `GetStorageItemsAsync` и формирования metadata, но **не читает содержимое выбранных файлов или каталогов**. Если canonical metadata превышает лимит, StorageItems route отклоняется и запись об этом отклонении не создаётся. Старый fail-closed defer для ненулевого `MaxBytes` больше не нужен.

Эта representation фиксирует payload/size semantics, но сама по себе не задаёт будущую физическую SQLite table layout: structured columns/relations могут храниться дополнительно, при этом canonical JSON остаётся точной принятой текстовой representation события.

## 7. Enforcement boundary

`MaxBytes` проверяется после получения/обязательной нормализации canonical representation, но до persistence. Если payload превышает лимит, он не передаётся в persistence и факт отклонения не создаёт запись журнала, в соответствии с `REQUIREMENTS.md`.

Эта семантика не задаёт числовые defaults и не включает автоматически ни один формат.
