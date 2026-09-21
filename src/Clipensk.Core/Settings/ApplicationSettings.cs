using Clipensk.Core.Input;

namespace Clipensk.Core.Settings;

public sealed record ApplicationSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? DataRootPath { get; init; }

    public HotKeyGesture? JournalHotKey { get; init; }

    public bool AutoLockEnabled { get; init; } = false;

    /// <summary>
    /// How many minutes of system-wide user inactivity trigger an automatic lock while
    /// <see cref="AutoLockEnabled"/> is true. <c>null</c> means the duration has not been
    /// configured yet, in which case auto-lock stays inert even when enabled: there is nothing to
    /// pick a default from (<c>docs/REQUIREMENTS.md</c> §3 fixes only that the option defaults to
    /// off, not a duration).
    /// </summary>
    public int? AutoLockAfterMinutes { get; init; }

    public int TrashRetentionDays { get; init; } = 30;

    public string PasswordHint { get; init; } = string.Empty;

    /// <summary>
    /// Defaults to a 30-calendar-day threshold per the product decision in
    /// <c>docs/OPEN_QUESTIONS.md</c> §8 — record-count and physical-size thresholds stay off by
    /// default. <c>null</c> means the user explicitly turned rotation off in Settings; an explicit
    /// JSON <c>null</c> in the settings file overrides this initializer on load, exactly like
    /// <see cref="DefaultJournalPeriodDays"/>, so a deliberate opt-out survives across restarts.
    /// </summary>
    public ArchiveRotationSettings? ArchiveRotation { get; init; } = new() { MaxCalendarDays = 30 };

    /// <summary>
    /// How many calendar days the journal shows by default when it opens, per
    /// <c>docs/REQUIREMENTS.md</c> §8. Defaults to 30 per the product decision in
    /// <c>docs/OPEN_QUESTIONS.md</c> §7. <c>null</c> means the user explicitly cleared it in
    /// Settings, in which case the journal falls back to its single-day behavior instead of
    /// silently reverting to the 30-day default — an explicit JSON <c>null</c> in the settings file
    /// overrides this initializer on load, so a deliberate clear survives across restarts.
    /// </summary>
    public int? DefaultJournalPeriodDays { get; init; } = 30;

    /// <summary>
    /// Bare file name (no directory separators) of the external translation file inside
    /// <c>&lt;DataRoot&gt;\Languages\</c> that is currently active, per <c>docs/REQUIREMENTS.md</c>
    /// §20. <c>null</c> means only the built-in Russian strings are used. Storing a bare name rather
    /// than an absolute path keeps the reference valid if the data root itself is ever moved, and
    /// resolving it against the Languages directory at read time is what actually confines file
    /// access to that directory — see the containment check in <c>JsonApplicationSettingsStore</c>.
    /// </summary>
    public string? ActiveLocalizationFileName { get; init; }

    /// <summary>
    /// Whether Clipensk starts automatically when the current Windows user logs in, per explicit
    /// product decision: opt-in through Settings, off by default, current-user only — never a
    /// machine-wide registration, which would need elevation this app never requests.
    /// </summary>
    public bool AutostartEnabled { get; init; } = false;
}
