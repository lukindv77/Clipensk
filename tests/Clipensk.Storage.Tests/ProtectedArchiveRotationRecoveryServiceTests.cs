using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveRotationRecoveryServiceTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task RecoverAsync_IsNoOpWithoutAPendingOperation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var recovery = new ProtectedArchiveRotationRecoveryService(
            environment.Session,
            environment.Factory);

        ArchiveRotationRecoveryResult result = await recovery.RecoverAsync(Today);

        Assert.False(result.HadPendingOperation);
        Assert.Empty(result.Descriptors);
    }

    [Theory]
    [InlineData(ArchiveRotationPhase.Planned)]
    [InlineData(ArchiveRotationPhase.ReadyToPublish)]
    [InlineData(ArchiveRotationPhase.PhysicalPublished)]
    [InlineData(ArchiveRotationPhase.SourcePurged)]
    public async Task RecoverAsync_RollsForwardToCompletionFromEveryDurablePhase(
        ArchiveRotationPhase startPhase)
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await OperationAtPhaseAsync(
            environment,
            startPhase,
            [new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 21)]);

        var recovery = new ProtectedArchiveRotationRecoveryService(
            environment.Session,
            environment.Factory);
        ArchiveRotationRecoveryResult result = await recovery.RecoverAsync(Today);

        Assert.True(result.HadPendingOperation);
        Assert.Equal(operationId, result.OperationId);
        Assert.Equal(startPhase, result.ResumedFromPhase);

        foreach (PendingArchiveRotationTarget target in plan.Targets)
        {
            Assert.True(File.Exists(
                Path.Combine(environment.Root, "Archive", target.FileName.FileName)));
            Assert.Contains(
                result.Descriptors,
                descriptor => descriptor.FileName == target.FileName.FileName
                    && descriptor.DatabaseId == target.DatabaseId
                    && descriptor.Coverage == target.Coverage);
        }

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        Assert.Null(await repository.ReadAsync());
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.False(Directory.Exists(Path.Combine(
            environment.Root,
            "Archive",
            $".clipensk-archive-rotation-{operationId:D}")));
    }

    [Fact]
    public async Task RecoverAsync_RebuildsPlannedShadowsLostBeforePublication()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await OperationAtPhaseAsync(
            environment,
            ArchiveRotationPhase.Planned,
            [new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 21)]);

        // A crash left staging incomplete; Current still holds every planned source row.
        Directory.Delete(
            Path.Combine(environment.Root, "Archive", $".clipensk-archive-rotation-{operationId:D}"),
            recursive: true);

        var recovery = new ProtectedArchiveRotationRecoveryService(
            environment.Session,
            environment.Factory);
        ArchiveRotationRecoveryResult result = await recovery.RecoverAsync(Today);

        Assert.True(result.HadPendingOperation);
        Assert.Equal(2, plan.Targets.Count);
        foreach (PendingArchiveRotationTarget target in plan.Targets)
        {
            string finalPath = Path.Combine(environment.Root, "Archive", target.FileName.FileName);
            Assert.True(File.Exists(finalPath));
            // The rebuilt shadow reproduced the planned physical evidence exactly.
            Assert.Equal(target.ShadowPhysicalSizeBytes, new FileInfo(finalPath).Length);
        }
    }

    [Fact]
    public async Task RecoverAsync_FailsClosedWhenPlannedTargetAlreadyExists()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await OperationAtPhaseAsync(
            environment,
            ArchiveRotationPhase.Planned,
            [new DateOnly(2026, 2, 20)]);

        // A planned canonical name must not be occupied while the operation is still Planned.
        File.Copy(
            Path.Combine(
                environment.Root,
                "Archive",
                $".clipensk-archive-rotation-{operationId:D}",
                plan.Targets[0].FileName.FileName),
            Path.Combine(environment.Root, "Archive", plan.Targets[0].FileName.FileName));

        var recovery = new ProtectedArchiveRotationRecoveryService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await recovery.RecoverAsync(Today));

        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationOperation? pending = await repository.ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(ArchiveRotationPhase.Planned, pending!.Phase);
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    private static async Task<(Guid OperationId, ArchiveRotationShadowPlan Plan)> OperationAtPhaseAsync(
        GlobalPolicyTestEnvironment environment,
        ArchiveRotationPhase phase,
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
        if (phase == ArchiveRotationPhase.Planned)
        {
            return (operationId, plan);
        }

        _ = await repository.AdvancePhaseAsync(
            operationId,
            ArchiveRotationPhase.Planned,
            ArchiveRotationPhase.ReadyToPublish);
        if (phase == ArchiveRotationPhase.ReadyToPublish)
        {
            return (operationId, plan);
        }

        var publisher = new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory);
        _ = await publisher.PublishOrRecoverAsync(operationId);
        if (phase == ArchiveRotationPhase.PhysicalPublished)
        {
            return (operationId, plan);
        }

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
