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

Для HTML/RTF извлекаемый позднее `SearchText` является производным индексируемым representation и не участвует в `MaxBytes`.

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

Когда появится reader разрешённых registered/private binary formats, canonical bytes — точные байты, которые Clipensk собирается сохранить во внешний файл. Если обязательной нормализации для формата нет, лимит применяется к исходным сохраняемым байтам.

## 6. CF_HDROP / StorageItems

Для `CF_HDROP` сейчас зафиксированы сами metadata-поля, но ещё не зафиксировано их canonical persisted text representation. Поэтому размерный лимит `CF_HDROP` нельзя вычислять произвольной сериализацией.

До утверждения canonical metadata representation production execution обязан действовать fail-closed для `StorageItems` route с ненулевым `MaxBytes`: такой лимит нельзя молча игнорировать или измерять по случайному runtime object size. Route без `MaxBytes` может быть прочитан последующим stage, но persistence semantics остаётся отдельным tranche.

## 7. Enforcement boundary

`MaxBytes` проверяется после получения/обязательной нормализации canonical representation, но до persistence. Если payload превышает лимит, он не передаётся в persistence и факт отклонения не создаёт запись журнала, в соответствии с `REQUIREMENTS.md`.

Эта семантика не задаёт числовые defaults и не включает автоматически ни один формат.
