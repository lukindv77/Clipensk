namespace Clipensk.Core.Settings;

/// <summary>
/// The pure mapping between the Settings editing surface and <see cref="ArchiveRotationSettings"/>.
///
/// The editing surface expresses the physical-size threshold in whole megabytes, because the
/// protocol's byte threshold is an Archive file length and typing it in bytes is not usable. The
/// mapping is exact in both directions for every value this surface can produce. A settings file
/// edited by hand may still carry a byte threshold that is not a whole number of megabytes;
/// <see cref="HasSubMegabyteRemainder"/> reports that so the editor can warn before saving changes
/// the stored value.
///
/// This type exists so the mapping and its validation live where tests can reach them: the WinUI
/// settings page is not covered by any automated test.
/// </summary>
public sealed record ArchiveRotationSettingsDraft
{
    public const long BytesPerMegabyte = 1024L * 1024L;

    private const long MaxRepresentableMegabytes = long.MaxValue / BytesPerMegabyte;

    public long? MaxRecordCount { get; init; }

    public long? MaxMegabytes { get; init; }

    public int? MaxCalendarDays { get; init; }

    public ArchiveRotationThresholdMode? ThresholdMode { get; init; }

    /// <summary>
    /// True when the stored byte threshold this draft was read from is not a whole number of
    /// megabytes, so saving the draft back would change it.
    /// </summary>
    public bool HasSubMegabyteRemainder { get; init; }

    /// <summary>No configured threshold at all, which means rotation is off.</summary>
    public bool IsEmpty =>
        !MaxRecordCount.HasValue && !MaxMegabytes.HasValue && !MaxCalendarDays.HasValue;

    public static ArchiveRotationSettingsDraft FromSettings(ArchiveRotationSettings? settings)
    {
        if (settings is null)
        {
            return new ArchiveRotationSettingsDraft();
        }

        long? megabytes = null;
        bool remainder = false;
        if (settings.MaxBytes is long maxBytes)
        {
            // A threshold below one megabyte cannot be represented by this surface. Clamping to one
            // keeps the draft valid, and the remainder flag tells the editor the value would change.
            megabytes = Math.Max(1, maxBytes / BytesPerMegabyte);
            remainder = maxBytes % BytesPerMegabyte != 0;
        }

        return new ArchiveRotationSettingsDraft
        {
            MaxRecordCount = settings.MaxRecordCount,
            MaxMegabytes = megabytes,
            MaxCalendarDays = settings.MaxCalendarDays,
            ThresholdMode = settings.ThresholdMode,
            HasSubMegabyteRemainder = remainder,
        };
    }

    /// <summary>
    /// Builds the durable settings, or <c>null</c> when no threshold is configured, which turns
    /// rotation off. Invalid combinations throw exactly what
    /// <see cref="ArchiveRotationSettings.Validate"/> throws, so they can never reach the settings
    /// file.
    /// </summary>
    public ArchiveRotationSettings? ToSettings()
    {
        if (IsEmpty)
        {
            // An explicit mode selection with nothing to apply it to is not "rotation off" — it is
            // an unfinished choice. Returning null here would silently discard the selected Any/All
            // mode instead of turning rotation off, per the product decision that choosing a
            // rotation variant in Settings must require filling in what it applies to.
            if (ThresholdMode.HasValue)
            {
                throw new ArgumentException(
                    "Archive rotation mode is selected but no threshold is configured to apply it to.",
                    nameof(ThresholdMode));
            }

            return null;
        }

        if (MaxMegabytes is long megabytes && megabytes > MaxRepresentableMegabytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxMegabytes),
                "Archive rotation size threshold is too large to express in bytes.");
        }

        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = MaxRecordCount,
            MaxBytes = MaxMegabytes.HasValue ? MaxMegabytes.Value * BytesPerMegabyte : null,
            MaxCalendarDays = MaxCalendarDays,
            ThresholdMode = ThresholdMode,
        };
        settings.Validate();
        return settings;
    }
}
