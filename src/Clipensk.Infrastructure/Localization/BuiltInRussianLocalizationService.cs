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
