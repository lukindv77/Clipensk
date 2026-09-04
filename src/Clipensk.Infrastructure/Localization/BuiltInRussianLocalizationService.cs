using Clipensk.Core.Localization;

namespace Clipensk.Infrastructure.Localization;

public sealed class BuiltInRussianLocalizationService : ILocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> Strings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["App.Title"] = "Clipensk",
        ["Journal.Title"] = "Журнал",
        ["Journal.Empty"] = "История буфера обмена пока не загружена.",
        ["Navigation.Journal"] = "Журнал",
        ["Navigation.Applications"] = "Приложения",
        ["Navigation.Maintenance"] = "Обслуживание",
        ["Navigation.Settings"] = "Настройки",
        ["Navigation.About"] = "О программе",
        ["Page.Applications.Title"] = "Приложения и правила сбора",
        ["Page.Maintenance.Title"] = "Обслуживание баз данных",
        ["Page.Settings.Title"] = "Настройки Clipensk",
        ["Page.About.Title"] = "О программе Clipensk",
        ["FirstRun.Title"] = "Первоначальная настройка",
        ["FirstRun.Body"] = "До создания баз данных и запуска журналирования выберите каталог, в котором Clipensk будет хранить пользовательские данные. Каталог будет проверен на доступность записи до сохранения настройки.",
        ["FirstRun.ChooseDataRoot"] = "Выбрать каталог данных",
        ["FirstRun.ValidationFailed"] = "Не удалось использовать выбранный каталог. Проверьте доступность записи и выберите другой каталог.",
        ["FirstRun.SavedLocked"] = "Каталог данных сохранён. Clipensk остаётся заблокированным до успешной проверки пароля.",
        ["Lock.Title"] = "Clipensk заблокирован",
        ["Lock.Body"] = "История и сбор данных буфера обмена недоступны до разблокировки. Без чтения защищённых данных доступны безопасные настройки и обслуживание.",
        ["Lock.PasswordHint"] = "Подсказка к паролю",
        ["Lock.PasswordHintEmpty"] = "Подсказка не задана.",
        ["Lock.PasswordPlaceholder"] = "Введите пароль",
        ["Lock.Unlock"] = "Разблокировать",
        ["Lock.PasswordRequired"] = "Введите пароль.",
        ["Lock.CryptoNotReady"] = "Проверка пароля ещё не подключена к криптографическому хранилищу. Пароль не сохранён, Clipensk остаётся заблокированным.",
        ["Settings.DataRoot.Title"] = "Каталог данных",
        ["Settings.DataRoot.NotConfigured"] = "Не настроен",
        ["Settings.HotKey.Title"] = "Горячая клавиша вызова журнала",
        ["Settings.HotKey.Key"] = "Основная клавиша",
        ["Settings.HotKey.Apply"] = "Применить горячую клавишу",
        ["Settings.HotKey.Saved"] = "Горячая клавиша сохранена и активирована.",
        ["Settings.HotKey.Failed"] = "Не удалось применить горячую клавишу. Прежняя комбинация сохранена.",
    };

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Strings.TryGetValue(key, out string? value) ? value : key;
    }
}
