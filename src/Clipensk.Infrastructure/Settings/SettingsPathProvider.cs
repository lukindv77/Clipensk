namespace Clipensk.Infrastructure.Settings;

public static class SettingsPathProvider
{
    public static string GetDefaultSettingsPath()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Не удалось определить каталог LocalApplicationData для настроек Clipensk.");
        }

        return Path.Combine(localApplicationData, "Clipensk", "settings.json");
    }
}
