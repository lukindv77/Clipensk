using Clipensk.Core.Settings;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ArchiveRotationThresholdRuleTests
{
    [Fact]
    public void SingleThreshold_ReachesOnEqualityAndBeyond()
    {
        var byRecords = new ArchiveRotationSettings { MaxRecordCount = 10 };
        Assert.False(byRecords.HasReachedThresholds(1, 9, null));
        Assert.True(byRecords.HasReachedThresholds(1, 10, null));
        Assert.True(byRecords.HasReachedThresholds(1, 11, null));

        var byDays = new ArchiveRotationSettings { MaxCalendarDays = 3 };
        Assert.False(byDays.HasReachedThresholds(2, 0, null));
        Assert.True(byDays.HasReachedThresholds(3, 0, null));
    }

    [Fact]
    public void AnyAndAll_CombineEnabledThresholdsExactly()
    {
        var any = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
            MaxCalendarDays = 5,
            ThresholdMode = ArchiveRotationThresholdMode.Any,
        };
        var all = any with { ThresholdMode = ArchiveRotationThresholdMode.All };

        Assert.True(any.HasReachedThresholds(1, 10, null));
        Assert.False(all.HasReachedThresholds(1, 10, null));
        Assert.True(all.HasReachedThresholds(5, 10, null));
    }

    [Fact]
    public void PhysicalSize_ParticipatesOnlyWithAMeasuredFileSize()
    {
        var bySize = new ArchiveRotationSettings { MaxBytes = 4096 };

        Assert.False(bySize.HasReachedThresholds(1, 0, 4095));
        Assert.True(bySize.HasReachedThresholds(1, 0, 4096));

        // A configuration that includes MaxBytes can never be decided without measuring the closed
        // Archive file, so omitting the size is fail-closed rather than silently ignored.
        Assert.Throws<ArgumentNullException>(() => bySize.HasReachedThresholds(1, 0, null));
    }

    [Fact]
    public void AllMode_WaitsForEveryEnabledThresholdIncludingSize()
    {
        var all = new ArchiveRotationSettings
        {
            MaxRecordCount = 5,
            MaxBytes = 8192,
            ThresholdMode = ArchiveRotationThresholdMode.All,
        };

        Assert.False(all.HasReachedThresholds(1, 5, 8191));
        Assert.False(all.HasReachedThresholds(1, 4, 8192));
        Assert.True(all.HasReachedThresholds(1, 5, 8192));
    }

    [Fact]
    public void RejectsImpossibleSegmentMetrics()
    {
        var settings = new ArchiveRotationSettings { MaxRecordCount = 1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.HasReachedThresholds(0, 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.HasReachedThresholds(1, -1, null));
    }
}
