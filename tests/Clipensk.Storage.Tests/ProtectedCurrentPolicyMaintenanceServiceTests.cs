using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedCurrentPolicyMaintenanceServiceTests
{
    [Fact]
    public async Task ApplyAsync_AtomicallyPublishesPolicyCleansCurrentAndLeavesContinuationMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow),
            ("PNG", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(initial);

        Guid deniedEvent = InsertInlinePayload(environment, "Text", sourceApplicationId: null);
        Guid allowedEvent = InsertExternalPayload(environment, "PNG", "PngImage", sourceApplicationId: null);
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Deny),
            ("PNG", ClipboardCapturePolicyRule.Allow));

        CurrentPolicyMaintenanceResult result = await service.ApplyAsync(next);

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(1, result.DeletedPayloadCount);
        Assert.NotEqual(Guid.Empty, result.Operation.OperationId);
        Assert.Equal(ProtectedCurrentPolicyMaintenanceService.OperationKind, result.Operation.OperationKind);
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryEvent WHERE EventId = '{deniedEvent:D}';"));
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{deniedEvent:D}';"));
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{allowedEvent:D}';"));

        ClipboardCapturePolicy? persisted = await environment.Repository.ReadAsync();
        AssertPolicy(next, persisted);
        PendingPolicyMaintenanceOperation? marker = await Pending(environment).ReadAsync();
        Assert.Equal(result.Operation, marker);

        using JsonDocument state = JsonDocument.Parse(result.Operation.StateJson);
        Assert.Equal(1, state.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("completed", state.RootElement.GetProperty("currentPhase").GetString());
        Assert.Equal("pending", state.RootElement.GetProperty("archiveExternalReferenceCleanup").GetString());
        Assert.Equal("pending", state.RootElement.GetProperty("catalogRebuild").GetString());
        Assert.Equal("pending", state.RootElement.GetProperty("externalTrashCollection").GetString());
        Assert.Equal("pending", state.RootElement.GetProperty("completion").GetString());
    }

    [Fact]
    public async Task ApplyAsync_InjectedFailureRollsBackMarkerPolicyAndCleanupTogether()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(initial);
        Guid eventId = InsertInlinePayload(environment, "Text", sourceApplicationId: null);
        var service = new ProtectedCurrentPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == CurrentPolicyMaintenanceCheckpoint.GlobalPolicyPublished)
                {
                    throw new InjectedFailureException();
                }
            });
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAsync<InjectedFailureException>(async () => await service.ApplyAsync(next));

        AssertPolicy(initial, await environment.Repository.ReadAsync());
        Assert.Null(await Pending(environment).ReadAsync());
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_CallerCancellationBeforeCommitRollsBackWholeCurrentPhase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(initial);
        Guid eventId = InsertInlinePayload(environment, "Text", sourceApplicationId: null);
        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedCurrentPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == CurrentPolicyMaintenanceCheckpoint.BeforeCommit)
                {
                    cancellation.Cancel();
                }
            });
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ApplyAsync(next, cancellation.Token));

        AssertPolicy(initial, await environment.Repository.ReadAsync());
        Assert.Null(await Pending(environment).ReadAsync());
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_SessionRevocationBeforeCommitRollsBackWholeCurrentPhase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(initial);
        Guid eventId = InsertInlinePayload(environment, "Text", sourceApplicationId: null);
        bool lockStarted = false;
        var service = new ProtectedCurrentPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == CurrentPolicyMaintenanceCheckpoint.BeforeCommit)
                {
                    lockStarted = environment.Lifecycle.TryBeginLock();
                }
            });
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyAsync(next));
        Assert.True(lockStarted);

        environment.ReopenSession();
        AssertPolicy(initial, await environment.Repository.ReadAsync());
        Assert.Null(await Pending(environment).ReadAsync());
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_LateCancellationAfterCommitDoesNotDemoteDurableSuccess()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(initial);
        Guid eventId = InsertInlinePayload(environment, "Text", sourceApplicationId: null);
        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedCurrentPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == CurrentPolicyMaintenanceCheckpoint.AfterCommit)
                {
                    cancellation.Cancel();
                }
            });
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        CurrentPolicyMaintenanceResult result = await service.ApplyAsync(next, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.WasAlreadyCompleted);
        AssertPolicy(next, await environment.Repository.ReadAsync());
        Assert.Equal(result.Operation, await Pending(environment).ReadAsync());
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_GlobalDenyWithApplicationAllowPreservesAllowedApplicationPayload()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        Guid applicationId = InsertApplicationPolicy(
            environment,
            ClipboardCapturePolicyRule.Allow);
        Guid applicationEvent = InsertInlinePayload(environment, "Text", applicationId);
        Guid unknownSourceEvent = InsertInlinePayload(environment, "Text", sourceApplicationId: null);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Allow));
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);

        CurrentPolicyMaintenanceResult result = await service.ApplyAsync(next);

        Assert.Equal(1, result.DeletedPayloadCount);
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{applicationEvent:D}';"));
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{unknownSourceEvent:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_ApplicationInheritanceAndBinaryFormatNamesUseExactEffectivePolicy()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow),
            ("text", ClipboardCapturePolicyRule.Allow)));
        Guid applicationId = InsertApplicationPolicy(
            environment,
            ClipboardCapturePolicyRule.Inherit,
            ("Text", ClipboardCapturePolicyRule.Allow));
        Guid applicationExactEvent = InsertInlinePayload(environment, "Text", applicationId);
        Guid applicationDifferentCaseEvent = InsertInlinePayload(environment, "text", applicationId);
        Guid globalOnlyEvent = InsertInlinePayload(environment, "Text", sourceApplicationId: null);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Deny));
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);

        CurrentPolicyMaintenanceResult result = await service.ApplyAsync(next);

        Assert.Equal(2, result.DeletedPayloadCount);
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{applicationExactEvent:D}';"));
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{applicationDifferentCaseEvent:D}';"));
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{globalOnlyEvent:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_ExternalCurrentPayloadIsRemovedButEventHeaderRemains()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Image", ClipboardCapturePolicyRule.Allow),
            ("Binary", ClipboardCapturePolicyRule.Allow)));
        Guid imageEvent = InsertExternalPayload(environment, "Image", "PngImage", sourceApplicationId: null);
        Guid binaryEvent = InsertExternalPayload(environment, "Binary", "CustomBinary", sourceApplicationId: null);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Image", ClipboardCapturePolicyRule.Deny),
            ("Binary", ClipboardCapturePolicyRule.Deny));
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);

        CurrentPolicyMaintenanceResult result = await service.ApplyAsync(next);

        Assert.Equal(2, result.DeletedPayloadCount);
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryEvent WHERE EventId = '{imageEvent:D}';"));
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryEvent WHERE EventId = '{binaryEvent:D}';"));
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId IN ('{imageEvent:D}', '{binaryEvent:D}');"));
    }

    [Fact]
    public async Task ApplyAsync_ReducedMaxBytesDoesNotRetroactivelyPurgePayload()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = new(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 100),
            });
        await environment.Repository.InitializeAsync(initial);
        Guid eventId = InsertInlinePayload(environment, "Text", sourceApplicationId: null, canonicalByteCount: 50);
        ClipboardCapturePolicy next = new(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1),
            });
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);

        CurrentPolicyMaintenanceResult result = await service.ApplyAsync(next);

        Assert.Equal(0, result.DeletedPayloadCount);
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_ExactRetryAfterCommittedCurrentPhaseIsIdempotent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        Guid eventId = InsertInlinePayload(environment, "Text", sourceApplicationId: null);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);

        CurrentPolicyMaintenanceResult first = await service.ApplyAsync(next);
        CurrentPolicyMaintenanceResult retry = await service.ApplyAsync(next);

        Assert.False(first.WasAlreadyCompleted);
        Assert.True(retry.WasAlreadyCompleted);
        Assert.Equal(first.Operation, retry.Operation);
        Assert.Equal(0, retry.DeletedPayloadCount);
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
        Assert.Equal(1L, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task ApplyAsync_ExistingDifferentPendingOperationRejectsSecondOperation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(initial);
        PendingPolicyMaintenanceOperation existing = await Pending(environment).StartAsync(
            "OtherMaintenance",
            "{\"phase\":\"pending\"}");
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.ApplyAsync(next));

        AssertPolicy(initial, await environment.Repository.ReadAsync());
        Assert.Equal(existing, await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ApplyAsync_CommittedMarkerRejectsDifferentRequestedPolicy()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        ClipboardCapturePolicy firstPolicy = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Deny));
        ClipboardCapturePolicy secondPolicy = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);
        CurrentPolicyMaintenanceResult first = await service.ApplyAsync(firstPolicy);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.ApplyAsync(secondPolicy));

        AssertPolicy(firstPolicy, await environment.Repository.ReadAsync());
        Assert.Equal(first.Operation, await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ApplyAsync_MalformedCommittedStateFailsClosed()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Deny));
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);
        CurrentPolicyMaintenanceResult first = await service.ApplyAsync(next);
        await Pending(environment).UpdateStateAsync(first.Operation.OperationId, "{}");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.ApplyAsync(next));

        AssertPolicy(next, await environment.Repository.ReadAsync());
        Assert.Equal(1L, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task ApplyAsync_NonCanonicalPersistedSourceApplicationIdFailsClosedBeforeWrites()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(initial);
        string upperApplicationId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{upperApplicationId}', '2026-09-09T00:00:00.0000000+00:00');
            """);
        _ = InsertInlinePayload(environment, "Text", Guid.Parse(upperApplicationId), persistedSourceApplicationId: upperApplicationId);
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.ApplyAsync(next));

        AssertPolicy(initial, await environment.Repository.ReadAsync());
        Assert.Null(await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ApplyAsync_InvalidCurrentSchemaFailsClosedBeforeMarkerOrPolicyWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(initial);
        environment.Execute("DROP INDEX IX_ClipboardHistoryPayload_FormatName;");
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.ApplyAsync(next));

        Assert.Equal("Allow", ReadGlobalCaptureRule(environment));
        Assert.Equal(0L, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task ApplyAsync_HoldsSharedMutationLeaseAcrossCurrentObservationAndCommit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        using var entered = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);
        var service = new ProtectedCurrentPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == CurrentPolicyMaintenanceCheckpoint.MutationLeaseAcquired)
                {
                    entered.Set();
                    release.Wait();
                }
            });
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));

        Task<CurrentPolicyMaintenanceResult> maintenance = service.ApplyAsync(next);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Task<ProtectedStorageMutationLease> competingMutation =
            environment.Session.AcquireMutationLeaseAsync().AsTask();

        try
        {
            Assert.False(competingMutation.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await maintenance;
        using ProtectedStorageMutationLease acquiredAfterMaintenance = await competingMutation;
    }

    [Fact]
    public async Task ApplyAsync_InvalidNewGlobalPolicyFailsBeforeOpeningStorage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        environment.Factory.Modes.Clear();
        var service = new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory);
        var invalid = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Inherit,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow),
            });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await service.ApplyAsync(invalid));

        Assert.Empty(environment.Factory.Modes);
        Assert.Equal(0L, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    private static SqlitePendingPolicyMaintenanceRepository Pending(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static ClipboardCapturePolicy Policy(
        ClipboardCapturePolicyRule capture,
        params (string Name, ClipboardCapturePolicyRule Rule)[] formats)
    {
        return new ClipboardCapturePolicy(
            capture,
            formats.ToDictionary(
                item => item.Name,
                item => new ClipboardFormatCapturePolicy(item.Rule),
                StringComparer.Ordinal));
    }

    private static Guid InsertApplicationPolicy(
        GlobalPolicyTestEnvironment environment,
        ClipboardCapturePolicyRule capture,
        params (string Name, ClipboardCapturePolicyRule Rule)[] formats)
    {
        Guid applicationId = Guid.NewGuid();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId:D}', '2026-09-09T00:00:00.0000000+00:00');
            INSERT INTO ApplicationCapturePolicy (ApplicationId, CaptureRule)
            VALUES ('{applicationId:D}', '{capture}');
            """);
        foreach ((string formatName, ClipboardCapturePolicyRule rule) in formats)
        {
            environment.Execute($"""
                INSERT INTO ApplicationFormatCapturePolicy (
                    ApplicationId, FormatName, CaptureRule, MaxBytes)
                VALUES ('{applicationId:D}', '{formatName}', '{rule}', NULL);
                """);
        }
        return applicationId;
    }

    private static Guid InsertInlinePayload(
        GlobalPolicyTestEnvironment environment,
        string formatName,
        Guid? sourceApplicationId,
        long canonicalByteCount = 4,
        string? persistedSourceApplicationId = null)
    {
        Guid eventId = Guid.NewGuid();
        string sourceSql = sourceApplicationId.HasValue
            ? $"'{persistedSourceApplicationId ?? sourceApplicationId.Value.ToString("D")}'"
            : "NULL";
        environment.Execute($"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '{eventId:D}', '2026-09-08T12:00:00.0000000+00:00', 0, 'UTC', '2026-09-08',
                {sourceSql}, NULL, NULL, NULL);
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
            VALUES (
                '{eventId:D}', 0, '{formatName}', 'Text', {canonicalByteCount},
                'text', 'text', NULL, NULL, NULL);
            """);
        return eventId;
    }

    private static Guid InsertExternalPayload(
        GlobalPolicyTestEnvironment environment,
        string formatName,
        string payloadKind,
        Guid? sourceApplicationId)
    {
        Guid eventId = Guid.NewGuid();
        string sourceSql = sourceApplicationId.HasValue
            ? $"'{sourceApplicationId.Value:D}'"
            : "NULL";
        string sha = new string(payloadKind == "PngImage" ? 'A' : 'B', 64);
        string extension = payloadKind == "PngImage" ? ".png" : ".bin";
        environment.Execute($"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '{eventId:D}', '2026-09-08T12:00:00.0000000+00:00', 0, 'UTC', '2026-09-08',
                {sourceSql}, NULL, NULL, NULL);
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
            VALUES (
                '{eventId:D}', 0, '{formatName}', '{payloadKind}', 10,
                NULL, NULL, '{sha}', '2026/09/08/{sha}{extension}', 10);
            """);
        return eventId;
    }

    private static void AssertPolicy(
        ClipboardCapturePolicy expected,
        ClipboardCapturePolicy? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Capture, actual.Capture);
        Assert.Equal(expected.Formats.Count, actual.Formats.Count);
        foreach ((string formatName, ClipboardFormatCapturePolicy expectedFormat) in expected.Formats)
        {
            Assert.True(actual.Formats.TryGetValue(formatName, out ClipboardFormatCapturePolicy actualFormat));
            Assert.Equal(expectedFormat, actualFormat);
        }
    }

    private static string ReadGlobalCaptureRule(GlobalPolicyTestEnvironment environment)
    {
        using var connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CaptureRule FROM GlobalCapturePolicy WHERE SingletonId = 1;";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private sealed class InjectedFailureException : Exception;
}