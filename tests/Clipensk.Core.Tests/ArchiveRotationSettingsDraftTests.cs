using Clipensk.Core.Settings;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ArchiveRotationSettingsDraftTests
{
    [Fact]
    public void ToSettings_EmptyDraftTurnsRotationOff()
    {
        var draft = new ArchiveRotationSettingsDraft();

        Assert.True(draft.IsEmpty);
        Assert.Null(draft.ToSettings());
    }

    [Fact]
    public void ToSettings_ThresholdModeWithoutAnyThresholdRequiresOneToBeFilledIn()
    {
        // Picking Any/All without any threshold is an unfinished choice, not "rotation off": the
        // product decision requires the user to fill in what the selected mode applies to, per
        // docs/OPEN_QUESTIONS.md §8, rather than silently discarding the selection.
        var draft = new ArchiveRotationSettingsDraft
        {
            ThresholdMode = ArchiveRotationThresholdMode.All,
        };

        Assert.True(draft.IsEmpty);
        Assert.Throws<ArgumentException>(() => draft.ToSettings());
    }

    [Fact]
    public void ToSettings_ConvertsMegabytesToExactBytes()
    {
        var draft = new ArchiveRotationSettingsDraft { MaxMegabytes = 512 };

        ArchiveRotationSettings? settings = draft.ToSettings();

        Assert.NotNull(settings);
        Assert.Equal(512L * 1024 * 1024, settings!.MaxBytes);
        Assert.Null(settings.MaxRecordCount);
        Assert.Null(settings.MaxCalendarDays);
        Assert.Null(settings.ThresholdMode);
    }

    [Fact]
    public void ToSettings_RejectsSeveralThresholdsWithoutAnExplicitMode()
    {
        var draft = new ArchiveRotationSettingsDraft
        {
            MaxCalendarDays = 30,
            MaxRecordCount = 10_000,
        };

        Assert.Throws<ArgumentException>(() => draft.ToSettings());
    }

    [Fact]
    public void ToSettings_AcceptsSeveralThresholdsWithAnExplicitMode()
    {
        var draft = new ArchiveRotationSettingsDraft
        {
            MaxCalendarDays = 30,
            MaxRecordCount = 10_000,
            ThresholdMode = ArchiveRotationThresholdMode.All,
        };

        ArchiveRotationSettings? settings = draft.ToSettings();

        Assert.NotNull(settings);
        Assert.Equal(ArchiveRotationThresholdMode.All, settings!.ThresholdMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ToSettings_RejectsNonPositiveThresholds(int calendarDays)
    {
        var draft = new ArchiveRotationSettingsDraft { MaxCalendarDays = calendarDays };

        Assert.Throws<ArgumentOutOfRangeException>(() => draft.ToSettings());
    }

    [Fact]
    public void ToSettings_RejectsASizeThresholdThatCannotBeExpressedInBytes()
    {
        var draft = new ArchiveRotationSettingsDraft
        {
            MaxMegabytes = (long.MaxValue / ArchiveRotationSettingsDraft.BytesPerMegabyte) + 1,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => draft.ToSettings());
    }

    [Fact]
    public void FromSettings_NullSettingsProduceAnEmptyDraft()
    {
        ArchiveRotationSettingsDraft draft = ArchiveRotationSettingsDraft.FromSettings(null);

        Assert.True(draft.IsEmpty);
        Assert.False(draft.HasSubMegabyteRemainder);
        Assert.Null(draft.ThresholdMode);
    }

    [Fact]
    public void FromSettings_RoundTripsEverySurfaceProducibleValue()
    {
        var original = new ArchiveRotationSettings
        {
            MaxRecordCount = 25_000,
            MaxBytes = 2048L * 1024 * 1024,
            MaxCalendarDays = 90,
            ThresholdMode = ArchiveRotationThresholdMode.Any,
        };

        ArchiveRotationSettingsDraft draft = ArchiveRotationSettingsDraft.FromSettings(original);

        Assert.False(draft.HasSubMegabyteRemainder);
        Assert.Equal(2048, draft.MaxMegabytes);
        Assert.Equal(original, draft.ToSettings());
    }

    [Fact]
    public void FromSettings_ReportsAByteThresholdThatIsNotAWholeMegabyte()
    {
        // Only a hand-edited settings file can produce this; saving the draft would change it.
        var original = new ArchiveRotationSettings
        {
            MaxBytes = (3L * 1024 * 1024) + 1,
        };

        ArchiveRotationSettingsDraft draft = ArchiveRotationSettingsDraft.FromSettings(original);

        Assert.True(draft.HasSubMegabyteRemainder);
        Assert.Equal(3, draft.MaxMegabytes);
    }

    [Fact]
    public void FromSettings_ClampsASubMegabyteThresholdToOneMegabyte()
    {
        var original = new ArchiveRotationSettings { MaxBytes = 1024 };

        ArchiveRotationSettingsDraft draft = ArchiveRotationSettingsDraft.FromSettings(original);

        Assert.True(draft.HasSubMegabyteRemainder);
        Assert.Equal(1, draft.MaxMegabytes);
        // The clamped draft is still valid, so the editor can present and save it.
        Assert.Equal(ArchiveRotationSettingsDraft.BytesPerMegabyte, draft.ToSettings()!.MaxBytes);
    }
}
