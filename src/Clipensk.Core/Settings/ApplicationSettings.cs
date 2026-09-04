using Clipensk.Core.Input;

namespace Clipensk.Core.Settings;

public sealed record ApplicationSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? DataRootPath { get; init; }

    public HotKeyGesture? JournalHotKey { get; init; }

    public bool AutoLockEnabled { get; init; } = false;

    public int TrashRetentionDays { get; init; } = 30;

    public string PasswordHint { get; init; } = string.Empty;
}
