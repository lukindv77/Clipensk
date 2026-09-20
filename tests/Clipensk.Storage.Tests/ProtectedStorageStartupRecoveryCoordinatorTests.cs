using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageStartupRecoveryCoordinatorTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task RecoverAsync_IsNoOpWhenNothingIsPending()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();

        ProtectedStorageStartupRecoveryResult result = await Coordinator(environment)
            .RecoverAsync(Today);

        Assert.False(result.HadPendingWork);
        Assert.Null(result.ArchiveSplitOperationId);
        Assert.Empty(result.ArchiveSplitDescriptors);
        Assert.False(result.ArchiveRotation.HadPendingOperation);
        Assert.False(result.PolicyMaintenance.HadPendingOperation);
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task RecoverAsync_CompletesAPendingArchiveRotation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PendingRotationAsync(
            environment,
            ArchiveRotationPhase.PhysicalPublished,
            [new DateOnly(2026, 2, 20), new DateOnly(2026, 2, 21)]);

        ProtectedStorageStartupRecoveryResult result = await Coordinator(environment)
            .RecoverAsync(Today);

        Assert.True(result.HadPendingWork);
        Assert.True(result.ArchiveRotation.HadPendingOperation);
        Assert.Equal(operationId, result.ArchiveRotation.OperationId);
        Assert.Equal(ArchiveRotationPhase.PhysicalPublished, result.ArchiveRotation.ResumedFromPhase);
        Assert.Null(result.ArchiveSplitOperationId);

        foreach (PendingArchiveRotationTarget target in plan.Targets)
        {
            Assert.True(File.Exists(
                Path.Combine(environment.Root, "Archive", target.FileName.FileName)));
            Assert.Contains(
                result.ArchiveRotation.Descriptors,
                descriptor => descriptor.FileName == target.FileName.FileName);
        }

        Assert.Null(await RotationRepository(environment).ReadAsync());
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task RecoverAsync_CompletesAPendingArchiveSplit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        PendingArchiveSplitOperation split = await PendingSplitAsync(environment);

        ProtectedStorageStartupRecoveryResult result = await Coordinator(environment)
            .RecoverAsync(Today);

        Assert.True(result.HadPendingWork);
        Assert.Equal(split.OperationId, result.ArchiveSplitOperationId);
        Assert.Equal(split.Segments.Count, result.ArchiveSplitDescriptors.Count);
        Assert.False(result.ArchiveRotation.HadPendingOperation);
        Assert.Null(await SplitRepository(environment).ReadAsync());
    }

    [Fact]
    public async Task RecoverAsync_CompletesRotationBeforeResumingPolicyMaintenance()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PendingRotationAsync(
            environment,
            ArchiveRotationPhase.ReadyToPublish,
            [new DateOnly(2026, 2, 20)]);
        Guid policyOperationId = await PendingPolicyMaintenanceAsync(environment);

        ProtectedStorageStartupRecoveryResult result = await Coordinator(environment)
            .RecoverAsync(Today);

        Assert.True(result.ArchiveRotation.HadPendingOperation);
        Assert.Equal(operationId, result.ArchiveRotation.OperationId);
        Assert.True(result.PolicyMaintenance.HadPendingOperation);
        Assert.Equal(policyOperationId, result.PolicyMaintenance.OperationId);

        // Both durable markers are gone, and the rotation published before the policy-maintenance
        // continuation reached the Archive set it maintains.
        Assert.Null(await RotationRepository(environment).ReadAsync());
        Assert.Null(await PolicyRepository(environment).ReadAsync());
        Assert.True(File.Exists(
            Path.Combine(environment.Root, "Archive", plan.Targets[0].FileName.FileName)));
    }

    [Fact]
    public async Task RecoverAsync_FailedRotationLeavesPolicyMaintenancePending()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid operationId, ArchiveRotationShadowPlan plan) = await PendingRotationAsync(
            environment,
            ArchiveRotationPhase.Planned,
            [new DateOnly(2026, 2, 20)]);
        Guid policyOperationId = await PendingPolicyMaintenanceAsync(environment);

        // A planned canonical name must not be occupied while the operation is still Planned.
        File.Copy(
            Path.Combine(
                environment.Root,
                "Archive",
                $".clipensk-archive-rotation-{operationId:D}",
                plan.Targets[0].FileName.FileName),
            Path.Combine(environment.Root, "Archive", plan.Targets[0].FileName.FileName));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Coordinator(environment).RecoverAsync(Today));

        // Capture must stay suspended and the policy-maintenance continuation must not have run
        // against an Archive set the failed rotation left undefined.
        PendingArchiveRotationOperation? rotation = await RotationRepository(environment).ReadAsync();
        Assert.NotNull(rotation);
        Assert.Equal(ArchiveRotationPhase.Planned, rotation!.Phase);

        PendingPolicyMaintenanceOperation? policy = await PolicyRepository(environment).ReadAsync();
        Assert.NotNull(policy);
        Assert.Equal(policyOperationId, policy!.OperationId);
    }

    [Fact]
    public async Task RunAsync_StartsNoRotationWhenThresholdsAreNotConfigured()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20));

        ProtectedStorageStartupResult result = await Coordinator(environment)
            .RunAsync(Today, rotationSettings: null, trashRetentionDays: null);

        Assert.Null(result.StartedRotation);
        Assert.False(result.Recovery.HadPendingWork);
        Assert.Null(await RotationRepository(environment).ReadAsync());
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task RunAsync_RunsANewRotationWhenThresholdsAreConfigured()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedDay(environment, new DateOnly(2026, 2, 20));
        SeedDay(environment, new DateOnly(2026, 2, 21));

        ProtectedStorageStartupResult result = await Coordinator(environment)
            .RunAsync(Today, new ArchiveRotationSettings { MaxCalendarDays = 1 }, trashRetentionDays: null);

        Assert.NotNull(result.StartedRotation);
        Assert.True(result.StartedRotation!.Started);
        Assert.Equal(2, result.StartedRotation.Targets.Count);
        Assert.Null(await RotationRepository(environment).ReadAsync());
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task RunAsync_RecoversTheInterruptedRotationBeforeStartingANewOne()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (Guid interrupted, ArchiveRotationShadowPlan plan) = await PendingRotationAsync(
            environment,
            ArchiveRotationPhase.ReadyToPublish,
            [new DateOnly(2026, 2, 20)]);
        // A second closed day that only becomes rotatable once the interrupted operation is gone.
        SeedDay(environment, new DateOnly(2026, 2, 21));

        // A new start against a pending rotation fails closed in the scanner, so completing here at
        // all proves recovery ran first.
        ProtectedStorageStartupResult result = await Coordinator(environment)
            .RunAsync(Today, new ArchiveRotationSettings { MaxCalendarDays = 1 }, trashRetentionDays: null);

        Assert.True(result.Recovery.ArchiveRotation.HadPendingOperation);
        Assert.Equal(interrupted, result.Recovery.ArchiveRotation.OperationId);
        Assert.NotNull(result.StartedRotation);
        Assert.True(result.StartedRotation!.Started);
        Assert.NotEqual(interrupted, result.StartedRotation.OperationId);

        Assert.True(File.Exists(
            Path.Combine(environment.Root, "Archive", plan.Targets[0].FileName.FileName)));
        Assert.True(File.Exists(Path.Combine(
            environment.Root,
            "Archive",
            result.StartedRotation.Targets[0].FileName.FileName)));
        Assert.Null(await RotationRepository(environment).ReadAsync());
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task RunAsync_DeletesExpiredTrashWhenRetentionIsConfigured()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        string expired = SeedTrashedPayload(environment, "2026-01-01");
        string fresh = SeedTrashedPayload(environment, "2026-02-25");

        ProtectedStorageStartupResult result = await Coordinator(environment)
            .RunAsync(Today, rotationSettings: null, trashRetentionDays: 30);

        Assert.NotNull(result.TrashRetention);
        Assert.Equal(1, result.TrashRetention!.DeletedDateDirectoryCount);
        Assert.False(Directory.Exists(expired));
        Assert.True(Directory.Exists(fresh));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunAsync_SkipsTrashRetentionWhenItIsNotUsable(int? trashRetentionDays)
    {
        // A hand-edited settings value must never leave clipboard capture suspended.
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        string expired = SeedTrashedPayload(environment, "2026-01-01");

        ProtectedStorageStartupResult result = await Coordinator(environment)
            .RunAsync(Today, rotationSettings: null, trashRetentionDays);

        Assert.Null(result.TrashRetention);
        Assert.True(Directory.Exists(expired));
    }

    private static string SeedTrashedPayload(
        GlobalPolicyTestEnvironment environment,
        string deletionDate)
    {
        string dateDirectory = Path.Combine(environment.Root, "Trash", deletionDate);
        Directory.CreateDirectory(Path.Combine(dateDirectory, "2025-12-01"));
        File.WriteAllBytes(
            Path.Combine(dateDirectory, "2025-12-01", "payload.png"),
            [1, 2, 3]);
        return dateDirectory;
    }

    private static ProtectedStorageStartupRecoveryCoordinator Coordinator(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static SqlitePendingArchiveRotationRepository RotationRepository(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static SqlitePendingArchiveSplitRepository SplitRepository(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static SqlitePendingPolicyMaintenanceRepository PolicyRepository(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    /// <summary>
    /// Publishes a policy change that denies a format no stored payload uses, so the durable
    /// pending-maintenance marker exists without deleting any history the rotation still owns.
    /// </summary>
    private static async Task<Guid> PendingPolicyMaintenanceAsync(
        GlobalPolicyTestEnvironment environment)
    {
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        CurrentPolicyMaintenanceResult applied =
            await new ProtectedCurrentPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(Policy(
                    ClipboardCapturePolicyRule.Allow,
                    ("Text", ClipboardCapturePolicyRule.Allow),
                    ("Html", ClipboardCapturePolicyRule.Deny)));
        return applied.Operation.OperationId;
    }

    private static ClipboardCapturePolicy Policy(
        ClipboardCapturePolicyRule capture,
        params (string Name, ClipboardCapturePolicyRule Rule)[] formats) =>
        new(
            capture,
            formats.ToDictionary(
                item => item.Name,
                item => new ClipboardFormatCapturePolicy(item.Rule),
                StringComparer.Ordinal));

    private static async Task<(Guid OperationId, ArchiveRotationShadowPlan Plan)> PendingRotationAsync(
        GlobalPolicyTestEnvironment environment,
        ArchiveRotationPhase phase,
        IReadOnlyList<DateOnly> days)
    {
        foreach (DateOnly day in days)
        {
            SeedDay(environment, day);
        }

        Guid operationId = Guid.NewGuid();
        var builder = new ProtectedArchiveRotationShadowBuilder(
            environment.Session,
            environment.Factory);
        ArchiveRotationShadowPlan plan = await builder.BuildAsync(
            operationId,
            new ArchiveRotationSettings { MaxCalendarDays = 1 },
            Today);

        SqlitePendingArchiveRotationRepository repository = RotationRepository(environment);
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

        _ = await new ProtectedArchiveRotationPublisher(environment.Session, environment.Factory)
            .PublishOrRecoverAsync(operationId);
        if (phase == ArchiveRotationPhase.PhysicalPublished)
        {
            return (operationId, plan);
        }

        _ = await new ProtectedArchiveRotationSourcePurgeService(
                environment.Session,
                environment.Factory)
            .PurgeOrRecoverAsync(operationId, Today);
        return (operationId, plan);
    }

    private static async Task<PendingArchiveSplitOperation> PendingSplitAsync(
        GlobalPolicyTestEnvironment environment)
    {
        var sourceFileName = new ArchiveFileName(81, ArchiveFileName.NoSplit);
        var sourceCoverage = new JournalDateRange(
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 4));
        DatabaseIdentity sourceIdentity = await new ProtectedArchiveDatabaseService(
                environment.Session,
                environment.Factory)
            .CreateAsync(sourceFileName, sourceCoverage);

        _ = await new ProtectedExternalPayloadCatalogRebuildService(
                environment.Session,
                environment.Factory)
            .RebuildAsync();
        _ = await new ProtectedArchiveCatalogMaintenanceService(
                environment.Session,
                environment.Factory)
            .RebuildAsync(Today);

        JournalDateRange[] ranges =
        [
            new(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 2)),
            new(new DateOnly(2026, 2, 3), new DateOnly(2026, 2, 4)),
        ];
        IReadOnlyList<PendingArchiveSplitSegment> segments = new ArchiveSplitPlanner().Build(
            sourceFileName,
            sourceIdentity.DatabaseId,
            sourceCoverage,
            ranges,
            [sourceFileName],
            Guid.NewGuid);

        return await SplitRepository(environment).StartAsync(
            sourceFileName,
            sourceIdentity.DatabaseId,
            sourceCoverage,
            segments);
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
