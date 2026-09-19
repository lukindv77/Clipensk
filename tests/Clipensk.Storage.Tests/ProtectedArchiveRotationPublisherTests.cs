using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveRotationPublisherTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task PublishOrRecoverAsync_CopiesShadowsToCanonicalFilesAndAdvancesPhase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PrepareReadyOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 21)]);

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        PendingArchiveRotationOperation published = await publisher.PublishOrRecoverAsync(operationId);

        Assert.Equal(ArchiveRotationPhase.PhysicalPublished, published.Phase);
        Assert.Equal(2, plan.Targets.Count);
        foreach (PendingArchiveRotationTarget target in plan.Targets)
        {
            string finalPath = Path.Combine(environment.Root, "Archive", target.FileName.FileName);
            Assert.True(File.Exists(finalPath));
            Assert.Equal(target.ShadowPhysicalSizeBytes, new FileInfo(finalPath).Length);

            // The staging shadow is retained as the backup for the later source purge.
            Assert.True(File.Exists(StagedPath(environment, operationId, target.FileName)));
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(environment.Root, "Archive"), "*.tmp"));
        // Current is still the durable source until a later phase purges it.
        Assert.Equal(2, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_IsIdempotentOncePhysicalPublished()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, _) = await PrepareReadyOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20)]);

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        _ = await publisher.PublishOrRecoverAsync(operationId);
        PendingArchiveRotationOperation again = await publisher.PublishOrRecoverAsync(operationId);

        Assert.Equal(ArchiveRotationPhase.PhysicalPublished, again.Phase);
    }

    [Fact]
    public async Task PublishOrRecoverAsync_ResumesAfterCrashBetweenTargets()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PrepareReadyOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 21)]);

        var crashing = new ProtectedArchiveRotationPublisher(
            environment.Session,
            environment.Factory,
            (checkpoint, segmentOrder) =>
            {
                if (checkpoint == ArchiveRotationPublicationCheckpoint.AfterFinalValidation &&
                    segmentOrder == 0)
                {
                    throw new IOException("simulated crash after first target");
                }
            });
        await Assert.ThrowsAsync<IOException>(
            async () => await crashing.PublishOrRecoverAsync(operationId));

        string firstFinal = Path.Combine(environment.Root, "Archive", plan.Targets[0].FileName.FileName);
        string secondFinal = Path.Combine(environment.Root, "Archive", plan.Targets[1].FileName.FileName);
        Assert.True(File.Exists(firstFinal));
        Assert.False(File.Exists(secondFinal));

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        PendingArchiveRotationOperation resumed = await publisher.PublishOrRecoverAsync(operationId);

        Assert.Equal(ArchiveRotationPhase.PhysicalPublished, resumed.Phase);
        Assert.True(File.Exists(secondFinal));
        Assert.Empty(Directory.GetFiles(Path.Combine(environment.Root, "Archive"), "*.tmp"));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_DiscardsTemporaryFileLeftByACrashedAttempt()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PrepareReadyOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20)]);

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        string temporaryPath = Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}-{plan.Targets[0].FileName.FileName}.tmp");
        File.WriteAllText(temporaryPath, "stale attempt");

        PendingArchiveRotationOperation published = await publisher.PublishOrRecoverAsync(operationId);

        Assert.Equal(ArchiveRotationPhase.PhysicalPublished, published.Phase);
        Assert.False(File.Exists(temporaryPath));
        Assert.Equal(
            plan.Targets[0].ShadowPhysicalSizeBytes,
            new FileInfo(Path.Combine(environment.Root, "Archive", plan.Targets[0].FileName.FileName)).Length);
    }

    [Fact]
    public async Task PublishOrRecoverAsync_FailsClosedOnForeignFileAtReservedName()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PrepareReadyOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20)]);

        // A different, independently created Archive now occupies the reserved canonical name.
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            plan.Targets[0].FileName,
            plan.Targets[0].Coverage);

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await publisher.PublishOrRecoverAsync(operationId));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_FailsClosedOnDirectoryAtReservedName()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PrepareReadyOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20)]);

        Directory.CreateDirectory(
            Path.Combine(environment.Root, "Archive", plan.Targets[0].FileName.FileName));

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await publisher.PublishOrRecoverAsync(operationId));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_FailsClosedWhenStagedShadowIsMissing()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PrepareReadyOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20)]);

        File.Delete(StagedPath(environment, operationId, plan.Targets[0].FileName));

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        await Assert.ThrowsAnyAsync<IOException>(
            async () => await publisher.PublishOrRecoverAsync(operationId));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_RejectsWrongPhaseAndForeignOwnership()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20));
        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            Today);
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        _ = await repository.StartAsync(operationId, plan.PolicySnapshot, plan.Targets);

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await publisher.PublishOrRecoverAsync(operationId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await publisher.PublishOrRecoverAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await publisher.PublishOrRecoverAsync(Guid.Empty));
    }

    private static async Task<(Guid OperationId, ArchiveRotationShadowPlan Plan)> PrepareReadyOperationAsync(
        GlobalPolicyTestEnvironment environment,
        ArchiveRotationSettings settings,
        IReadOnlyList<DateOnly> days)
    {
        foreach (DateOnly day in days)
        {
            SeedDay(environment, day);
        }

        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(operationId, settings, Today);

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        _ = await repository.StartAsync(operationId, plan.PolicySnapshot, plan.Targets);
        _ = await repository.AdvancePhaseAsync(
            operationId,
            ArchiveRotationPhase.Planned,
            ArchiveRotationPhase.ReadyToPublish);
        return (operationId, plan);
    }

    private static string StagedPath(
        GlobalPolicyTestEnvironment environment,
        Guid operationId,
        ArchiveFileName fileName) =>
        Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}",
            fileName.FileName);

    private static void SeedDay(GlobalPolicyTestEnvironment environment, DateOnly day)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        Guid eventId = Guid.NewGuid();
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO ClipboardHistoryEvent (
                    EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                    SourceApplicationId, SourceProcessId, SourceExecutablePath,
                    SourceApplicationUserModelId)
                VALUES ($eventId, $eventUtc, 0, 'UTC', $calendarDate, NULL, NULL, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
            insert.Parameters.AddWithValue("$eventUtc", $"{day:yyyy-MM-dd}T09:00:00.0000000+00:00");
            insert.Parameters.AddWithValue(
                "$calendarDate",
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        using SqliteCommand payload = connection.CreateCommand();
        payload.CommandText = """
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath,
                ExternalSizeBytes)
            VALUES ($eventId, 0, 'Text', 'Text', 4, 'text', 'text', NULL, NULL, NULL);
            """;
        payload.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        payload.ExecuteNonQuery();
    }
}
