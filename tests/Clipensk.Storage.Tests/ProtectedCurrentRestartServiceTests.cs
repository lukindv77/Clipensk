using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

/// <summary>"Start the current database anew" after current.db was lost (docs/CURRENT_RESTART.md).</summary>
public sealed class ProtectedCurrentRestartServiceTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task ALostCurrent_GetsANewEmptyOne_OfTheSameStorage_AndTheCatalogIsReplaced()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 27));
        Guid lostDatabaseId = ReadDatabaseId(environment, environment.CurrentPath);
        environment.Session.Dispose();
        File.Delete(environment.CurrentPath);

        ProtectedCurrentRestartResult result = await Service(environment)
            .RestartAsync(environment.Root, environment.StorageId, environment.Key, Today);

        Assert.True(result.IsSuccess);
        Assert.True(result.CurrentCreated);
        Assert.NotNull(result.CatalogQuarantineRelativePath);
        Assert.True(File.Exists(Path.Combine(environment.Root, result.CatalogQuarantineRelativePath!)));
        Assert.NotEqual(lostDatabaseId, ReadDatabaseId(environment, environment.CurrentPath));
        Assert.Equal(environment.StorageId, ReadStorageId(environment, environment.CurrentPath));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(environment.Root, "Current"), ".clipensk-*"));
        Assert.True((await environment.Service.InitializeOrValidateAsync(
            environment.Root, environment.StorageId, environment.Key, allowInitialize: false)).IsSuccess);
    }

    [Fact]
    public async Task OnlyAnArchiveLeft_IsEnough_AndTheCatalogIsRebuiltFromIt()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20));
        SeedDay(environment, new DateOnly(2026, 2, 21));
        ProtectedStorageStartupResult rotation = await new ProtectedStorageStartupRecoveryCoordinator(
                environment.Session,
                environment.Factory)
            .RunAsync(Today, new ArchiveRotationSettings { MaxCalendarDays = 1 }, trashRetentionDays: null);
        Assert.True(rotation.StartedRotation!.Started);
        environment.Session.Dispose();
        Directory.Delete(Path.Combine(environment.Root, "Current"), recursive: true);

        ProtectedCurrentRestartResult result = await Service(environment)
            .RestartAsync(environment.Root, environment.StorageId, environment.Key, Today);

        Assert.True(result.IsSuccess);
        Assert.Null(result.CatalogQuarantineRelativePath);
        Assert.True(File.Exists(environment.CatalogPath));
        Assert.Equal(
            rotation.StartedRotation.Targets.Count,
            environment.Scalar("SELECT COUNT(*) FROM ArchiveSegmentIndex;", catalog: true));
    }

    [Fact]
    public async Task AnExistingCurrent_IsNeverReplaced()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Session.Dispose();
        byte[] before = SHA256.HashData(File.ReadAllBytes(environment.CurrentPath));

        ProtectedCurrentRestartResult result = await Service(environment)
            .RestartAsync(environment.Root, environment.StorageId, environment.Key, Today);

        Assert.Equal(ProtectedStorageDatabaseStatus.MissingOrPartialStorage, result.Status);
        Assert.False(result.CurrentCreated);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(environment.CurrentPath)));
    }

    [Fact]
    public async Task AnEmptyDataRoot_IsNotAStorageToRestart()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Session.Dispose();
        Directory.Delete(Path.Combine(environment.Root, "Current"), recursive: true);

        ProtectedCurrentRestartResult result = await Service(environment)
            .RestartAsync(environment.Root, environment.StorageId, environment.Key, Today);

        Assert.Equal(ProtectedStorageDatabaseStatus.MissingOrPartialStorage, result.Status);
        Assert.False(Directory.Exists(Path.Combine(environment.Root, "Current")));
    }

    [Fact]
    public async Task AnotherStorageId_CreatesNothing()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Session.Dispose();
        File.Delete(environment.CurrentPath);

        ProtectedCurrentRestartResult result = await Service(environment)
            .RestartAsync(environment.Root, Guid.NewGuid(), environment.Key, Today);

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.False(result.CurrentCreated);
        Assert.False(File.Exists(environment.CurrentPath));
        Assert.Equal(
            ["storage-catalog.db"],
            Directory.EnumerateFileSystemEntries(Path.Combine(environment.Root, "Current")).Select(Path.GetFileName));
    }

    private static ProtectedCurrentRestartService Service(GlobalPolicyTestEnvironment environment) =>
        new(environment.Factory);

    private static Guid ReadDatabaseId(GlobalPolicyTestEnvironment environment, string path) =>
        ReadIdentityGuid(environment, path, "DatabaseId");

    private static Guid ReadStorageId(GlobalPolicyTestEnvironment environment, string path) =>
        ReadIdentityGuid(environment, path, "StorageId");

    private static Guid ReadIdentityGuid(GlobalPolicyTestEnvironment environment, string path, string column)
    {
        using SqliteConnection connection = environment.Factory.Open(path, environment.Key, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM DatabaseIdentity WHERE SingletonId = 1;";
        return Guid.Parse((string)command.ExecuteScalar()!);
    }

    private static void SeedDay(GlobalPolicyTestEnvironment environment, DateOnly day)
    {
        string eventId = Guid.NewGuid().ToString("D");
        environment.Execute($"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES ('{eventId}', '{day:yyyy-MM-dd}T09:00:00.0000000+00:00', 0, 'UTC',
                    '{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}', NULL, NULL, NULL, NULL);
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
            VALUES ('{eventId}', 0, 'Text', 'Text', 4, 'text', 'text', NULL, NULL, NULL);
            """);
    }
}
