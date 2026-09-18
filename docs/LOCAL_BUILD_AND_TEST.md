# Локальная сборка и тесты вне Windows CI

Этот документ описывает, как получить быструю локальную обратную связь по кроссплатформенной части Clipensk в Linux-окружении разработки (в частности, в cloud-сессии Claude Code). Он **не отменяет** требование exact-SHA CI evidence и не заменяет Windows-проверки.

## Что можно проверять локально

| Проект | TFM | Локально |
|---|---|---|
| `Clipensk.Core` | `net10.0` | да |
| `Clipensk.Storage` | `net10.0` | да |
| `Clipensk.Core.Tests` | `net10.0` | да |
| `Clipensk.Storage.Tests` | `net10.0` | да (использует `bundle_e_sqlite3`, не SQLCipher) |
| `Clipensk.Infrastructure.Tests` | `net10.0` | да |
| `Clipensk.App`, `Clipensk.Windows` | WinUI / Windows App SDK | **нет**, только Windows CI |
| Native SQLCipher, published-runtime verification | — | **нет**, только Windows CI |

Локальный прогон трёх тестовых проектов занимает примерно 10 секунд против нескольких минут полного CI-цикла.

## Требования к сетевой политике окружения

Дефолтный уровень **Trusted** в cloud-окружении Claude Code содержит `dotnet.microsoft.com` и `dot.net`, но **без wildcard**, поэтому поддомен с бинарниками SDK недоступен. Нужен уровень **Custom** с включённым флагом «Also include default list of common package managers» и дополнительными доменами:

```text
builds.dotnet.microsoft.com
aka.ms
dotnetcli.azureedge.net
dotnetbuilds.azureedge.net
globalcdn.nuget.org
```

Минимально достаточны первые две строки. `api.nuget.org` входит в Trusted-список и дополнительного разрешения не требует.

Настройка: `claude.ai/code` → иконка облака над полем ввода → шестерёнка у нужного окружения → **Network access: Custom**. Отдельной страницы настроек у селектора нет.

Диагностика при отказе: `curl -sS "$HTTPS_PROXY/__agentproxy/status"`, поле `recentRelayFailures` называет заблокированный хост и причину. Отказ политики выглядит как `connect_rejected` / `403 CONNECT`. Обходить политику запрещено; TLS-проверку отключать нельзя.

## Установка SDK

Версия закреплена в `global.json` (`rollForward: latestPatch`).

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --version 10.0.400 --install-dir "$HOME/.dotnet" --no-path
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

## Прогон тестов

`Clipensk.slnx` целиком локально не собирается: он содержит WinUI-проекты. Запускать тестовые проекты нужно **по одному** — текущий `Microsoft.Testing.Platform` runner из `global.json` на нескольких `.csproj` в одной команде возвращает `Zero tests ran` и exit code 5.

```bash
dotnet test tests/Clipensk.Storage.Tests/Clipensk.Storage.Tests.csproj --configuration Release
dotnet test tests/Clipensk.Core.Tests/Clipensk.Core.Tests.csproj --configuration Release
dotnet test tests/Clipensk.Infrastructure.Tests/Clipensk.Infrastructure.Tests.csproj --configuration Release
```

Сумма трёх проектов должна совпадать с общим числом тестов в Windows CI.

## Границы применимости

- Локальный PASS **не является** acceptance evidence. Принятие изменения по-прежнему требует exact-SHA CI.
- Изменения в `src/Clipensk.Storage/**` или `src/Clipensk.Core/Storage/**` по-прежнему требуют exact-main Build **и** Native SQLCipher.
- Локальные тесты идут на `e_sqlite3`, а не на SQLCipher, поэтому поведение шифрования, native provenance и published-runtime loading локально не проверяются вообще.
- Manual production WinUI smoke остаётся отдельным и независимым от любой автоматики.

Практическая ценность локального прогона — ловить ошибки компиляции, регрессии контрактов схемы и логики репозиториев до пуша, экономя CI-циклы.
