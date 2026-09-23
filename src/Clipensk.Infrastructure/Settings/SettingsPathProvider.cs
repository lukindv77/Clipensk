
namespace Clipensk.Infrastructure.Settings;

public static class SettingsPathProvider
{
    /// <summary>
    /// <c>settings.json</c> lives beside <c>Clipensk.exe</c>, per product decision
    /// (<c>docs/OPEN_QUESTIONS.md</c> §12, 2026-09-23): each Windows user runs their own unpacked
    /// copy of Clipensk, so the settings in its folder belong to that user alone. The settings do not
    /// travel with the data root; relocating the storage only changes the path they name. A
    /// <c>settings.json</c> an earlier version left in <c>%LocalAppData%\Clipensk</c> is ignored.
    /// </summary>
    public static string GetDefaultSettingsPath() =>
        Path.Combine(GetProgramDirectory(), "settings.json");

    /// <summary>The folder Clipensk was started from.</summary>
    public static string GetProgramDirectory() =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

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
    /// Backs the single-instance guard. It deliberately lives in the user's profile rather than
    /// beside the settings or inside the data root: the guard must be per Windows user (one running
    /// Clipensk per user, whichever copy of the program it was started from, other users
    /// unaffected), and it must keep working regardless of where — or whether — a data root has
    /// been configured.
    /// </summary>
    public static string GetInstanceLockPath() =>
        Path.Combine(GetUserApplicationDirectory(), "instance.lock");

    /// <summary>
    /// Whether new files can be created in <paramref name="directory"/>: a probe file is created and
    /// removed. Clipensk refuses to start when its program folder fails this, because it could not
    /// keep its settings there (for example under <c>C:\Program Files</c>).
    /// </summary>
    public static bool CanCreateFilesIn(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string probe = Path.Combine(directory, ".clipensk-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
