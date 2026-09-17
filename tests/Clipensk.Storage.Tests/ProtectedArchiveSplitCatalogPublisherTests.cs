using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveSplitCatalogPublisherTests
{
    private static readonly DateOnly CurrentCalendarDate = new(2026, 9, 17);

    public static TheoryData<ArchiveSplitCatalogCheckpoint> RecoveryCheckpoints => new()
    {
        ArchiveSplitCatalogCheckpoint.BeforeCatalogReplacement,
        ArchiveSplitCatalogCheckpoint.AfterCatalogReplacement,
        ArchiveSplitCatalogCheckpoint.AfterCatalogValidation,
        ArchiveSplitCatalogCheckpoint.BeforeCatalogPhaseCommit,
        ArchiveSplitCatalogCheckpoint.AfterCatalogPhaseCommit,
        ArchiveSplitCatalogCheckpoint.AfterBackupDelete,
        ArchiveSplitCatalogCheckpoint.AfterStagingDelete,
        ArchiveSplitCatalogCheckpoint.BeforeMarkerClear,
    };

    [Fact]
    public async Task PublishOrRecoverAsync_RebuildsBothCatalogProjectionsAndCompletesSplit()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePhysicalPublishedSplitAsync(environment);
        ExternalPayloadAddress stale = Address('f', 99);
        SeedCatalogAddress(environment, stale);

        var archiveCatalog = new ProtectedArchiveSegmentCatalog(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ArchiveSegmentDescriptor> staleSegments =
            await archiveCatalog.ReadAsync();
        Assert.Single(staleSegments);
        Assert.Equal(fixture.Operation.SourceCoverage, staleSegments[0].Coverage);
        Assert.Contains(stale, ReadExternalCatalog(environment));

        var service = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ArchiveSegmentDescriptor> completed =
            await service.PublishOrRecoverAsync(
                fixture.Operation.OperationId,
                CurrentCalendarDate);

        Assert.Equal(fixture.Operation.Segments.Count, completed.Count);
        for (int index = 0; index < completed.Count; index++)
        {
            PendingArchiveSplitSegment planned = fixture.Operation.Segments[index];
            ArchiveSegmentDescriptor actual = completed[index];
            Assert.Equal(planned.DatabaseId, actual.DatabaseId);
            Assert.Equal(planned.FileName.FileName, actual.FileName);
            Assert.Equal(planned.Coverage, actual.Coverage);
            Assert.True(actual.IsSealed);
        }

        Assert.Null(await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).ReadAsync());
        Assert.False(File.Exists(fixture.Publisher.GetBackupPath(fixture.Operation.OperationId)));
        Assert.False(Directory.Exists(
            fixture.Builder.GetStagingDirectory(fixture.Operation.OperationId)));

        Assert.Equal(
            completed.ToArray(),
            (await archiveCatalog.ValidateConsistencyAsync(CurrentCalendarDate)).ToArray());
        Assert.Equal([fixture.ExpectedExternalAddress], ReadExternalCatalog(environment));
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    [Theory]
    [MemberData(nameof(RecoveryCheckpoints))]
    public async Task PublishOrRecoverAsync_RollsForwardFromEveryCatalogBoundary(
        ArchiveSplitCatalogCheckpoint targetCheckpoint)
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePhysicalPublishedSplitAsync(environment);
        bool injected = false;
        var crashing = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (!injected && checkpoint == targetCheckpoint)
                {
                    injected = true;
                    throw new SimulatedCrashException(targetCheckpoint);
                }
            });

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            crashing.PublishOrRecoverAsync(
                fixture.Operation.OperationId,
                CurrentCalendarDate));
        Assert.True(injected);

        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        ArchiveSplitPhase expectedPhase = targetCheckpoint is
            ArchiveSplitCatalogCheckpoint.AfterCatalogPhaseCommit or
            ArchiveSplitCatalogCheckpoint.AfterBackupDelete or
            ArchiveSplitCatalogCheckpoint.AfterStagingDelete or
            ArchiveSplitCatalogCheckpoint.BeforeMarkerClear
            ? ArchiveSplitPhase.CatalogPublished
            : ArchiveSplitPhase.PhysicalPublished;
        Assert.Equal(expectedPhase, persisted.Phase);

        var recovering = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ArchiveSegmentDescriptor> completed =
            await recovering.PublishOrRecoverAsync(
                fixture.Operation.OperationId,
                CurrentCalendarDate);

        Assert.Equal(fixture.Operation.Segments.Count, completed.Count);
        Assert.Null(await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).ReadAsync());
        Assert.False(File.Exists(fixture.Publisher.GetBackupPath(fixture.Operation.OperationId)));
        Assert.False(Directory.Exists(
            fixture.Builder.GetStagingDirectory(fixture.Operation.OperationId)));
        Assert.Equal([fixture.ExpectedExternalAddress], ReadExternalCatalog(environment));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_MissingBackupBeforeCatalogPublishedFailsBeforeCatalogMutation()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePhysicalPublishedSplitAsync(environment);
        var archiveCatalog = new ProtectedArchiveSegmentCatalog(
            environment.Session,
            environment.Factory);
        ArchiveSegmentDescriptor[] archiveBefore =
            (await archiveCatalog.ReadAsync()).ToArray();
        ExternalPayloadAddress[] externalBefore = ReadExternalCatalog(environment);

        File.Delete(fixture.Publisher.GetBackupPath(fixture.Operation.OperationId));
        var service = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PublishOrRecoverAsync(
                fixture.Operation.OperationId,
                CurrentCalendarDate));

        Assert.Equal(archiveBefore, (await archiveCatalog.ReadAsync()).ToArray());
        Assert.Equal(externalBefore, ReadExternalCatalog(environment));
        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(ArchiveSplitPhase.PhysicalPublished, persisted.Phase);
    }

    [Fact]
    public async Task PublishOrRecoverAsync_CatalogPublishedArchiveMismatchFailsClosedWithoutCleanup()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreateCatalogPublishedCrashAsync(environment);
        PendingArchiveSplitSegment second = fixture.Operation.Segments[1];
        environment.Execute(
            $"DELETE FROM ArchiveSegmentIndex WHERE FileName = '{second.FileName.FileName}';",
            catalog: true);

        var service = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PublishOrRecoverAsync(
                fixture.Operation.OperationId,
                CurrentCalendarDate));

        Assert.True(File.Exists(fixture.Publisher.GetBackupPath(fixture.Operation.OperationId)));
        Assert.True(Directory.Exists(
            fixture.Builder.GetStagingDirectory(fixture.Operation.OperationId)));
        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(ArchiveSplitPhase.CatalogPublished, persisted.Phase);
    }

    [Fact]
    public async Task PublishOrRecoverAsync_CatalogPublishedExternalMismatchFailsClosedWithoutCleanup()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreateCatalogPublishedCrashAsync(environment);
        SeedCatalogAddress(environment, Address('e', 73));

        var service = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PublishOrRecoverAsync(
                fixture.Operation.OperationId,
                CurrentCalendarDate));

        Assert.True(File.Exists(fixture.Publisher.GetBackupPath(fixture.Operation.OperationId)));
        Assert.True(Directory.Exists(
            fixture.Builder.GetStagingDirectory(fixture.Operation.OperationId)));
        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(ArchiveSplitPhase.CatalogPublished, persisted.Phase);
    }

    [Fact]
    public async Task PublishOrRecoverAsync_LateCallerCancellationAfterCatalogCommitStillCompletes()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePhysicalPublishedSplitAsync(environment);
        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ArchiveSplitCatalogCheckpoint.AfterCatalogPhaseCommit)
                {
                    cancellation.Cancel();
                }
            });

        IReadOnlyList<ArchiveSegmentDescriptor> completed =
            await service.PublishOrRecoverAsync(
                fixture.Operation.OperationId,
                CurrentCalendarDate,
                cancellation.Token);

        Assert.Equal(fixture.Operation.Segments.Count, completed.Count);
        Assert.Null(await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).ReadAsync());
        Assert.False(Directory.Exists(
            fixture.Builder.GetStagingDirectory(fixture.Operation.OperationId)));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_HoldsMutationLeaseAcrossCatalogPublication()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePhysicalPublishedSplitAsync(environment);
        using var entered = new ManualResetEventSlim();
        using var allowContinue = new ManualResetEventSlim();
        var service = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ArchiveSplitCatalogCheckpoint.AfterCatalogReplacement)
                {
                    entered.Set();
                    allowContinue.Wait(TimeSpan.FromSeconds(10));
                }
            });

        Task<IReadOnlyList<ArchiveSegmentDescriptor>> publication =
            service.PublishOrRecoverAsync(
                fixture.Operation.OperationId,
                CurrentCalendarDate);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        using var competingCancellation =
            new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using ProtectedStorageMutationLease competing =
                await environment.Session.AcquireMutationLeaseAsync(
                    competingCancellation.Token);
        });

        allowContinue.Set();
        _ = await publication.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<SplitFixture> CreateCatalogPublishedCrashAsync(
        GlobalPolicyTestEnvironment environment)
    {
        SplitFixture fixture = await CreatePhysicalPublishedSplitAsync(environment);
        bool injected = false;
        var service = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (!injected && checkpoint == ArchiveSplitCatalogCheckpoint.AfterCatalogPhaseCommit)
                {
                    injected = true;
                    throw new SimulatedCrashException(checkpoint);
                }
            });
        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            service.PublishOrRecoverAsync(
                fixture.Operation.OperationId,
                CurrentCalendarDate));
        Assert.True(injected);
        return fixture;
    }

    private static async Task<SplitFixture> CreatePhysicalPublishedSplitAsync(
        GlobalPolicyTestEnvironment environment)
    {
        var sourceFileName = new ArchiveFileName(
            70,
            ArchiveFileName.NoSplit);
        var coverage = new JournalDateRange(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 31));
        var archiveService = new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory);
        DatabaseIdentity sourceIdentity =
            await archiveService.CreateAsync(sourceFileName, coverage);
        string sourcePath = Path.Combine(
            environment.Root,
            "Archive",
            sourceFileName.FileName);
        ExternalPayloadAddress expectedExternalAddress = Address('a', 2048);
        SeedSourceArchive(environment, sourcePath, expectedExternalAddress);

        _ = await new ProtectedExternalPayloadCatalogRebuildService(
            environment.Session,
            environment.Factory).RebuildAsync();
        _ = await new ProtectedArchiveCatalogMaintenanceService(
            environment.Session,
            environment.Factory).RebuildAsync(CurrentCalendarDate);

        JournalDateRange[] requestedRanges =
        [
            new(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15)),
            new(new DateOnly(2026, 8, 16), new DateOnly(2026, 8, 23)),
            new(new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 31)),
        ];
        IReadOnlyList<PendingArchiveSplitSegment> segments =
            new ArchiveSplitPlanner().Build(
                sourceFileName,
                sourceIdentity.DatabaseId,
                coverage,
                requestedRanges,
                [sourceFileName],
                IdFactory(Id(601), Id(602)));
        PendingArchiveSplitOperation operation =
            await new SqlitePendingArchiveSplitRepository(
                environment.Session,
                environment.Factory).StartAsync(
                    sourceFileName,
                    sourceIdentity.DatabaseId,
                    coverage,
                    segments);

        var builder = new ProtectedArchiveSplitShadowBuilder(
            environment.Session,
            environment.Factory);
        PendingArchiveSplitOperation ready =
            await builder.BuildAsync(operation.OperationId);
        var publisher = new ProtectedArchiveSplitPublisher(
            environment.Session,
            environment.Factory);
        PendingArchiveSplitOperation physical =
            await publisher.PublishOrRecoverAsync(ready.OperationId);
        Assert.Equal(ArchiveSplitPhase.PhysicalPublished, physical.Phase);

        return new SplitFixture(
            physical,
            builder,
            publisher,
            expectedExternalAddress);
    }

    private static void SeedSourceArchive(
        GlobalPolicyTestEnvironment environment,
        string sourcePath,
        ExternalPayloadAddress externalAddress)
    {
        string app = Id(201).ToString("D");
        string event1 = Id(301).ToString("D");
        string event2 = Id(302).ToString("D");
        string event3 = Id(303).ToString("D");

        Execute(
            environment,
            sourcePath,
            $"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{app}', '2026-08-01T00:00:00.0000000+00:00');

            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath,
                SourceApplicationUserModelId)
            VALUES
                ('{event1}', '2026-08-15T12:00:00.0000000+00:00', 180,
                 'Turkey Standard Time', '2026-08-15',
                 '{app}', 101, 'C:\\Apps\\Alpha.exe', NULL),
                ('{event2}', '2026-08-16T12:00:00.0000000+00:00', 180,
                 'Turkey Standard Time', '2026-08-16',
                 '{app}', 101, 'C:\\Apps\\Alpha.exe', NULL),
                ('{event3}', '2026-08-25T12:00:00.0000000+00:00', 180,
                 'Turkey Standard Time', '2026-08-25',
                 '{app}', 101, 'C:\\Apps\\Alpha.exe', NULL);

            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256,
                ExternalRelativePath, ExternalSizeBytes)
            VALUES
                ('{event1}', 0, 'Text', 'Text', 5,
                 'first', 'first', NULL, NULL, NULL),
                ('{event2}', 0, 'PNG', 'PngImage', {externalAddress.SizeBytes},
                 NULL, NULL, '{externalAddress.Sha256}',
                 '{externalAddress.RelativePath}', {externalAddress.SizeBytes}),
                ('{event3}', 0, 'Text', 'Text', 5,
                 'third', 'third', NULL, NULL, NULL);
            """);
    }

    private static ExternalPayloadAddress Address(char shaCharacter, long size)
    {
        string sha = new(shaCharacter, 64);
        return new ExternalPayloadAddress(
            sha,
            $"2026-08-16/{sha}.png",
            size);
    }

    private static void SeedCatalogAddress(
        GlobalPolicyTestEnvironment environment,
        ExternalPayloadAddress address)
    {
        environment.Execute(
            $"""
            INSERT INTO ExternalPayloadAddressIndex (Sha256, RelativePath, SizeBytes)
            VALUES ('{address.Sha256}', '{address.RelativePath}', {address.SizeBytes});
            """,
            catalog: true);
    }

    private static ExternalPayloadAddress[] ReadExternalCatalog(
        GlobalPolicyTestEnvironment environment)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CatalogPath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sha256, RelativePath, SizeBytes
            FROM ExternalPayloadAddressIndex
            ORDER BY Sha256 COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<ExternalPayloadAddress>();
        while (reader.Read())
        {
            result.Add(new ExternalPayloadAddress(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }
        return result.ToArray();
    }

    private static void Execute(
        GlobalPolicyTestEnvironment environment,
        string databasePath,
        string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            databasePath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static Func<Guid> IdFactory(params Guid[] ids)
    {
        int index = 0;
        return () => index < ids.Length
            ? ids[index++]
            : throw new InvalidOperationException("Test DatabaseId factory exhausted.");
    }

    private static Guid Id(int value) =>
        new(value, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    private sealed record SplitFixture(
        PendingArchiveSplitOperation Operation,
        ProtectedArchiveSplitShadowBuilder Builder,
        ProtectedArchiveSplitPublisher Publisher,
        ExternalPayloadAddress ExpectedExternalAddress);

    private sealed class SimulatedCrashException(
        ArchiveSplitCatalogCheckpoint checkpoint)
        : Exception($"Simulated crash at {checkpoint}.");
}
