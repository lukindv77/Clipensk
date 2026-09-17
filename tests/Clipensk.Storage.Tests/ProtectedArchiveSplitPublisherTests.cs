using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveSplitPublisherTests
{
    public static TheoryData<ArchiveSplitPublicationCheckpoint> RecoveryCheckpoints => new()
    {
        ArchiveSplitPublicationCheckpoint.BeforeAdditionalMove,
        ArchiveSplitPublicationCheckpoint.AfterAdditionalMove,
        ArchiveSplitPublicationCheckpoint.AfterAdditionalValidation,
        ArchiveSplitPublicationCheckpoint.BeforeSourceReplace,
        ArchiveSplitPublicationCheckpoint.AfterSourceReplace,
        ArchiveSplitPublicationCheckpoint.AfterSourceValidation,
        ArchiveSplitPublicationCheckpoint.BeforePhysicalPhaseCommit,
        ArchiveSplitPublicationCheckpoint.AfterPhysicalPhaseCommit,
    };

    [Theory]
    [MemberData(nameof(RecoveryCheckpoints))]
    public async Task PublishOrRecoverAsync_RollsForwardFromEveryPublicationBoundary(
        ArchiveSplitPublicationCheckpoint targetCheckpoint)
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreateReadySplitAsync(environment);
        bool injected = false;
        var crashingPublisher = new ProtectedArchiveSplitPublisher(
            environment.Session,
            environment.Factory,
            (checkpoint, _) =>
            {
                if (!injected && checkpoint == targetCheckpoint)
                {
                    injected = true;
                    throw new SimulatedCrashException(targetCheckpoint);
                }
            });

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            crashingPublisher.PublishOrRecoverAsync(fixture.Operation.OperationId));
        Assert.True(injected);

        var recoveringPublisher = new ProtectedArchiveSplitPublisher(
            environment.Session,
            environment.Factory);
        PendingArchiveSplitOperation recovered =
            await recoveringPublisher.PublishOrRecoverAsync(
                fixture.Operation.OperationId);

        Assert.Equal(ArchiveSplitPhase.PhysicalPublished, recovered.Phase);
        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(ArchiveSplitPhase.PhysicalPublished, persisted.Phase);
        Assert.Equal(fixture.Operation.OperationId, persisted.OperationId);

        Assert.True(File.Exists(
            recoveringPublisher.GetBackupPath(fixture.Operation.OperationId)));
        Assert.Equal(
            3,
            Scalar(
                environment,
                recoveringPublisher.GetBackupPath(fixture.Operation.OperationId),
                "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));

        foreach (PendingArchiveSplitSegment segment in recovered.Segments)
        {
            string finalPath = Path.Combine(
                environment.Root,
                "Archive",
                segment.FileName.FileName);
            Assert.True(File.Exists(finalPath));
            Assert.Equal(
                segment.DatabaseId.ToString("D"),
                TextScalar(
                    environment,
                    finalPath,
                    "SELECT DatabaseId FROM DatabaseIdentity WHERE SingletonId = 1;"));
            Assert.Equal(
                segment.Coverage.StartDate.ToString("yyyy-MM-dd"),
                TextScalar(
                    environment,
                    finalPath,
                    "SELECT CoverageStartDate FROM DatabaseIdentity WHERE SingletonId = 1;"));

            Assert.False(File.Exists(Path.Combine(
                fixture.Builder.GetStagingDirectory(recovered.OperationId),
                segment.FileName.FileName)));
        }

        Assert.Equal(
            1,
            Scalar(environment, fixture.SourcePath, """
                SELECT COUNT(*) FROM ClipboardHistoryEvent
                WHERE CalendarDate = '2026-08-15';
                """));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_RejectsUnexpectedReservedFinalCollision()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreateReadySplitAsync(environment);
        PendingArchiveSplitSegment second = fixture.Operation.Segments[1];
        string unexpectedFinal = Path.Combine(
            environment.Root,
            "Archive",
            second.FileName.FileName);
        File.WriteAllText(unexpectedFinal, "unexpected collision");

        var publisher = new ProtectedArchiveSplitPublisher(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishOrRecoverAsync(fixture.Operation.OperationId));

        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(ArchiveSplitPhase.ReadyToPublish, persisted.Phase);
        Assert.Equal(
            3,
            Scalar(
                environment,
                fixture.SourcePath,
                "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_ReplacementWithoutBackupFailsClosed()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreateReadySplitAsync(environment);
        bool injected = false;
        var crashingPublisher = new ProtectedArchiveSplitPublisher(
            environment.Session,
            environment.Factory,
            (checkpoint, _) =>
            {
                if (!injected &&
                    checkpoint == ArchiveSplitPublicationCheckpoint.AfterSourceReplace)
                {
                    injected = true;
                    throw new SimulatedCrashException(checkpoint);
                }
            });

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            crashingPublisher.PublishOrRecoverAsync(fixture.Operation.OperationId));
        Assert.True(injected);

        string backupPath = crashingPublisher.GetBackupPath(
            fixture.Operation.OperationId);
        Assert.True(File.Exists(backupPath));
        File.Delete(backupPath);

        var recoveringPublisher = new ProtectedArchiveSplitPublisher(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            recoveringPublisher.PublishOrRecoverAsync(fixture.Operation.OperationId));

        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(ArchiveSplitPhase.ReadyToPublish, persisted.Phase);
    }

    [Fact]
    public async Task PublishOrRecoverAsync_PhysicalPublishedIsIdempotentAndKeepsBackup()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreateReadySplitAsync(environment);
        var publisher = new ProtectedArchiveSplitPublisher(
            environment.Session,
            environment.Factory);

        PendingArchiveSplitOperation first =
            await publisher.PublishOrRecoverAsync(fixture.Operation.OperationId);
        PendingArchiveSplitOperation second =
            await publisher.PublishOrRecoverAsync(fixture.Operation.OperationId);

        Assert.Equal(ArchiveSplitPhase.PhysicalPublished, first.Phase);
        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Equal(first.SourceFileName, second.SourceFileName);
        Assert.Equal(first.SourceDatabaseId, second.SourceDatabaseId);
        Assert.Equal(first.SourceCoverage, second.SourceCoverage);
        Assert.Equal(first.Phase, second.Phase);
        Assert.Equal(first.CreatedAtUtc, second.CreatedAtUtc);
        Assert.Equal(first.Segments.Count, second.Segments.Count);
        for (int index = 0; index < first.Segments.Count; index++)
        {
            Assert.Equal(first.Segments[index], second.Segments[index]);
        }

        Assert.True(File.Exists(
            publisher.GetBackupPath(fixture.Operation.OperationId)));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_PlannedPhaseCannotPublish()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePlannedSplitAsync(environment);
        var publisher = new ProtectedArchiveSplitPublisher(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishOrRecoverAsync(fixture.Operation.OperationId));

        Assert.Equal(
            3,
            Scalar(
                environment,
                fixture.SourcePath,
                "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    private static async Task<SplitFixture> CreateReadySplitAsync(
        GlobalPolicyTestEnvironment environment)
    {
        SplitFixture planned = await CreatePlannedSplitAsync(environment);
        PendingArchiveSplitOperation ready =
            await planned.Builder.BuildAsync(planned.Operation.OperationId);
        return planned with { Operation = ready };
    }

    private static async Task<SplitFixture> CreatePlannedSplitAsync(
        GlobalPolicyTestEnvironment environment)
    {
        var sourceFileName = new ArchiveFileName(
            60,
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
        SeedSourceArchive(environment, sourcePath);

        JournalDateRange[] requestedRanges =
        [
            new(
                new DateOnly(2026, 8, 1),
                new DateOnly(2026, 8, 15)),
            new(
                new DateOnly(2026, 8, 16),
                new DateOnly(2026, 8, 23)),
            new(
                new DateOnly(2026, 8, 24),
                new DateOnly(2026, 8, 31)),
        ];

        IReadOnlyList<PendingArchiveSplitSegment> segments =
            new ArchiveSplitPlanner().Build(
                sourceFileName,
                sourceIdentity.DatabaseId,
                coverage,
                requestedRanges,
                [sourceFileName],
                IdFactory(Id(501), Id(502)));

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

        return new SplitFixture(
            sourcePath,
            operation,
            builder);
    }

    private static void SeedSourceArchive(
        GlobalPolicyTestEnvironment environment,
        string sourcePath)
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
                ('{event2}', 0, 'Text', 'Text', 6,
                 'second', 'second', NULL, NULL, NULL),
                ('{event3}', 0, 'Text', 'Text', 5,
                 'third', 'third', NULL, NULL, NULL);
            """);
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

    private static long Scalar(
        GlobalPolicyTestEnvironment environment,
        string databasePath,
        string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            databasePath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string TextScalar(
        GlobalPolicyTestEnvironment environment,
        string databasePath,
        string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            databasePath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static Func<Guid> IdFactory(params Guid[] ids)
    {
        int index = 0;
        return () => index < ids.Length
            ? ids[index++]
            : throw new InvalidOperationException(
                "Test DatabaseId factory exhausted.");
    }

    private static Guid Id(int value) =>
        new(value, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    private sealed record SplitFixture(
        string SourcePath,
        PendingArchiveSplitOperation Operation,
        ProtectedArchiveSplitShadowBuilder Builder);

    private sealed class SimulatedCrashException(
        ArchiveSplitPublicationCheckpoint checkpoint)
        : Exception($"Simulated crash at {checkpoint}.");
}
