using System.Text.Json;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedCurrentApplicationPolicyMaintenanceServiceTests
{
    [Fact]
    public async Task ApplyAsync_AtomicallyPublishesTargetPolicyCleansOnlyTargetCurrentAndLeavesMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow),
            ("PNG", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        ApplicationId target = InsertApplicationIdentity(environment);
        ApplicationId other = InsertApplicationIdentity(environment);
        Guid deniedTargetEvent = InsertInlinePayload(environment, "Text", target);
        Guid allowedTargetEvent = InsertExternalPayload(environment, "PNG", "PngImage", target);
        Guid otherEvent = InsertInlinePayload(environment, "Text", other);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Inherit,
            ("Text", ClipboardCapturePolicyRule.Deny),
            ("PNG", ClipboardCapturePolicyRule.Inherit));
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        CurrentApplicationPolicyMaintenanceResult result = await service.ApplyAsync(target, next);

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(1, result.DeletedPayloadCount);
        Assert.Equal(ProtectedCurrentApplicationPolicyMaintenanceService.OperationKind, result.Operation.OperationKind);
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryEvent WHERE EventId = '{deniedTargetEvent:D}';"));
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{deniedTargetEvent:D}';"));
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{allowedTargetEvent:D}';"));
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{otherEvent:D}';"));
        AssertPolicy(next, await ReadApplicationPolicyAsync(environment, global, target));

        PendingPolicyMaintenanceOperation? marker = await Pending(environment).ReadAsync();
        Assert.Equal(result.Operation, marker);
        using JsonDocument state = JsonDocument.Parse(result.Operation.StateJson);
        Assert.Equal(1, state.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(target.ToString(), state.RootElement.GetProperty("applicationId").GetString());
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
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        ApplicationId target = InsertApplicationIdentity(environment);
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        InsertApplicationPolicy(environment, target, initial);
        Guid eventId = InsertInlinePayload(environment, "Text", target);
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == CurrentApplicationPolicyMaintenanceCheckpoint.ApplicationPolicyPublished)
                {
                    throw new InjectedFailureException();
                }
            });
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAsync<InjectedFailureException>(
            async () => await service.ApplyAsync(target, next));

        AssertPolicy(initial, await ReadApplicationPolicyAsync(environment, global, target));
        Assert.Null(await Pending(environment).ReadAsync());
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_CallerCancellationBeforeCommitRollsBackWholeCurrentPhase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        ApplicationId target = InsertApplicationIdentity(environment);
        Guid eventId = InsertInlinePayload(environment, "Text", target);
        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == CurrentApplicationPolicyMaintenanceCheckpoint.BeforeCommit)
                {
                    cancellation.Cancel();
                }
            });
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ApplyAsync(target, next, cancellation.Token));

        Assert.Null(await ReadApplicationPolicyAsync(environment, global, target));
        Assert.Null(await Pending(environment).ReadAsync());
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_LateCancellationAfterCommitDoesNotDemoteDurableSuccess()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        ApplicationId target = InsertApplicationIdentity(environment);
        Guid eventId = InsertInlinePayload(environment, "Text", target);
        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == CurrentApplicationPolicyMaintenanceCheckpoint.AfterCommit)
                {
                    cancellation.Cancel();
                }
            });
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        CurrentApplicationPolicyMaintenanceResult result =
            await service.ApplyAsync(target, next, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.WasAlreadyCompleted);
        AssertPolicy(next, await ReadApplicationPolicyAsync(environment, global, target));
        Assert.Equal(result.Operation, await Pending(environment).ReadAsync());
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_InheritanceAndBinaryFormatNamesUseExactEffectivePolicyForTargetOnly()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow),
            ("text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        ApplicationId target = InsertApplicationIdentity(environment);
        ApplicationId other = InsertApplicationIdentity(environment);
        Guid targetExact = InsertInlinePayload(environment, "Text", target);
        Guid targetDifferentCase = InsertInlinePayload(environment, "text", target);
        Guid otherExact = InsertInlinePayload(environment, "Text", other);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Inherit,
            ("Text", ClipboardCapturePolicyRule.Deny));
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        CurrentApplicationPolicyMaintenanceResult result = await service.ApplyAsync(target, next);

        Assert.Equal(1, result.DeletedPayloadCount);
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{targetExact:D}';"));
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{targetDifferentCase:D}';"));
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{otherExact:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_ReducedMaxBytesDoesNotRetroactivelyPurgeTargetPayload()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = new(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 100),
            });
        await environment.Repository.InitializeAsync(global);
        ApplicationId target = InsertApplicationIdentity(environment);
        Guid eventId = InsertInlinePayload(environment, "Text", target, canonicalByteCount: 50);
        ClipboardCapturePolicy next = new(
            ClipboardCapturePolicyRule.Inherit,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1),
            });
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        CurrentApplicationPolicyMaintenanceResult result = await service.ApplyAsync(target, next);

        Assert.Equal(0, result.DeletedPayloadCount);
        Assert.Equal(1L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
    }

    [Fact]
    public async Task ApplyAsync_ExactRetryAfterCommittedCurrentPhaseIsIdempotent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        ApplicationId target = InsertApplicationIdentity(environment);
        Guid eventId = InsertInlinePayload(environment, "Text", target);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        CurrentApplicationPolicyMaintenanceResult first = await service.ApplyAsync(target, next);
        CurrentApplicationPolicyMaintenanceResult retry = await service.ApplyAsync(target, next);

        Assert.False(first.WasAlreadyCompleted);
        Assert.True(retry.WasAlreadyCompleted);
        Assert.Equal(first.Operation, retry.Operation);
        Assert.Equal(0, retry.DeletedPayloadCount);
        Assert.Equal(0L, environment.Scalar($"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';"));
        Assert.Equal(1L, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task ApplyAsync_CommittedMarkerRejectsDifferentApplicationOrPolicy()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        ApplicationId firstApplication = InsertApplicationIdentity(environment);
        ApplicationId secondApplication = InsertApplicationIdentity(environment);
        ClipboardCapturePolicy firstPolicy = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Deny));
        ClipboardCapturePolicy secondPolicy = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);
        CurrentApplicationPolicyMaintenanceResult first =
            await service.ApplyAsync(firstApplication, firstPolicy);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.ApplyAsync(secondApplication, firstPolicy));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.ApplyAsync(firstApplication, secondPolicy));

        AssertPolicy(firstPolicy, await ReadApplicationPolicyAsync(environment, global, firstApplication));
        Assert.Null(await ReadApplicationPolicyAsync(environment, global, secondApplication));
        Assert.Equal(first.Operation, await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ApplyAsync_ExistingDifferentPendingOperationRejectsBeforeApplicationWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        ApplicationId target = InsertApplicationIdentity(environment);
        PendingPolicyMaintenanceOperation existing = await Pending(environment).StartAsync(
            "OtherMaintenance",
            "{\"phase\":\"pending\"}");
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.ApplyAsync(target, next));

        Assert.Null(await ReadApplicationPolicyAsync(environment, global, target));
        Assert.Equal(existing, await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ApplyAsync_MissingApplicationIdentityFailsClosedBeforeMarkerOrPolicyWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        ApplicationId missing = ApplicationId.New();
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);
        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Deny,
            ("Text", ClipboardCapturePolicyRule.Deny));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.ApplyAsync(missing, next));

        Assert.Null(await Pending(environment).ReadAsync());
        Assert.Equal(0L, environment.Scalar("SELECT COUNT(*) FROM ApplicationCapturePolicy;"));
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

    private static ApplicationId InsertApplicationIdentity(GlobalPolicyTestEnvironment environment)
    {
        ApplicationId applicationId = ApplicationId.New();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId}', '2026-09-10T00:00:00.0000000+00:00');
            """);
        return applicationId;
    }

    private static void InsertApplicationPolicy(
        GlobalPolicyTestEnvironment environment,
        ApplicationId applicationId,
        ClipboardCapturePolicy policy)
    {
        environment.Execute($"""
            INSERT INTO ApplicationCapturePolicy (ApplicationId, CaptureRule)
            VALUES ('{applicationId}', '{policy.Capture}');
            """);
        foreach ((string formatName, ClipboardFormatCapturePolicy format) in policy.Formats)
        {
            string maxBytes = format.MaxBytes.HasValue ? format.MaxBytes.Value.ToString() : "NULL";
            environment.Execute($"""
                INSERT INTO ApplicationFormatCapturePolicy (
                    ApplicationId, FormatName, CaptureRule, MaxBytes)
                VALUES ('{applicationId}', '{formatName}', '{format.Capture}', {maxBytes});
                """);
        }
    }

    private static async Task<ClipboardCapturePolicy?> ReadApplicationPolicyAsync(
        GlobalPolicyTestEnvironment environment,
        ClipboardCapturePolicy globalPolicy,
        ApplicationId applicationId)
    {
        var repository = new SqliteClipboardCapturePolicyRepository(
            environment.Session,
            globalPolicy,
            environment.Factory);
        return await repository.GetApplicationPolicyAsync(applicationId);
    }

    private static Guid InsertInlinePayload(
        GlobalPolicyTestEnvironment environment,
        string formatName,
        ApplicationId sourceApplicationId,
        long canonicalByteCount = 4)
    {
        Guid eventId = Guid.NewGuid();
        environment.Execute($"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '{eventId:D}', '2026-09-10T00:00:00.0000000+00:00', 0, 'UTC', '2026-09-10',
                '{sourceApplicationId}', NULL, NULL, NULL);
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
        ApplicationId sourceApplicationId)
    {
        Guid eventId = Guid.NewGuid();
        string sha = new string(payloadKind == "PngImage" ? 'A' : 'B', 64);
        string extension = payloadKind == "PngImage" ? ".png" : ".bin";
        environment.Execute($"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '{eventId:D}', '2026-09-10T00:00:00.0000000+00:00', 0, 'UTC', '2026-09-10',
                '{sourceApplicationId}', NULL, NULL, NULL);
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
            VALUES (
                '{eventId:D}', 0, '{formatName}', '{payloadKind}', 10,
                NULL, NULL, '{sha}', '2026/09/10/{sha}{extension}', 10);
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

    private sealed class InjectedFailureException : Exception;
}
