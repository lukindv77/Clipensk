using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveRotationCatalogPublisherTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task PublishOrRecoverAsync_ProjectsTargetsClearsMarkerAndDeletesStaging()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PurgedOperationAsync(
            environment,
            [new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 21)]);

        var publisher = new ProtectedArchiveRotationCatalogPublisher(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ArchiveSegmentDescriptor> descriptors =
            await publisher.PublishOrRecoverAsync(operationId, Today);

        Assert.Equal(2, plan.Targets.Count);
        foreach (PendingArchiveRotationTarget target in plan.Targets)
        {
            ArchiveSegmentDescriptor descriptor = Assert.Single(
                descriptors,
                candidate => candidate.FileName == target.FileName.FileName);
            Assert.Equal(target.DatabaseId, descriptor.DatabaseId);
            Assert.Equal(target.Coverage, descriptor.Coverage);
        }

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        Assert.Null(await repository.ReadAsync());
        Assert.False(Directory.Exists(Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}")));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_ResumesCleanupAfterCatalogPhaseCommit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, _) = await PurgedOperationAsync(
            environment,
            [new DateOnly(2026, 2, 20)]);

        var crashing = new ProtectedArchiveRotationCatalogPublisher(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ArchiveRotationCatalogCheckpoint.AfterCatalogPhaseCommit)
                {
                    throw new IOException("simulated crash after catalog phase commit");
                }
            });
        await Assert.ThrowsAsync<IOException>(
            async () => await crashing.PublishOrRecoverAsync(operationId, Today));

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationOperation? pending = await repository.ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(ArchiveRotationPhase.CatalogPublished, pending!.Phase);

        var publisher = new ProtectedArchiveRotationCatalogPublisher(
            environment.Session,
            environment.Factory);
        _ = await publisher.PublishOrRecoverAsync(operationId, Today);

        Assert.Null(await repository.ReadAsync());
        Assert.False(Directory.Exists(Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}")));
    }

    [Fact]
    public async Task PublishOrRecoverAsync_FailsClosedWhenRetainedShadowIsLostBeforeCatalog()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PurgedOperationAsync(
            environment,
            [new DateOnly(2026, 2, 20)]);

        File.Delete(Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}",
            plan.Targets[0].FileName.FileName));

        var publisher = new ProtectedArchiveRotationCatalogPublisher(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await publisher.PublishOrRecoverAsync(operationId, Today));

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationOperation? pending = await repository.ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(ArchiveRotationPhase.SourcePurged, pending!.Phase);
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

        var publisher = new ProtectedArchiveRotationCatalogPublisher(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await publisher.PublishOrRecoverAsync(operationId, Today));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await publisher.PublishOrRecoverAsync(Guid.NewGuid(), Today));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await publisher.PublishOrRecoverAsync(Guid.Empty, Today));
    }

    private static async Task<(Guid OperationId, ArchiveRotationShadowPlan Plan)> PurgedOperationAsync(
        GlobalPolicyTestEnvironment environment,
        IReadOnlyList<DateOnly> days)
    {
        foreach (DateOnly day in days)
        {
            SeedDay(environment, day);
        }

        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(environment.Session, environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            Today);

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        _ = await repository.StartAsync(operationId, plan.PolicySnapshot, plan.Targets);
        _ = await repository.AdvancePhaseAsync(
            operationId,
            ArchiveRotationPhase.Planned,
            ArchiveRotationPhase.ReadyToPublish);

        var physicalPublisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        _ = await physicalPublisher.PublishOrRecoverAsync(operationId);

        var purge = new ProtectedArchiveRotationSourcePurgeService(environment.Session, environment.Factory);
        _ = await purge.PurgeOrRecoverAsync(operationId, Today);
        return (operationId, plan);
    }

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
