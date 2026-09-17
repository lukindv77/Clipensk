using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveSplitShadowBuilderTests
{
    [Fact]
    public async Task BuildAsync_BuildsValidatedShadowSetFromPersistedCalendarDate()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePlannedSplitAsync(environment);
        var builder = new ProtectedArchiveSplitShadowBuilder(
            environment.Session,
            environment.Factory);

        string stagingDirectory = builder.GetStagingDirectory(fixture.Operation.OperationId);
        Directory.CreateDirectory(stagingDirectory);
        string staleSentinel = Path.Combine(stagingDirectory, "stale.txt");
        File.WriteAllText(staleSentinel, "stale");

        Guid unrelatedOperationId = Id(900);
        string unrelatedDirectory = builder.GetStagingDirectory(unrelatedOperationId);
        Directory.CreateDirectory(unrelatedDirectory);
        string unrelatedSentinel = Path.Combine(unrelatedDirectory, "keep.txt");
        File.WriteAllText(unrelatedSentinel, "keep");

        PendingArchiveSplitOperation ready =
            await builder.BuildAsync(fixture.Operation.OperationId);

        Assert.Equal(ArchiveSplitPhase.ReadyToPublish, ready.Phase);
        Assert.False(File.Exists(staleSentinel));
        Assert.True(File.Exists(unrelatedSentinel));

        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(ArchiveSplitPhase.ReadyToPublish, persisted.Phase);
        Assert.Equal(ready.OperationId, persisted.OperationId);

        Assert.True(File.Exists(fixture.SourcePath));
        Assert.Equal(3, Scalar(environment, fixture.SourcePath, """
            SELECT COUNT(*) FROM ClipboardHistoryEvent;
            """));

        for (int index = 0; index < ready.Segments.Count; index++)
        {
            PendingArchiveSplitSegment segment = ready.Segments[index];
            string stagedPath = StagedPath(builder, ready, segment);
            Assert.True(File.Exists(stagedPath));
            Assert.Equal(
                segment.DatabaseId.ToString("D"),
                TextScalar(environment, stagedPath, """
                    SELECT DatabaseId FROM DatabaseIdentity WHERE SingletonId = 1;
                    """));
            Assert.Equal(
                segment.Coverage.StartDate.ToString("yyyy-MM-dd"),
                TextScalar(environment, stagedPath, """
                    SELECT CoverageStartDate FROM DatabaseIdentity WHERE SingletonId = 1;
                    """));
            Assert.Equal(
                segment.Coverage.EndDate.ToString("yyyy-MM-dd"),
                TextScalar(environment, stagedPath, """
                    SELECT CoverageEndDate FROM DatabaseIdentity WHERE SingletonId = 1;
                    """));

            if (index > 0)
            {
                Assert.False(File.Exists(
                    Path.Combine(
                        environment.Root,
                        "Archive",
                        segment.FileName.FileName)));
            }
        }

        Assert.Equal(
            fixture.SourceIdentity.CreatedAtUtc.ToString("O"),
            TextScalar(
                environment,
                StagedPath(builder, ready, ready.Segments[0]),
                """
                SELECT CreatedAtUtc FROM DatabaseIdentity WHERE SingletonId = 1;
                """));

        string firstSegmentPath = StagedPath(builder, ready, ready.Segments[0]);
        Assert.Equal(1, Scalar(environment, firstSegmentPath, """
            SELECT COUNT(*)
            FROM ClipboardHistoryEvent
            WHERE CalendarDate = '2026-08-15'
              AND EventUtc = '2026-08-16T00:01:00.0000000+00:00';
            """));
        Assert.Equal(1, Scalar(environment, firstSegmentPath, """
            SELECT COUNT(*)
            FROM ApplicationIdentityAlias
            WHERE AliasType = 'ExecutablePath'
              AND AliasValue = 'C:\Apps\Alpha.exe';
            """));

        string secondSegmentPath = StagedPath(builder, ready, ready.Segments[1]);
        ExternalPayloadReference external = ReadExternalPayload(
            environment,
            secondSegmentPath,
            Id(302).ToString("D"));
        Assert.Equal("AA11BB22", external.Sha256);
        Assert.Equal("Payloads/aa/11.bin", external.RelativePath);
        Assert.Equal(2048L, external.SizeBytes);

        Assert.Equal(
            1,
            Scalar(environment, secondSegmentPath, """
                SELECT COUNT(*)
                FROM ClipboardHistoryEvent
                WHERE CalendarDate = '2026-08-16';
                """));
        Assert.Equal(
            1,
            Scalar(
                environment,
                StagedPath(builder, ready, ready.Segments[2]),
                """
                SELECT COUNT(*)
                FROM ClipboardHistoryEvent
                WHERE CalendarDate = '2026-08-25';
                """));
    }

    [Fact]
    public async Task BuildAsync_SourceIdentityMismatchFailsBeforeStagingCleanup()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePlannedSplitAsync(environment);
        var builder = new ProtectedArchiveSplitShadowBuilder(
            environment.Session,
            environment.Factory);

        string stagingDirectory = builder.GetStagingDirectory(fixture.Operation.OperationId);
        Directory.CreateDirectory(stagingDirectory);
        string sentinel = Path.Combine(stagingDirectory, "keep-on-failure.txt");
        File.WriteAllText(sentinel, "keep");

        Execute(
            environment,
            fixture.SourcePath,
            $"UPDATE DatabaseIdentity SET DatabaseId = '{Id(777):D}' WHERE SingletonId = 1;");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            builder.BuildAsync(fixture.Operation.OperationId));

        Assert.True(File.Exists(sentinel));
        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(ArchiveSplitPhase.Planned, persisted.Phase);
    }

    [Fact]
    public async Task BuildAsync_ReservedFinalFilenameCollisionFailsClosed()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePlannedSplitAsync(environment);
        var builder = new ProtectedArchiveSplitShadowBuilder(
            environment.Session,
            environment.Factory);

        PendingArchiveSplitSegment reserved = fixture.Operation.Segments[1];
        string reservedFinalPath = Path.Combine(
            environment.Root,
            "Archive",
            reserved.FileName.FileName);
        File.WriteAllText(reservedFinalPath, "unexpected collision");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildAsync(fixture.Operation.OperationId));

        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(ArchiveSplitPhase.Planned, persisted.Phase);
        Assert.Equal(3, Scalar(environment, fixture.SourcePath, """
            SELECT COUNT(*) FROM ClipboardHistoryEvent;
            """));
        Assert.True(File.Exists(reservedFinalPath));
        Assert.True(Directory.Exists(
            builder.GetStagingDirectory(fixture.Operation.OperationId)));
    }

    [Fact]
    public async Task ValidateShadowSet_DetectsStagedPayloadTampering()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePlannedSplitAsync(environment);
        var builder = new ProtectedArchiveSplitShadowBuilder(
            environment.Session,
            environment.Factory);
        PendingArchiveSplitOperation ready =
            await builder.BuildAsync(fixture.Operation.OperationId);

        string firstSegmentPath = StagedPath(builder, ready, ready.Segments[0]);
        Execute(
            environment,
            firstSegmentPath,
            """
            UPDATE ClipboardHistoryPayload
            SET InlineCanonicalText = 'tampered'
            WHERE EventId = '0000012d-0000-0000-0000-000000000001'
              AND PayloadOrder = 0;
            """);

        using ProtectedStorageMutationLease mutationLease =
            await environment.Session.AcquireMutationLeaseAsync();

        Assert.Throws<InvalidDataException>(() =>
            builder.ValidateShadowSet(ready, mutationLease));
    }

    [Fact]
    public async Task BuildAsync_CancellationBeforeReadyToPublishLeavesFinalArchiveUnchanged()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        SplitFixture fixture = await CreatePlannedSplitAsync(environment);
        var builder = new ProtectedArchiveSplitShadowBuilder(
            environment.Session,
            environment.Factory);
        using var cancellation = new CancellationTokenSource();

        environment.Factory.OnOpen = (_, mode) =>
        {
            if (mode == SqliteOpenMode.ReadWriteCreate)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            builder.BuildAsync(
                fixture.Operation.OperationId,
                cancellation.Token));

        environment.Factory.OnOpen = null;
        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());

        Assert.Equal(ArchiveSplitPhase.Planned, persisted.Phase);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.Equal(3, Scalar(environment, fixture.SourcePath, """
            SELECT COUNT(*) FROM ClipboardHistoryEvent;
            """));
        foreach (PendingArchiveSplitSegment segment in fixture.Operation.Segments.Skip(1))
        {
            Assert.False(File.Exists(Path.Combine(
                environment.Root,
                "Archive",
                segment.FileName.FileName)));
        }
    }

    private static async Task<SplitFixture> CreatePlannedSplitAsync(
        GlobalPolicyTestEnvironment environment)
    {
        var sourceFileName = new ArchiveFileName(
            50,
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
                IdFactory(Id(401), Id(402)));

        PendingArchiveSplitOperation operation =
            await new SqlitePendingArchiveSplitRepository(
                environment.Session,
                environment.Factory).StartAsync(
                    sourceFileName,
                    sourceIdentity.DatabaseId,
                    coverage,
                    segments);

        return new SplitFixture(
            sourceFileName,
            sourceIdentity,
            sourcePath,
            operation);
    }

    private static void SeedSourceArchive(
        GlobalPolicyTestEnvironment environment,
        string sourcePath)
    {
        string app1 = Id(201).ToString("D");
        string app2 = Id(202).ToString("D");
        string event1 = Id(301).ToString("D");
        string event2 = Id(302).ToString("D");
        string event3 = Id(303).ToString("D");

        Execute(
            environment,
            sourcePath,
            $"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES
                ('{app1}', '2026-08-01T00:00:00.0000000+00:00'),
                ('{app2}', '2026-08-01T00:00:00.0000000+00:00');

            INSERT INTO ApplicationIdentityAlias (
                AliasType, AliasValue, ApplicationId, CreatedAtUtc)
            VALUES
                ('ExecutablePath', 'C:\Apps\Alpha.exe', '{app1}',
                 '2026-08-01T00:00:00.0000000+00:00'),
                ('Aumid', 'Clipensk.Tests.Beta', '{app2}',
                 '2026-08-01T00:00:00.0000000+00:00');

            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath,
                SourceApplicationUserModelId)
            VALUES
                ('{event1}', '2026-08-16T00:01:00.0000000+00:00', 180,
                 'Turkey Standard Time', '2026-08-15',
                 '{app1}', 101, 'C:\Apps\Alpha.exe', NULL),
                ('{event2}', '2026-08-16T12:00:00.0000000+00:00', 180,
                 'Turkey Standard Time', '2026-08-16',
                 '{app2}', 202, NULL, 'Clipensk.Tests.Beta'),
                ('{event3}', '2026-08-25T12:00:00.0000000+00:00', 180,
                 'Turkey Standard Time', '2026-08-25',
                 '{app2}', 202, NULL, 'Clipensk.Tests.Beta');

            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256,
                ExternalRelativePath, ExternalSizeBytes)
            VALUES
                ('{event1}', 0, 'Text', 'Text', 5,
                 'first', 'first', NULL, NULL, NULL),
                ('{event2}', 0, 'PNG', 'PngImage', 2048,
                 NULL, NULL, 'AA11BB22', 'Payloads/aa/11.bin', 2048),
                ('{event3}', 0, 'Link', 'Link', 21,
                 'https://example.test', 'example', NULL, NULL, NULL);
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

    private static ExternalPayloadReference ReadExternalPayload(
        GlobalPolicyTestEnvironment environment,
        string databasePath,
        string eventId)
    {
        using SqliteConnection connection = environment.Factory.Open(
            databasePath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ExternalSha256, ExternalRelativePath, ExternalSizeBytes
            FROM ClipboardHistoryPayload
            WHERE EventId = $eventId AND PayloadOrder = 0;
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var result = new ExternalPayloadReference(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2));
        Assert.False(reader.Read());
        return result;
    }

    private static string StagedPath(
        ProtectedArchiveSplitShadowBuilder builder,
        PendingArchiveSplitOperation operation,
        PendingArchiveSplitSegment segment) =>
        Path.Combine(
            builder.GetStagingDirectory(operation.OperationId),
            segment.FileName.FileName);

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
        ArchiveFileName SourceFileName,
        DatabaseIdentity SourceIdentity,
        string SourcePath,
        PendingArchiveSplitOperation Operation);

    private sealed record ExternalPayloadReference(
        string Sha256,
        string RelativePath,
        long SizeBytes);
}
