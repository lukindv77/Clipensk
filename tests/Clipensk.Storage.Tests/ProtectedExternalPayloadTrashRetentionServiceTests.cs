using System.Globalization;
using Clipensk.Storage.ExternalFiles;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedExternalPayloadTrashRetentionServiceTests
{
    private static readonly DateOnly Today = new(2026, 3, 31);
    private const int RetentionDays = 30;

    [Fact]
    public async Task CollectAsync_IsNoOpWhenTrashDoesNotExist()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();

        ExternalPayloadTrashRetentionResult result =
            await Service(environment).CollectAsync(Today, RetentionDays);

        Assert.Equal(0, result.DeletedDateDirectoryCount);
        Assert.Equal(0, result.DeletedFileCount);
        Assert.Empty(result.SkippedEntryNames);
    }

    [Fact]
    public async Task CollectAsync_DeletesAnExpiredDeletionDateWithItsWholeSubtree()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedTrashedPayload(environment, new DateOnly(2026, 3, 1), "2026-02-20", "a.png");
        SeedTrashedPayload(environment, new DateOnly(2026, 3, 1), "2026-02-21", "b.png");

        ExternalPayloadTrashRetentionResult result =
            await Service(environment).CollectAsync(Today, RetentionDays);

        Assert.Equal(1, result.DeletedDateDirectoryCount);
        Assert.Equal(2, result.DeletedFileCount);
        Assert.Empty(result.SkippedEntryNames);
        Assert.False(Directory.Exists(TrashDate(environment, new DateOnly(2026, 3, 1))));
    }

    [Theory]
    // Retention keeps a payload for the full retention span: deleted on D + retentionDays, not before.
    [InlineData("2026-03-01", true)]
    [InlineData("2026-03-02", false)]
    [InlineData("2026-03-31", false)]
    public async Task CollectAsync_DeletesOnlyAfterTheFullRetentionSpan(
        string deletionDateText,
        bool expectedDeleted)
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly deletionDate = DateOnly.ParseExact(deletionDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        SeedTrashedPayload(environment, deletionDate, "2026-02-20", "a.png");

        ExternalPayloadTrashRetentionResult result =
            await Service(environment).CollectAsync(Today, RetentionDays);

        Assert.Equal(expectedDeleted ? 1 : 0, result.DeletedDateDirectoryCount);
        Assert.Equal(!expectedDeleted, Directory.Exists(TrashDate(environment, deletionDate)));
    }

    [Fact]
    public async Task CollectAsync_SkipsAFutureDeletionDateInsteadOfDeletingIt()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var future = new DateOnly(2026, 4, 10);
        SeedTrashedPayload(environment, future, "2026-02-20", "a.png");

        ExternalPayloadTrashRetentionResult result =
            await Service(environment).CollectAsync(Today, RetentionDays);

        Assert.Equal(0, result.DeletedDateDirectoryCount);
        Assert.Equal("2026-04-10", Assert.Single(result.SkippedEntryNames));
        Assert.True(Directory.Exists(TrashDate(environment, future)));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-3-1")]
    [InlineData("2026-03-01T00")]
    public async Task CollectAsync_NeverDeletesADirectoryItCannotDate(string directoryName)
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        string directory = Path.Combine(environment.Root, "Trash", directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "keep.txt"), "keep");

        ExternalPayloadTrashRetentionResult result =
            await Service(environment).CollectAsync(Today, RetentionDays);

        Assert.Equal(0, result.DeletedDateDirectoryCount);
        Assert.Equal(directoryName, Assert.Single(result.SkippedEntryNames));
        Assert.True(File.Exists(Path.Combine(directory, "keep.txt")));
    }

    [Fact]
    public async Task CollectAsync_NeverDeletesALooseFileInTheTrashRoot()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Directory.CreateDirectory(Path.Combine(environment.Root, "Trash"));
        string loose = Path.Combine(environment.Root, "Trash", "2026-03-01");
        File.WriteAllText(loose, "not a directory");

        ExternalPayloadTrashRetentionResult result =
            await Service(environment).CollectAsync(Today, RetentionDays);

        Assert.Equal(0, result.DeletedDateDirectoryCount);
        Assert.Equal("2026-03-01", Assert.Single(result.SkippedEntryNames));
        Assert.True(File.Exists(loose));
    }

    [Fact]
    public async Task CollectAsync_FailsClosedAndDeletesNothingWhenTheSubtreeContainsALink()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var deletionDate = new DateOnly(2026, 3, 1);
        SeedTrashedPayload(environment, deletionDate, "2026-02-20", "a.png");

        string outside = Path.Combine(environment.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "precious");
        string linkPath = Path.Combine(TrashDate(environment, deletionDate), "linked");
        if (!TryCreateDirectorySymlink(linkPath, outside))
        {
            return;
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Service(environment).CollectAsync(Today, RetentionDays));

        // Nothing inside the rejected tree was touched, and the link target is intact.
        Assert.True(Directory.Exists(TrashDate(environment, deletionDate)));
        Assert.True(File.Exists(Path.Combine(
            TrashDate(environment, deletionDate),
            "2026-02-20",
            "a.png")));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CollectAsync_RejectsANonPositiveRetention(int retentionDays)
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedTrashedPayload(environment, new DateOnly(2026, 3, 1), "2026-02-20", "a.png");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Service(environment).CollectAsync(Today, retentionDays));
        Assert.True(Directory.Exists(TrashDate(environment, new DateOnly(2026, 3, 1))));
    }

    [Fact]
    public async Task CollectAsync_LeavesUnexpiredDatesUntouchedWhileDeletingExpiredOnes()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedTrashedPayload(environment, new DateOnly(2026, 2, 1), "2026-01-10", "old.png");
        SeedTrashedPayload(environment, new DateOnly(2026, 3, 1), "2026-02-20", "edge.png");
        SeedTrashedPayload(environment, new DateOnly(2026, 3, 20), "2026-03-10", "fresh.png");

        ExternalPayloadTrashRetentionResult result =
            await Service(environment).CollectAsync(Today, RetentionDays);

        Assert.Equal(2, result.DeletedDateDirectoryCount);
        Assert.Equal(2, result.DeletedFileCount);
        Assert.False(Directory.Exists(TrashDate(environment, new DateOnly(2026, 2, 1))));
        Assert.False(Directory.Exists(TrashDate(environment, new DateOnly(2026, 3, 1))));
        Assert.True(Directory.Exists(TrashDate(environment, new DateOnly(2026, 3, 20))));
    }

    private static ProtectedExternalPayloadTrashRetentionService Service(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session);

    private static string TrashDate(GlobalPolicyTestEnvironment environment, DateOnly deletionDate) =>
        Path.Combine(
            environment.Root,
            "Trash",
            deletionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static void SeedTrashedPayload(
        GlobalPolicyTestEnvironment environment,
        DateOnly deletionDate,
        string firstStoredDate,
        string fileName)
    {
        string directory = Path.Combine(TrashDate(environment, deletionDate), firstStoredDate);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), [1, 2, 3]);
    }

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
