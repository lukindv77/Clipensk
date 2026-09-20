namespace Clipensk.Core.Settings;

/// <summary>
/// The pure rule for computing the journal's default display period from the persisted setting,
/// per <c>docs/REQUIREMENTS.md</c> §8: "при открытии журнала отображается именно этот период."
///
/// This lives in Core, separate from <see cref="ApplicationSettings"/>, so the WinUI page that
/// applies it stays a thin caller of a tested rule rather than carrying date arithmetic of its own.
/// </summary>
public static class DefaultJournalPeriod
{
    /// <summary>
    /// Validates a candidate value for <see cref="ApplicationSettings.DefaultJournalPeriodDays"/>.
    /// <c>null</c> means no default is configured, which is itself valid: the mechanism is opt-in
    /// until a product default is chosen (<c>docs/OPEN_QUESTIONS.md</c> §7).
    /// </summary>
    public static void Validate(int? days)
    {
        if (days is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(days),
                "Default journal period must span at least one calendar day.");
        }
    }

    /// <summary>
    /// The inclusive calendar range the journal should display on open, ending on
    /// <paramref name="today"/> and spanning <paramref name="days"/> complete calendar days.
    /// A one-day period is exactly <paramref name="today"/> on both ends.
    /// </summary>
    public static (DateOnly Start, DateOnly End) ForDays(int days, DateOnly today)
    {
        Validate(days);
        return (today.AddDays(-(days - 1)), today);
    }
}
