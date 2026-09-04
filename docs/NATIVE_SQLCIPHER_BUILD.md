# Native SQLCipher build — Windows

Статус: **x64 pipeline implemented; green native evidence is required before x64 is called PASS. ARM64 remains NOT READY.**

Дата фиксации pipeline: 4 сентября 2026 года.

Этот документ описывает воспроизводимый source-based native build для SQLCipher, используемого Clipensk. Он дополняет `CRYPTOGRAPHY.md` и не заменяет requirement о fresh CI evidence.

## 1. Pinned source inputs

Для текущего x64 pipeline зафиксированы:

- SQLCipher Community Edition `4.17.0`;
- SQLCipher commit `810db22f575ee7cf94ea96a3e91622b5fcece3dc`;
- OpenSSL `3.5.8`;
- OpenSSL source tarball SHA-256:
  `a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2`.

SQLCipher source fetch завершается проверкой exact commit. OpenSSL archive проверяется по exact SHA-256 до распаковки.

Готовые сторонние `e_sqlcipher` binaries в pipeline не используются.

## 2. Build entry point

Локальный/CI entry point:

```powershell
./eng/build-sqlcipher-windows-x64.ps1
```

По умолчанию результат помещается в:

```text
artifacts/native/win-x64/
```

Временная source/build директория `.native-build/` не является частью repository state и исключена из Git.

## 3. OpenSSL x64

OpenSSL собирается Visual Studio 2022 toolchain из исходников как static dependency:

```text
perl Configure VC-WIN64A no-shared no-tests no-asm
nmake
nmake install_sw
```

`no-asm` выбран для более простого и предсказуемого CI baseline. Это не окончательная performance-конфигурация продукта; оптимизация допустима отдельным tranche только после сохранения crypto/runtime compatibility evidence.

## 4. SQLCipher x64

SQLCipher строится `Makefile.msc` с OpenSSL crypto provider.

Ключевые compile flags:

```text
SQLITE_TEMP_STORE=2
SQLITE_HAS_CODEC=1
SQLITE_EXTRA_INIT=sqlcipher_extra_init
SQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown
SQLCIPHER_CRYPTO_OPENSSL=1
SQLITE_ENABLE_FTS5=1
SQLITE_ENABLE_RTREE=1
SQLITE_MAX_ATTACHED=125
```

Native output переименован в ожидаемое SQLitePCLRaw provider имя:

```text
sqlcipher.dll
sqlcipher.lib
```

После build script проверяет через `dumpbin`:

- PE machine = x64;
- export `sqlite3_open_v2`;
- export `sqlite3_key`;
- export `sqlite3_rekey`.

## 5. Build manifest

В artifact directory создаётся `native-build-manifest.json`, содержащий architecture, pinned source versions/commit/checksum и SHA-256 собранного `sqlcipher.dll`.

Рядом сохраняются license files SQLCipher, SQLite и OpenSSL.

Build timestamp означает время конкретного artifact build и поэтому сам по себе не является reproducibility proof. Exact input pinning и hash artifact нужны как provenance groundwork; byte-for-byte reproducible build ещё должен быть проверен отдельно.

## 6. Native smoke evidence

Tool `tools/Clipensk.SqlCipher.Smoke` использует production `ProtectedStorageDatabaseService` и `SqlCipherConnectionFactory`.

Smoke обязан:

1. создать `current.db` + `storage-catalog.db` случайным 32-byte MasterKey;
2. подтвердить protected storage initialization;
3. проверить, что первые 16 байт файлов не равны plaintext SQLite header `SQLite format 3\0`;
4. повторно открыть storage правильным MasterKey;
5. убедиться, что другой случайный MasterKey не открывает storage;
6. получить реальный `PRAGMA cipher_version` и `PRAGMA cipher_status=1` через production connection factory.

Только успешный smoke с собранным native `sqlcipher.dll` считается evidence реальной SQLCipher integration для соответствующей архитектуры.

## 7. CI workflow

Workflow `.github/workflows/sqlcipher-native.yml` на x64 собирает pinned OpenSSL + SQLCipher, публикует smoke host, запускает native smoke и сохраняет native build artifact/evidence на ограниченный срок.

Обычный `.github/workflows/build.yml` продолжает проверять managed Restore/Build/Test. Его PASS не заменяет native SQLCipher evidence.

## 8. ARM64

ARM64 пока **NOT READY**. Нельзя автоматически переносить x64 PASS на ARM64. Нужны отдельные MSVC/OpenSSL target configuration, SQLCipher build recipe, PE architecture verification, native smoke и CI evidence.

## 9. Packaging boundary

Даже green native build/smoke не означает, что production installer уже доставляет DLL. Отдельно остаётся включить verified `sqlcipher.dll` в выбранную схему распространения Clipensk для каждой архитектуры и проверить runtime loading из установленного приложения.
