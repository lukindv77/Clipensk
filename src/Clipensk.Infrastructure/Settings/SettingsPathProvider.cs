namespace Clipensk.Infrastructure.Settings;

public static class SettingsPathProvider
{
    public static string GetDefaultSettingsPath() =>
        Path.Combine(GetUserApplicationDirectory(), "settings.json");

    /// <summary>
    /// The data root offered when the user has not picked one, per explicit product decision:
    /// each Windows user gets their own settings and their own storage, placed by default only
    /// where that user already has private access. <c>LocalApplicationData</c> resolves inside the
    /// current user's profile, which Windows restricts to that user, so the default location
    /// cannot land in a shared or public folder that another account could reach.
    /// </summary>
    public static string GetDefaultDataRootPath() =>
        Path.Combine(GetUserApplicationDirectory(), "Data");

    /// <summary>
    /// Backs the single-instance guard. It deliberately lives beside <c>settings.json</c> rather
    /// than inside the data root: the guard must be per Windows user (one running Clipensk per
    /// user, other users unaffected), and it must keep working regardless of where — or whether —
    /// a data root has been configured.
    /// </summary>
    public static string GetInstanceLockPath() =>
        Path.Combine(GetUserApplicationDirectory(), "instance.lock");

    private static string GetUserApplicationDirectory()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Не удалось определить каталог LocalApplicationData для настроек Clipensk.");
        }

        return Path.Combine(localApplicationData, "Clipensk");
    }
}
