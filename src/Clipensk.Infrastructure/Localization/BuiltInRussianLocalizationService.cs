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
    };

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Strings.TryGetValue(key, out string? value) ? value : key;
    }
}
