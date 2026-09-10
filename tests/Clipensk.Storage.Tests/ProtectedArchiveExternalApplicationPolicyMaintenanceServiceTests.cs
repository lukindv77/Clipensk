using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveExternalApplicationPolicyMaintenanceServiceTests
{
    [Fact]
    public async Task ApplyAsync_RemovesDeniedTargetExternalOnlyAndPreservesOrdinaryOtherAndNullSources()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow),
            ("PNG", ClipboardCapturePolicyRule.Allow),
            ("Binary", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        DurableApplicationId target = InsertApplicationIdentity(environment);
        DurableApplicationId other = InsertApplicationIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 21, 1);
        InsertArchiveApplicationIdentity(environment, archive, target);
        InsertArchiveApplicationIdentity(environment, archive, other);

        Guid targetText = InsertArchiveInlinePayload(environment, archive, 1, "Text", target);
        Guid targetPng = InsertArchiveExternalPayload(environment, archive, 1, "PNG", "PngImage", target, 'a');
        Guid targetBinary = InsertArchiveExternalPayload(environment, archive, 1, "Binary", "CustomBinary", target, 'b');
        Guid otherPng = InsertArchiveExternalPayload(environment, archive, 1, "PNG", "PngImage", other, 'c');
        Guid nullPng = InsertArchiveExternalPayload(environment, archive, 1, "PNG", "PngImage", null, 'd');

        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Inherit,
            ("Text", ClipboardCapturePolicyRule.Deny),
            ("PNG", ClipboardCapturePolicyRule.Deny),
            ("Binary", ClipboardCapturePolicyRule.Inherit));
        await new ProtectedCurrentApplicationPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(target, next);

        ArchiveExternalApplicationPolicyMaintenanceResult result =
            await new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync();

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(1, result.DeletedPayloadCount);
        Assert.Equal(1, result.ArchiveDatabaseCount);
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, targetText));
        Assert.Equal(0L, ArchivePayloadCount(environment, archive, targetPng));
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, targetBinary));
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, otherPng));
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, nullPng));
        Assert.Equal(5L, ArchiveScalar(environment, archive, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));

        JsonElement state = ReadState(result.Operation);
        Assert.Equal(target.ToString(), state.GetProperty("applicationId").GetString());
        Assert.Equal("completed", state.GetProperty("currentPhase").GetString());
        Assert.Equal("completed", state.GetProperty("archiveExternalReferenceCleanup").GetString());
        Assert.Equal("pending", state.GetProperty("catalogRebuild").GetString());
        Assert.Equal("pending", state.GetProperty("externalTrashCollection").GetString());
        Assert.Equal("pending", state.GetProperty("completion").GetString());
    }

    [Fact]
    public async Task ApplyAsync_BinaryFormatNamesUseExactEffectivePolicyForTargetOnly()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Binary", ClipboardCapturePolicyRule.Allow),
            ("binary", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(global);
        DurableApplicationId target = InsertApplicationIdentity(environment);
        DurableApplicationId other = InsertApplicationIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 22, 2);
        InsertArchiveApplicationIdentity(environment, archive, target);
        InsertArchiveApplicationIdentity(environment, archive, other);
        Guid targetExact = InsertArchiveExternalPayload(environment, archive, 2, "Binary", "CustomBinary", target, 'e');
        Guid targetDifferentCase = InsertArchiveExternalPayload(environment, archive, 2, "binary", "CustomBinary", target, 'f');
        Guid otherExact = InsertArchiveExternalPayload(environment, archive, 2, "Binary", "CustomBinary", other, '1');

        await new ProtectedCurrentApplicationPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(
                target,
                Policy(
                    ClipboardCapturePolicyRule.Inherit,
                    ("Binary", ClipboardCapturePolicyRule.Deny)));

        ArchiveExternalApplicationPolicyMaintenanceResult result =
            await new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync();

        Assert.Equal(1, result.DeletedPayloadCount);
        Assert.Equal(0L, ArchivePayloadCount(environment, archive, targetExact));
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, targetDifferentCase));
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, otherExact));
    }

    [Fact]
    public async Task ApplyAsync_ReducedMaxBytesDoesNotRetroactivelyPurgeTargetExternalPayload()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = PolicyWithMaxBytes(
            ClipboardCapturePolicyRule.Allow,
            "PNG",
            ClipboardCapturePolicyRule.Allow,
            100);
        await environment.Repository.InitializeAsync(global);
        DurableApplicationId target = InsertApplicationIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 23, 3);
        InsertArchiveApplicationIdentity(environment, archive, target);
        Guid eventId = InsertArchiveExternalPayload(
            environment,
            archive,
            3,
            "PNG",
            "PngImage",
            target,
            '2',
            canonicalByteCount: 50);

        ClipboardCapturePolicy next = PolicyWithMaxBytes(
            ClipboardCapturePolicyRule.Inherit,
            "PNG",
            ClipboardCapturePolicyRule.Allow,
            1);
        await new ProtectedCurrentApplicationPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(target, next);

        ArchiveExternalApplicationPolicyMaintenanceResult result =
            await new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync();

        Assert.Equal(0, result.DeletedPayloadCount);
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, eventId));
    }

    [Fact]
    public async Task ApplyAsync_MalformedLaterArchiveFailsPreflightBeforeAnyArchiveMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        DurableApplicationId target = InsertApplicationIdentity(environment);
        ArchiveFileName first = await CreateArchiveAsync(environment, 24, 4);
        ArchiveFileName second = await CreateArchiveAsync(environment, 25, 5);
        InsertArchiveApplicationIdentity(environment, first, target);
        InsertArchiveApplicationIdentity(environment, second, target);
        Guid firstEvent = InsertArchiveExternalPayload(environment, first, 4, "PNG", "PngImage", target, '3');
        Guid secondEvent = InsertArchiveExternalPayload(environment, second, 5, "PNG", "PngImage", target, '4');

        CurrentApplicationPolicyMaintenanceResult current =
            await new ProtectedCurrentApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(
                    target,
                    Policy(
                        ClipboardCapturePolicyRule.Inherit,
                        ("PNG", ClipboardCapturePolicyRule.Deny)));
        ExecuteArchive(environment, second, "DROP INDEX IX_ClipboardHistoryPayload_FormatName;");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync());

        Assert.Equal(1L, ArchivePayloadCount(environment, first, firstEvent));
        Assert.Equal(1L, ArchivePayloadCount(environment, second, secondEvent));
        PendingPolicyMaintenanceOperation? marker = await Pending(environment).ReadAsync();
        Assert.NotNull(marker);
        Assert.Equal(current.Operation.OperationId, marker.OperationId);
        Assert.Equal(
            "pending",
            ReadState(marker).GetProperty("archiveExternalReferenceCleanup").GetString());
    }

    [Fact]
    public async Task ApplyAsync_CancellationAfterFirstArchiveCommitIsResumableAndRetryCompletesRemainingArchive()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        DurableApplicationId target = InsertApplicationIdentity(environment);
        ArchiveFileName first = await CreateArchiveAsync(environment, 26, 6);
        ArchiveFileName second = await CreateArchiveAsync(environment, 27, 7);
        InsertArchiveApplicationIdentity(environment, first, target);
        InsertArchiveApplicationIdentity(environment, second, target);
        Guid firstEvent = InsertArchiveExternalPayload(environment, first, 6, "PNG", "PngImage", target, '5');
        Guid secondEvent = InsertArchiveExternalPayload(environment, second, 7, "PNG", "PngImage", target, '6');
        await new ProtectedCurrentApplicationPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(
                target,
                Policy(
                    ClipboardCapturePolicyRule.Inherit,
                    ("PNG", ClipboardCapturePolicyRule.Deny)));

        using var cancellation = new CancellationTokenSource();
        int archiveCommits = 0;
        var interrupted = new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                        ArchiveExternalApplicationPolicyMaintenanceCheckpoint.ArchiveCommitCompleted &&
                    ++archiveCommits == 1)
                {
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interrupted.ApplyAsync(cancellation.Token));

        Assert.Equal(0L, ArchivePayloadCount(environment, first, firstEvent));
        Assert.Equal(1L, ArchivePayloadCount(environment, second, secondEvent));
        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(
            "pending",
            ReadState(pending).GetProperty("archiveExternalReferenceCleanup").GetString());

        ArchiveExternalApplicationPolicyMaintenanceResult retry =
            await new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync();

        Assert.False(retry.WasAlreadyCompleted);
        Assert.Equal(1, retry.DeletedPayloadCount);
        Assert.Equal(0L, ArchivePayloadCount(environment, first, firstEvent));
        Assert.Equal(0L, ArchivePayloadCount(environment, second, secondEvent));
        Assert.Equal(
            "completed",
            ReadState(retry.Operation).GetProperty("archiveExternalReferenceCleanup").GetString());
    }

    [Fact]
    public async Task ApplyAsync_LateCancellationAfterMarkerCommitDoesNotDemoteDurableSuccess()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        DurableApplicationId target = InsertApplicationIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 28, 8);
        InsertArchiveApplicationIdentity(environment, archive, target);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, 8, "PNG", "PngImage", target, '7');
        await new ProtectedCurrentApplicationPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(
                target,
                Policy(
                    ClipboardCapturePolicyRule.Inherit,
                    ("PNG", ClipboardCapturePolicyRule.Deny)));

        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                    ArchiveExternalApplicationPolicyMaintenanceCheckpoint.AfterMarkerCommit)
                {
                    cancellation.Cancel();
                }
            });

        ArchiveExternalApplicationPolicyMaintenanceResult result =
            await service.ApplyAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(1, result.DeletedPayloadCount);
        Assert.Equal(0L, ArchivePayloadCount(environment, archive, eventId));
        Assert.Equal(
            "completed",
            ReadState(result.Operation).GetProperty("archiveExternalReferenceCleanup").GetString());
    }

    [Fact]
    public async Task ApplyAsync_ExactRetryAfterCompletedArchivePhaseIsIdempotent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        DurableApplicationId target = InsertApplicationIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 29, 9);
        InsertArchiveApplicationIdentity(environment, archive, target);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, 9, "PNG", "PngImage", target, '8');
        await new ProtectedCurrentApplicationPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(
                target,
                Policy(
                    ClipboardCapturePolicyRule.Inherit,
                    ("PNG", ClipboardCapturePolicyRule.Deny)));
        var service = new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        ArchiveExternalApplicationPolicyMaintenanceResult first = await service.ApplyAsync();
        environment.Factory.Modes.Clear();
        ArchiveExternalApplicationPolicyMaintenanceResult retry = await service.ApplyAsync();

        Assert.False(first.WasAlreadyCompleted);
        Assert.True(retry.WasAlreadyCompleted);
        Assert.Equal(first.Operation, retry.Operation);
        Assert.Equal(0, retry.DeletedPayloadCount);
        Assert.Equal(0, retry.ArchiveDatabaseCount);
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
        Assert.Equal(0L, ArchivePayloadCount(environment, archive, eventId));
    }

    [Fact]
    public async Task ApplyAsync_MalformedMarkerStateFailsClosedBeforeArchiveWrites()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        DurableApplicationId target = InsertApplicationIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 30, 10);
        InsertArchiveApplicationIdentity(environment, archive, target);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, 10, "PNG", "PngImage", target, '9');
        CurrentApplicationPolicyMaintenanceResult current =
            await new ProtectedCurrentApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(
                    target,
                    Policy(
                        ClipboardCapturePolicyRule.Inherit,
                        ("PNG", ClipboardCapturePolicyRule.Deny)));
        await Pending(environment).UpdateStateAsync(current.Operation.OperationId, "{}");
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync());

        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, eventId));
    }

    [Fact]
    public async Task ApplyAsync_GlobalMaintenanceMarkerRejectsBeforeArchiveWrites()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        DurableApplicationId target = InsertApplicationIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 31, 11);
        InsertArchiveApplicationIdentity(environment, archive, target);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, 11, "PNG", "PngImage", target, 'a');
        await new ProtectedCurrentPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("PNG", ClipboardCapturePolicyRule.Deny)));
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync());

        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, eventId));
    }

    [Fact]
    public async Task ApplyAsync_PersistedApplicationPolicyFingerprintMismatchFailsBeforeArchiveWrites()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        DurableApplicationId target = InsertApplicationIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 32, 12);
        InsertArchiveApplicationIdentity(environment, archive, target);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, 12, "PNG", "PngImage", target, 'b');
        await new ProtectedCurrentApplicationPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(
                target,
                Policy(
                    ClipboardCapturePolicyRule.Deny,
                    ("PNG", ClipboardCapturePolicyRule.Deny)));
        environment.Execute($"""
            UPDATE ApplicationCapturePolicy
            SET CaptureRule = 'Allow'
            WHERE ApplicationId = '{target}';
            """);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync());

        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, eventId));
    }

    [Fact]
    public async Task ApplyAsync_HoldsSharedMutationLeaseAcrossArchiveObservationAndMarkerCommit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        DurableApplicationId target = InsertApplicationIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 33, 1);
        InsertArchiveApplicationIdentity(environment, archive, target);
        await new ProtectedCurrentApplicationPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(
                target,
                Policy(
                    ClipboardCapturePolicyRule.Inherit,
                    ("PNG", ClipboardCapturePolicyRule.Deny)));

        Task<ProtectedStorageMutationLease>? competingAfterAcquire = null;
        Task<ProtectedStorageMutationLease>? competingBeforeMarkerCommit = null;
        var service = new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                    ArchiveExternalApplicationPolicyMaintenanceCheckpoint.MutationLeaseAcquired)
                {
                    competingAfterAcquire = environment.Session.AcquireMutationLeaseAsync().AsTask();
                    Assert.False(competingAfterAcquire.IsCompleted);
                }
                else if (checkpoint ==
                         ArchiveExternalApplicationPolicyMaintenanceCheckpoint.BeforeMarkerCommit)
                {
                    Assert.NotNull(competingAfterAcquire);
                    Assert.False(competingAfterAcquire.IsCompleted);
                    competingBeforeMarkerCommit = environment.Session.AcquireMutationLeaseAsync().AsTask();
                    Assert.False(competingBeforeMarkerCommit.IsCompleted);
                }
            });

        ArchiveExternalApplicationPolicyMaintenanceResult result = await service.ApplyAsync();

        Assert.False(result.WasAlreadyCompleted);
        Assert.NotNull(competingAfterAcquire);
        Assert.NotNull(competingBeforeMarkerCommit);
        using (ProtectedStorageMutationLease acquired = await competingAfterAcquire)
        {
        }
        using (ProtectedStorageMutationLease acquired = await competingBeforeMarkerCommit)
        {
        }
    }

    private static async Task<ArchiveFileName> CreateArchiveAsync(
        GlobalPolicyTestEnvironment environment,
        int baseNumber,
        int month)
    {
        var fileName = new ArchiveFileName(baseNumber, ArchiveFileName.NoSplit);
        await new ProtectedArchiveDatabaseService(environment.Session, environment.Factory)
            .CreateAsync(
                fileName,
                new JournalDateRange(
                    new DateOnly(2026, month, 1),
                    new DateOnly(2026, month, DateTime.DaysInMonth(2026, month))));
        return fileName;
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

    private static ClipboardCapturePolicy PolicyWithMaxBytes(
        ClipboardCapturePolicyRule capture,
        string formatName,
        ClipboardCapturePolicyRule formatRule,
        long maxBytes) =>
        new(
            capture,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                [formatName] = new(formatRule, maxBytes),
            });

    private static DurableApplicationId InsertApplicationIdentity(
        GlobalPolicyTestEnvironment environment)
    {
        DurableApplicationId applicationId = DurableApplicationId.New();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId}', '2026-09-10T00:00:00.0000000+00:00');
            """);
        return applicationId;
    }

    private static void InsertArchiveApplicationIdentity(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        DurableApplicationId applicationId) =>
        ExecuteArchive(environment, archive, $"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId}', '2026-09-10T00:00:00.0000000+00:00');
            """);

    private static Guid InsertArchiveInlinePayload(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        int month,
        string formatName,
        DurableApplicationId sourceApplicationId)
    {
        Guid eventId = Guid.NewGuid();
        string calendarDate = $"2026-{month:00}-10";
        ExecuteArchive(environment, archive, $"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '{eventId:D}', '{calendarDate}T12:00:00.0000000+00:00', 0, 'UTC', '{calendarDate}',
                '{sourceApplicationId}', NULL, NULL, NULL);
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
            VALUES (
                '{eventId:D}', 0, '{formatName}', 'Text', 4,
                'text', 'text', NULL, NULL, NULL);
            """);
        return eventId;
    }

    private static Guid InsertArchiveExternalPayload(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        int month,
        string formatName,
        string payloadKind,
        DurableApplicationId? sourceApplicationId,
        char shaCharacter,
        long canonicalByteCount = 10)
    {
        Guid eventId = Guid.NewGuid();
        string sourceSql = sourceApplicationId is null
            ? "NULL"
            : $"'{sourceApplicationId}'";
        string sha = new string(shaCharacter, 64);
        string extension = payloadKind == "PngImage" ? ".png" : ".bin";
        string calendarDate = $"2026-{month:00}-10";
        string relativePath = Path.Combine(calendarDate, sha + extension).Replace('\\', '/');
        ExecuteArchive(environment, archive, $"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '{eventId:D}', '{calendarDate}T12:00:00.0000000+00:00', 0, 'UTC', '{calendarDate}',
                {sourceSql}, NULL, NULL, NULL);
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
            VALUES (
                '{eventId:D}', 0, '{formatName}', '{payloadKind}', {canonicalByteCount},
                NULL, NULL, '{sha}', '{relativePath}', {canonicalByteCount});
            """);
        return eventId;
    }

    private static void ExecuteArchive(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            ArchivePath(environment, archive),
            environment.Key,
            SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ArchiveScalar(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            ArchivePath(environment, archive),
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long ArchivePayloadCount(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        Guid eventId) =>
        ArchiveScalar(
            environment,
            archive,
            $"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{eventId:D}';");

    private static string ArchivePath(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive) =>
        Path.Combine(environment.Root, "Archive", archive.FileName);

    private static SqlitePendingPolicyMaintenanceRepository Pending(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static JsonElement ReadState(PendingPolicyMaintenanceOperation operation) =>
        JsonDocument.Parse(operation.StateJson).RootElement.Clone();
}
