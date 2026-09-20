using Clipensk.Core.Localization;
using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Localization;

namespace Clipensk.App;

public partial class App
{
    /// <summary>
    /// Best-effort startup load of the persisted active translation file, per
    /// <c>docs/REQUIREMENTS.md</c> §20. A missing or corrupted external file must never prevent
    /// startup: on any failure the overlay simply stays empty and <see cref="ExternalOverlayLocalizationService"/>
    /// falls back to the built-in Russian strings, exactly as if no external translation had ever
    /// been configured.
    /// </summary>
    private static async Task TryApplyActiveLocalizationAsync(
        ExternalOverlayLocalizationService localization,
        ApplicationSettings settings)
    {
        if (settings.ActiveLocalizationFileName is not { } fileName ||
            string.IsNullOrWhiteSpace(settings.DataRootPath))
        {
            return;
        }

        string filePath = Path.Combine(settings.DataRootPath, "Languages", fileName);
        try
        {
            IReadOnlyDictionary<string, string> overlay =
                await new JsonExternalLocalizationLoader().LoadAsync(filePath);
            localization.SetOverlay(overlay);
        }
        catch
        {
            // Оставить встроенный русский: сломанный или удалённый внешний файл перевода не должен
            // мешать запуску приложения.
        }
    }
}
