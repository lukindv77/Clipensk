using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveDatabaseServiceTests
{
    [Fact]
    public async Task CreateAsync_CreatesSelfDescribingUnsplitArchive()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(25, ArchiveFileName.NoSplit);
        var coverage = Range(2026, 8, 1, 2026, 8, 31);

        DatabaseIdentity identity = await service.CreateAsync(fileName, coverage);

        Assert.Equal(environment.StorageId, identity.StorageId);
        Assert.NotEqual(Guid.Empty, identity.DatabaseId);
        Assert.Equal(DatabaseRole.Archive, identity.Role);
        Assert.Equal(ProtectedArchiveDatabaseService.ArchiveSchemaVersion, identity.SchemaVersion);
        Assert.Equal(ProtectedStorageDatabaseService.CurrentEncryptionVersion, identity.EncryptionVersion);
        Assert.Equal(25, identity.ArchiveBaseNumber);
        Assert.Null(identity.ArchiveSplitSequence);
        Assert.Equal(coverage.StartDate, identity.CoverageStartDate);
        Assert.Equal(coverage.EndDate, identity.CoverageEndDate);
        Assert.True(File.Exists(ArchivePath(environment, fileName)));
        Assert.Equal(
            new[] { SqliteOpenMode.ReadWriteCreate, SqliteOpenMode.ReadOnly },
            environment.Factory.Modes);

        using SqliteConnection connection = environment.Factory.Open(
            ArchivePath(environment, fileName),
            environment.Key,
            SqliteOpenMode.ReadOnly);
        Assert.Equal(1, Scalar(connection, "PRAGMA user_version;"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM DatabaseIdentity;"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ApplicationIdentity';"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ClipboardHistoryEvent';"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ClipboardHistoryPayload';"));
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='GlobalCapturePolicy';"));
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='CustomBinaryFormatConfiguration';"));
    }

    [Fact]
    public async Task CreateAsync_PersistsPositiveSplitSequence()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(25, 12);
        var coverage = Range(2026, 9, 1, 2026, 9, 7);

        DatabaseIdentity identity = await service.CreateAsync(fileName, coverage);

        Assert.Equal(25, identity.ArchiveBaseNumber);
        Assert.Equal(12, identity.ArchiveSplitSequence);
        Assert.True(File.Exists(ArchivePath(environment, fileName)));
    }

    [Fact]
    public async Task ValidateAsync_UsesReadOnlyAndReturnsPersistedIdentity()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(7, ArchiveFileName.NoSplit);
        DatabaseIdentity created = await service.CreateAsync(
            fileName,
            Range(2026, 7, 1, 2026, 7, 31));
        environment.Factory.Modes.Clear();

        DatabaseIdentity validated = await service.ValidateAsync(fileName);

        Assert.Equal(created, validated);
        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
    }

    [Fact]
    public async Task ValidateAsync_RejectsFileNameIdentityMismatch()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var original = new ArchiveFileName(25, ArchiveFileName.NoSplit);
        var renamed = new ArchiveFileName(26, ArchiveFileName.NoSplit);
        await service.CreateAsync(original, Range(2026, 6, 1, 2026, 6, 30));
        File.Copy(ArchivePath(environment, original), ArchivePath(environment, renamed));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateAsync(renamed));
    }

    [Fact]
    public async Task ValidateAsync_RejectsStorageIdentityMismatch()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(2, ArchiveFileName.NoSplit);
        await service.CreateAsync(fileName, Range(2026, 8, 1, 2026, 8, 31));
        ExecuteArchive(
            environment,
            fileName,
            $"UPDATE DatabaseIdentity SET StorageId = '{Guid.NewGuid():D}';");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateAsync(fileName));
    }

    [Fact]
    public async Task ValidateAsync_RejectsDatabaseIdentityShapeCorruption()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(6, ArchiveFileName.NoSplit);
        await service.CreateAsync(fileName, Range(2026, 8, 1, 2026, 8, 31));

        ExecuteArchive(environment, fileName, """
            ALTER TABLE DatabaseIdentity RENAME TO DatabaseIdentityOld;
            CREATE TABLE DatabaseIdentity (
                SingletonId INTEGER NOT NULL PRIMARY KEY,
                StorageId TEXT NOT NULL,
                DatabaseId TEXT NOT NULL,
                DatabaseRole TEXT NOT NULL,
                SchemaVersion INTEGER NOT NULL,
                EncryptionVersion INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                ArchiveBaseNumber TEXT NULL,
                ArchiveSplitSequence INTEGER NULL,
                CoverageStartDate TEXT NULL,
                CoverageEndDate TEXT NULL
            );
            INSERT INTO DatabaseIdentity SELECT * FROM DatabaseIdentityOld;
            DROP TABLE DatabaseIdentityOld;
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateAsync(fileName));
    }

    [Fact]
    public async Task ValidateAsync_RejectsHistoryOutsideAssignedCoverage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(3, ArchiveFileName.NoSplit);
        await service.CreateAsync(fileName, Range(2026, 8, 1, 2026, 8, 31));

        ExecuteArchive(environment, fileName, """
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '11111111-1111-1111-1111-111111111111',
                '2026-09-01T12:00:00.0000000+00:00',
                0,
                'UTC',
                '2026-09-01',
                NULL, NULL, NULL, NULL);
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateAsync(fileName));
    }

    [Fact]
    public async Task ValidateAsync_RejectsBrokenHistoryForeignKey()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(4, ArchiveFileName.NoSplit);
        await service.CreateAsync(fileName, Range(2026, 8, 1, 2026, 8, 31));

        ExecuteArchive(environment, fileName, """
            PRAGMA foreign_keys = OFF;
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '22222222-2222-2222-2222-222222222222',
                '2026-08-12T12:00:00.0000000+00:00',
                0,
                'UTC',
                '2026-08-12',
                '33333333-3333-3333-3333-333333333333',
                NULL, NULL, NULL);
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateAsync(fileName));
    }

    [Fact]
    public async Task ValidateAsync_RejectsHistorySchemaCorruption()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(5, ArchiveFileName.NoSplit);
        await service.CreateAsync(fileName, Range(2026, 8, 1, 2026, 8, 31));

        ExecuteArchive(
            environment,
            fileName,
            "DROP INDEX IX_ClipboardHistoryPayload_FormatName;");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ValidateAsync(fileName));
    }

    [Fact]
    public async Task CreateAsync_CallerCancellationDuringOpenLeavesNoFinalOrStagingFile()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(8, ArchiveFileName.NoSplit);
        using var cancellation = new CancellationTokenSource();
        environment.Factory.OnOpen = (_, mode) =>
        {
            if (mode == SqliteOpenMode.ReadWriteCreate)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateAsync(
                fileName,
                Range(2026, 8, 1, 2026, 8, 31),
                cancellation.Token));

        Assert.False(File.Exists(ArchivePath(environment, fileName)));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(environment.Root, "Archive"),
            ".clipensk-archive-create-*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ValidateAsync_CallerCancellationBeforeOpenDoesNotOpenDatabase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(9, ArchiveFileName.NoSplit);
        await service.CreateAsync(fileName, Range(2026, 8, 1, 2026, 8, 31));
        environment.Factory.Modes.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ValidateAsync(fileName, cancellation.Token));

        Assert.Empty(environment.Factory.Modes);
    }

    [Fact]
    public async Task ValidateAsync_SessionRevocationBeforeOpenDoesNotOpenDatabase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(10, ArchiveFileName.NoSplit);
        await service.CreateAsync(fileName, Range(2026, 8, 1, 2026, 8, 31));
        environment.Factory.Modes.Clear();
        Assert.True(environment.Lifecycle.TryBeginLock());
        environment.Lifecycle.CompleteLock();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ValidateAsync(fileName));

        Assert.Empty(environment.Factory.Modes);
    }

    [Fact]
    public async Task CreateAsync_RejectsExistingArchiveWithoutOverwrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(11, ArchiveFileName.NoSplit);
        var coverage = Range(2026, 8, 1, 2026, 8, 31);
        DatabaseIdentity first = await service.CreateAsync(fileName, coverage);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(fileName, coverage));

        DatabaseIdentity after = await service.ValidateAsync(fileName);
        Assert.Equal(first, after);
    }

    [Fact]
    public async Task CreateAsync_RejectsNonCanonicalArchiveFileNameValueBeforeSchedulingWork()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateAsync(default, Range(2026, 8, 1, 2026, 8, 31)));
    }

    private static JournalDateRange Range(
        int startYear,
        int startMonth,
        int startDay,
        int endYear,
        int endMonth,
        int endDay) =>
        new(
            new DateOnly(startYear, startMonth, startDay),
            new DateOnly(endYear, endMonth, endDay));

    private static string ArchivePath(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName fileName) =>
        Path.Combine(environment.Root, "Archive", fileName.FileName);

    private static void ExecuteArchive(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName fileName,
        string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            ArchivePath(environment, fileName),
            environment.Key,
            SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
