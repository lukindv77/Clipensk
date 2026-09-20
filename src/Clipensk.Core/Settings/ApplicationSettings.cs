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

    public ArchiveRotationSettings? ArchiveRotation { get; init; }

    /// <summary>
    /// How many calendar days the journal shows by default when it opens, per
    /// <c>docs/REQUIREMENTS.md</c> §8. <c>null</c> means no default is configured
    /// (<c>docs/OPEN_QUESTIONS.md</c> §7 has not chosen a product default yet), and the journal
    /// falls back to its existing single-day behavior.
    /// </summary>
    public int? DefaultJournalPeriodDays { get; init; }
}
