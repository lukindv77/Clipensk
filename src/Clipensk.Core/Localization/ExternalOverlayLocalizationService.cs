namespace Clipensk.Core.Localization;

/// <summary>
/// Looks strings up in an optional external translation overlay first, falling back to the given
/// inner service — normally <c>BuiltInRussianLocalizationService</c> — for any key the overlay does
/// not cover, per <c>docs/REQUIREMENTS.md</c> §20: "При отсутствии перевода используется встроенная
/// русская строка."
///
/// The overlay can be replaced at any time via <see cref="SetOverlay"/> (loading a different
/// translation file, rereading the active one, or reverting to built-in only) and is read on every
/// <see cref="GetString"/> call, so callers holding a reference to this service see the new language
/// immediately without needing a new instance.
/// </summary>
public sealed class ExternalOverlayLocalizationService : ILocalizationService
{
    private readonly ILocalizationService _fallback;
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, string> _overlay = new Dictionary<string, string>();

    public ExternalOverlayLocalizationService(ILocalizationService fallback)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>Replaces the active overlay. <c>null</c> reverts to the built-in fallback only.</summary>
    public void SetOverlay(IReadOnlyDictionary<string, string>? overlay)
    {
        lock (_gate)
        {
            _overlay = overlay ?? new Dictionary<string, string>();
        }
    }

    public string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        IReadOnlyDictionary<string, string> overlay;
        lock (_gate)
        {
            overlay = _overlay;
        }

        return overlay.TryGetValue(key, out string? value) ? value : _fallback.GetString(key);
    }
}
