using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveExternalPolicyMaintenanceServiceTests
{
    [Fact]
    public async Task ApplyAsync_RemovesDeniedExternalReferencesButPreservesOrdinaryArchivePayloadsAndEventHeaders()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow),
            ("PNG", ClipboardCapturePolicyRule.Allow),
            ("Binary", ClipboardCapturePolicyRule.Allow));
        await environment.Repository.InitializeAsync(initial);

        ArchiveFileName archive = await CreateArchiveAsync(environment, 1, 1, 31);
        Guid textEvent = InsertArchiveInlinePayload(environment, archive, "Text", sourceApplicationId: null);
        Guid pngEvent = InsertArchiveExternalPayload(environment, archive, "PNG", "PngImage", sourceApplicationId: null, shaCharacter: 'a');
        Guid binaryEvent = InsertArchiveExternalPayload(environment, archive, "Binary", "CustomBinary", sourceApplicationId: null, shaCharacter: 'b');

        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Deny),
            ("PNG", ClipboardCapturePolicyRule.Deny),
            ("Binary", ClipboardCapturePolicyRule.Allow));
        await new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory)
            .ApplyAsync(next);

        var service = new ProtectedArchiveExternalPolicyMaintenanceService(environment.Session, environment.Factory);
        ArchiveExternalPolicyMaintenanceResult result = await service.ApplyAsync();

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(1, result.DeletedPayloadCount);
        Assert.Equal(1, result.ArchiveDatabaseCount);
        Assert.Equal(1L, ArchiveScalar(environment, archive, $"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{textEvent:D}';"));
        Assert.Equal(0L, ArchiveScalar(environment, archive, $"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{pngEvent:D}';"));
        Assert.Equal(1L, ArchiveScalar(environment, archive, $"SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE EventId = '{binaryEvent:D}';"));
        Assert.Equal(3L, ArchiveScalar(environment, archive, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));

        using JsonDocument state = JsonDocument.Parse(result.Operation.StateJson);
        Assert.Equal("completed", state.RootElement.GetProperty("currentPhase").GetString());
        Assert.Equal("completed", state.RootElement.GetProperty("archiveExternalReferenceCleanup").GetString());
        Assert.Equal("pending", state.RootElement.GetProperty("catalogRebuild").GetString());
        Assert.Equal("pending", state.RootElement.GetProperty("externalTrashCollection").GetString());
        Assert.Equal("pending", state.RootElement.GetProperty("completion").GetString());
    }

    [Fact]
    public async Task ApplyAsync_ApplicationOverrideNullSourceAndBinaryFormatNamesUseExactEffectivePolicy()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Binary", ClipboardCapturePolicyRule.Allow),
            ("binary", ClipboardCapturePolicyRule.Allow)));

        Guid applicationId = InsertApplicationPolicy(
            environment,
            ClipboardCapturePolicyRule.Inherit,
            ("Binary", ClipboardCapturePolicyRule.Allow));
        ArchiveFileName archive = await CreateArchiveAsync(environment, 2, 2, 28);
        InsertArchiveApplicationIdentity(environment, archive, applicationId);
        Guid appExact = InsertArchiveExternalPayload(environment, archive, "Binary", "CustomBinary", applicationId, 'c');
        Guid nullSource = InsertArchiveExternalPayload(environment, archive, "Binary", "CustomBinary", null, 'd');
        Guid appDifferentCase = InsertArchiveExternalPayload(environment, archive, "binary", "CustomBinary", applicationId, 'e');

        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Binary", ClipboardCapturePolicyRule.Deny));
        await new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory)
            .ApplyAsync(next);

        ArchiveExternalPolicyMaintenanceResult result = await new ProtectedArchiveExternalPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync();

        Assert.Equal(2, result.DeletedPayloadCount);
        Assert.Equal(1L, ArchivePayloadCount(environment, archive, appExact));
        Assert.Equal(0L, ArchivePayloadCount(environment, archive, nullSource));
        Assert.Equal(0L, ArchivePayloadCount(environment, archive, appDifferentCase));
    }

    [Fact]
    public async Task ApplyAsync_ReducedMaxBytesDoesNotRetroactivelyPurgeAllowedArchiveExternalPayload()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy initial = PolicyWithMaxBytes(
            ClipboardCapturePolicyRule.Allow,
            "PNG",
            ClipboardCapturePolicyRule.Allow,
            100);
        await environment.Repository.InitializeAsync(initial);
        ArchiveFileName archive = await CreateArchiveAsync(environment, 3, 1, 31);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, "PNG", "PngImage", null, 'f', canonicalByteCount: 50);

        ClipboardCapturePolicy next = PolicyWithMaxBytes(
            ClipboardCapturePolicyRule.Allow,
            "PNG",
            ClipboardCapturePolicyRule.Allow,
            1);
        await new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory)
            .ApplyAsync(next);

        ArchiveExternalPolicyMaintenanceResult result = await new ProtectedArchiveExternalPolicyMaintenanceService(
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
        ArchiveFileName first = await CreateArchiveAsync(environment, 4, 1, 31);
        ArchiveFileName second = await CreateArchiveAsync(environment, 5, 2, 28);
        Guid firstEvent = InsertArchiveExternalPayload(environment, first, "PNG", "PngImage", null, '1');
        Guid secondEvent = InsertArchiveExternalPayload(environment, second, "PNG", "PngImage", null, '2');

        ClipboardCapturePolicy next = Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Deny));
        CurrentPolicyMaintenanceResult current = await new ProtectedCurrentPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(next);
        ExecuteArchive(environment, second, "DROP INDEX IX_ClipboardHistoryPayload_FormatName;");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedArchiveExternalPolicyMaintenanceService(environment.Session, environment.Factory)
                .ApplyAsync());

        Assert.Equal(1L, ArchivePayloadCount(environment, first, firstEvent));
        Assert.Equal(1L, ArchivePayloadCount(environment, second, secondEvent));
        PendingPolicyMaintenanceOperation? marker = await Pending(environment).ReadAsync();
        Assert.NotNull(marker);
        Assert.Equal(current.Operation.OperationId, marker.OperationId);
        Assert.Equal("pending", ReadState(marker).GetProperty("archiveExternalReferenceCleanup").GetString());
    }

    [Fact]
    public async Task ApplyAsync_CancellationAfterFirstArchiveCommitIsResumableAndRetryCompletesRemainingArchive()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        ArchiveFileName first = await CreateArchiveAsync(environment, 6, 1, 31);
        ArchiveFileName second = await CreateArchiveAsync(environment, 7, 2, 28);
        Guid firstEvent = InsertArchiveExternalPayload(environment, first, "PNG", "PngImage", null, '3');
        Guid secondEvent = InsertArchiveExternalPayload(environment, second, "PNG", "PngImage", null, '4');
        await new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("PNG", ClipboardCapturePolicyRule.Deny)));

        using var cancellation = new CancellationTokenSource();
        int archiveCommits = 0;
        var interrupted = new ProtectedArchiveExternalPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ArchiveExternalPolicyMaintenanceCheckpoint.ArchiveCommitCompleted &&
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
        Assert.Equal("pending", ReadState(pending).GetProperty("archiveExternalReferenceCleanup").GetString());

        ArchiveExternalPolicyMaintenanceResult retry = await new ProtectedArchiveExternalPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync();

        Assert.False(retry.WasAlreadyCompleted);
        Assert.Equal(1, retry.DeletedPayloadCount);
        Assert.Equal(0L, ArchivePayloadCount(environment, first, firstEvent));
        Assert.Equal(0L, ArchivePayloadCount(environment, second, secondEvent));
        Assert.Equal("completed", ReadState(retry.Operation).GetProperty("archiveExternalReferenceCleanup").GetString());
    }

    [Fact]
    public async Task ApplyAsync_LateCancellationAfterMarkerCommitDoesNotDemoteDurableSuccess()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        ArchiveFileName archive = await CreateArchiveAsync(environment, 8, 1, 31);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, "PNG", "PngImage", null, '5');
        await new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("PNG", ClipboardCapturePolicyRule.Deny)));

        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedArchiveExternalPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ArchiveExternalPolicyMaintenanceCheckpoint.AfterMarkerCommit)
                {
                    cancellation.Cancel();
                }
            });

        ArchiveExternalPolicyMaintenanceResult result = await service.ApplyAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(1, result.DeletedPayloadCount);
        Assert.Equal(0L, ArchivePayloadCount(environment, archive, eventId));
        Assert.Equal("completed", ReadState(result.Operation).GetProperty("archiveExternalReferenceCleanup").GetString());
    }

    [Fact]
    public async Task ApplyAsync_ExactRetryAfterCompletedArchivePhaseIsIdempotent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        ArchiveFileName archive = await CreateArchiveAsync(environment, 9, 1, 31);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, "PNG", "PngImage", null, '6');
        await new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("PNG", ClipboardCapturePolicyRule.Deny)));
        var service = new ProtectedArchiveExternalPolicyMaintenanceService(environment.Session, environment.Factory);

        ArchiveExternalPolicyMaintenanceResult first = await service.ApplyAsync();
        environment.Factory.Modes.Clear();
        ArchiveExternalPolicyMaintenanceResult retry = await service.ApplyAsync();

        Assert.False(first.WasAlreadyCompleted);
        Assert.True(retry.WasAlreadyCompleted);
        Assert.Equal(first.Operation, retry.Operation);
        Assert.Equal(0, retry.DeletedPayloadCount);
        Assert.Equal(0, retry.ArchiveDatabaseCount);
        Assert.Equal(0L, ArchivePayloadCount(environment, archive, eventId));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_MalformedMarkerStateFailsClosedBeforeArchiveWrites()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        ArchiveFileName archive = await CreateArchiveAsync(environment, 10, 1, 31);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, "PNG", "PngImage", null, '7');
        CurrentPolicyMaintenanceResult current = await new ProtectedCurrentPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("PNG", ClipboardCapturePolicyRule.Deny)));
        await Pending(environment).UpdateStateAsync(current.Operation.OperationId, "{}");
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedArchiveExternalPolicyMaintenanceService(environment.Session, environment.Factory)
                .ApplyAsync());

        Assert.Equal(1L, ArchivePayloadCount(environment, archive, eventId));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_WithoutGlobalMaintenanceMarkerRejectsBeforeArchiveWrites()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        ArchiveFileName archive = await CreateArchiveAsync(environment, 11, 1, 31);
        Guid eventId = InsertArchiveExternalPayload(environment, archive, "PNG", "PngImage", null, '8');
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedArchiveExternalPolicyMaintenanceService(environment.Session, environment.Factory)
                .ApplyAsync());

        Assert.Equal(1L, ArchivePayloadCount(environment, archive, eventId));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_HoldsSharedMutationLeaseAcrossArchiveObservationAndMarkerCommit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("PNG", ClipboardCapturePolicyRule.Allow)));
        await CreateArchiveAsync(environment, 12, 1, 31);
        await new ProtectedCurrentPolicyMaintenanceService(environment.Session, environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("PNG", ClipboardCapturePolicyRule.Deny)));

        Task<ProtectedStorageMutationLease>? competingAfterAcquire = null;
        Task<ProtectedStorageMutationLease>? competingBeforeMarkerCommit = null;
        var service = new ProtectedArchiveExternalPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ArchiveExternalPolicyMaintenanceCheckpoint.MutationLeaseAcquired)
                {
                    competingAfterAcquire = environment.Session.AcquireMutationLeaseAsync().AsTask();
                    Assert.False(
                        competingAfterAcquire.IsCompleted,
                        "Archive maintenance must own the mutation lease before archive observation.");
                }
                else if (checkpoint == ArchiveExternalPolicyMaintenanceCheckpoint.BeforeMarkerCommit)
                {
                    Assert.NotNull(competingAfterAcquire);
                    Assert.False(
                        competingAfterAcquire.IsCompleted,
                        "Archive maintenance must retain the mutation lease through archive observation and writes.");
                    competingBeforeMarkerCommit = environment.Session.AcquireMutationLeaseAsync().AsTask();
                    Assert.False(
                        competingBeforeMarkerCommit.IsCompleted,
                        "Archive maintenance must retain the mutation lease through marker commit preparation.");
                }
            });

        ArchiveExternalPolicyMaintenanceResult result = await service.ApplyAsync();

        Assert.NotNull(competingAfterAcquire);
        Assert.NotNull(competingBeforeMarkerCommit);
        Assert.False(result.WasAlreadyCompleted);
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
        int startMonth,
        int endDay)
    {
        var fileName = new ArchiveFileName(baseNumber, ArchiveFileName.NoSplit);
        await new ProtectedArchiveDatabaseService(environment.Session, environment.Factory)
            .CreateAsync(
                fileName,
                new JournalDateRange(
                    new DateOnly(2026, startMonth, 1),
                    new DateOnly(2026, startMonth, endDay)));
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

    private static void InsertArchiveApplicationIdentity(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        Guid applicationId) =>
        ExecuteArchive(environment, archive, $"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId:D}', '2026-09-09T00:00:00.0000000+00:00');
            """);

    private static Guid InsertArchiveInlinePayload(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archive,
        string formatName,
        Guid? sourceApplicationId)
    {
        Guid eventId = Guid.NewGuid();
        string sourceSql = sourceApplicationId.HasValue
            ? $"'{sourceApplicationId.Value:D}'"
            : "NULL";
        ExecuteArchive(environment, archive, $"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '{eventId:D}', '2026-01-10T12:00:00.0000000+00:00', 0, 'UTC', '2026-01-10',
                {sourceSql}, NULL, NULL, NULL);
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
        string formatName,
        string payloadKind,
        Guid? sourceApplicationId,
        char shaCharacter,
        long canonicalByteCount = 10)
    {
        Guid eventId = Guid.NewGuid();
        string sourceSql = sourceApplicationId.HasValue
            ? $"'{sourceApplicationId.Value:D}'"
            : "NULL";
        string sha = new string(shaCharacter, 64);
        string extension = payloadKind == "PngImage" ? ".png" : ".bin";
        int month = archive.BaseNumber switch
        {
            1 or 3 or 4 or 6 or 8 or 9 or 10 or 11 or 12 => 1,
            2 or 5 or 7 => 2,
            _ => 1,
        };
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
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
