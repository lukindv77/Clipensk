using Clipensk.Core.Input;

namespace Clipensk.Core.Settings;

/// <summary>
/// The first-run setup's last step (smoke finding З4, 2026-09-24): the parameters the user confirms
/// explicitly before Clipensk starts working — the journal hotkey, which has no default, autostart,
/// auto-lock and the journal's default period.
/// </summary>
public sealed record InitialSetupParameters(
    HotKeyGesture? JournalHotKey,
    bool AutostartEnabled,
    bool AutoLockEnabled,
    int? AutoLockAfterMinutes,
    int? DefaultJournalPeriodDays)
{
    /// <summary>The localization key of the first invalid choice, or <c>null</c> when all are valid.</summary>
    public string? FindProblem()
    {
        if (JournalHotKey is not { } hotKey ||
            hotKey.VirtualKey == 0 ||
            hotKey.Modifiers == HotKeyModifiers.None)
        {
            return "Setup.Parameters.HotKeyRequired";
        }

        if (AutoLockEnabled ? AutoLockAfterMinutes is not > 0 : AutoLockAfterMinutes is <= 0)
        {
            return "Settings.Lock.Invalid";
        }

        return DefaultJournalPeriodDays is <= 0 ? "Settings.JournalPeriod.Invalid" : null;
    }

    public ApplicationSettings ApplyTo(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (FindProblem() is { } problem)
        {
            throw new InvalidOperationException($"Initial setup parameters are invalid: {problem}.");
        }

        return settings with
        {
            JournalHotKey = JournalHotKey,
            AutostartEnabled = AutostartEnabled,
            AutoLockEnabled = AutoLockEnabled,
            AutoLockAfterMinutes = AutoLockAfterMinutes,
            DefaultJournalPeriodDays = DefaultJournalPeriodDays,
            InitialSetupCompleted = true,
        };
    }
}
