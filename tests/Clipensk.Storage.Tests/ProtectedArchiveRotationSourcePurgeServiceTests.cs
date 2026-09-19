using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveRotationSourcePurgeServiceTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task PurgeOrRecoverAsync_PurgesPlannedRangesAndKeepsOpenTail()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        // Two days per ready range over three seeded days leaves the third day short of the rule.
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PublishedOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 2 },
            [new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 21), new DateOnly(2026, 2, 22)]);

        // The third day never reached the rule, so it is the open tail and must survive.
        Assert.Single(plan.Targets);
        Assert.Equal(
            new JournalDateRange(new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 21)),
            plan.Targets[0].Coverage);
        Assert.Equal(
            new JournalDateRange(new DateOnly(2026, 2, 22), new DateOnly(2026, 2, 22)),
            plan.OpenTail);

        var service = new ProtectedArchiveRotationSourcePurgeService(
            environment.Session,
            environment.Factory);
        ArchiveRotationSourcePurgeResult result = await service.PurgeOrRecoverAsync(operationId, Today);

        Assert.Equal(ArchiveRotationPhase.SourcePurged, result.Operation.Phase);
        Assert.Equal(2, result.PurgedEventCount);
        Assert.Equal(0, result.AlreadyPurgedRangeCount);
        Assert.Equal(0, result.Operation.Targets.Count - plan.Targets.Count);
        Assert.Equal(
            0,
            environment.Scalar(
                "SELECT COUNT(*) FROM ClipboardHistoryEvent WHERE CalendarDate <= '2026-02-21';"));
        Assert.Equal(
            1,
            environment.Scalar(
                "SELECT COUNT(*) FROM ClipboardHistoryEvent WHERE CalendarDate = '2026-02-22';"));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryPayload;"));
    }

    [Fact]
    public async Task PurgeOrRecoverAsync_AcceptsRangeAlreadyPurgedByAnEarlierAttempt()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PublishedOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 21)]);

        // Simulate a crashed attempt that already completed the first range.
        environment.Execute("DELETE FROM ClipboardHistoryEvent WHERE CalendarDate = '2026-02-20';");

        var service = new ProtectedArchiveRotationSourcePurgeService(
            environment.Session,
            environment.Factory);
        ArchiveRotationSourcePurgeResult result = await service.PurgeOrRecoverAsync(operationId, Today);

        Assert.Equal(ArchiveRotationPhase.SourcePurged, result.Operation.Phase);
        Assert.Equal(1, result.AlreadyPurgedRangeCount);
        Assert.Equal(1, result.PurgedEventCount);
        Assert.Equal(2, plan.Targets.Count);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task PurgeOrRecoverAsync_IsIdempotentOnceSourcePurged()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, _) = await PublishedOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20)]);

        var service = new ProtectedArchiveRotationSourcePurgeService(
            environment.Session,
            environment.Factory);
        _ = await service.PurgeOrRecoverAsync(operationId, Today);
        ArchiveRotationSourcePurgeResult again = await service.PurgeOrRecoverAsync(operationId, Today);

        Assert.Equal(ArchiveRotationPhase.SourcePurged, again.Operation.Phase);
        Assert.Equal(0, again.PurgedEventCount);
    }

    [Fact]
    public async Task PurgeOrRecoverAsync_FailsClosedWhenRetainedShadowIsGone()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PublishedOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20)]);

        File.Delete(Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}",
            plan.Targets[0].FileName.FileName));

        var service = new ProtectedArchiveRotationSourcePurgeService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await service.PurgeOrRecoverAsync(operationId, Today));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task PurgeOrRecoverAsync_FailsClosedWhenPublishedTargetIsMissing()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PublishedOperationAsync(
            environment,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            [new DateOnly(2026, 2, 20)]);

        File.Delete(Path.Combine(environment.Root, "Archive", plan.Targets[0].FileName.FileName));

        var service = new ProtectedArchiveRotationSourcePurgeService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await service.PurgeOrRecoverAsync(operationId, Today));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task PurgeOrRecoverAsync_RejectsWrongPhaseAndForeignOwnership()
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

        var service = new ProtectedArchiveRotationSourcePurgeService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.PurgeOrRecoverAsync(operationId, Today));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.PurgeOrRecoverAsync(Guid.NewGuid(), Today));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await service.PurgeOrRecoverAsync(Guid.Empty, Today));
    }

    private static async Task<(Guid OperationId, ArchiveRotationShadowPlan Plan)> PublishedOperationAsync(
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

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        _ = await publisher.PublishOrRecoverAsync(operationId);
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
