using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceServiceTests
{
    [Fact]
    public async Task ApplyAsync_RebuildsCatalogAndMarksOnlyApplicationCatalogPhaseCompleted()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (DurableApplicationId applicationId, PendingPolicyMaintenanceOperation archiveCompleted) =
            await PrepareArchiveCompletedMarkerAsync(environment);
        SeedStaleCatalogAddress(environment, 'a');

        var service = new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);
        ExternalPayloadCatalogApplicationPolicyMaintenanceResult result = await service.ApplyAsync();

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(0, result.CatalogAddressCount);
        Assert.Equal(archiveCompleted.OperationId, result.Operation.OperationId);
        Assert.Equal(0L, CatalogAddressCount(environment));

        JsonElement state = ReadState(result.Operation);
        Assert.Equal(applicationId.ToString(), state.GetProperty("applicationId").GetString());
        Assert.Equal("completed", state.GetProperty("currentPhase").GetString());
        Assert.Equal(
            "completed",
            state.GetProperty("archiveExternalReferenceCleanup").GetString());
        Assert.Equal("completed", state.GetProperty("catalogRebuild").GetString());
        Assert.Equal("pending", state.GetProperty("externalTrashCollection").GetString());
        Assert.Equal("pending", state.GetProperty("completion").GetString());
    }

    [Fact]
    public async Task ApplyAsync_ArchivePendingFailsBeforeCatalogMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(GlobalPolicy());
        DurableApplicationId applicationId = InsertApplicationIdentity(environment);
        CurrentApplicationPolicyMaintenanceResult current =
            await new ProtectedCurrentApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(applicationId, ApplicationPolicy());
        SeedStaleCatalogAddress(environment, 'b');
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync());

        Assert.Equal(1L, CatalogAddressCount(environment));
        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(current.Operation.OperationId, pending!.OperationId);
        Assert.Equal("pending", ReadState(pending).GetProperty("catalogRebuild").GetString());
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_CancellationAfterRebuildIsResumableAndRetryCompletesMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareArchiveCompletedMarkerAsync(environment);
        SeedStaleCatalogAddress(environment, 'c');

        using var cancellation = new CancellationTokenSource();
        var interrupted = new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                    ExternalPayloadCatalogApplicationPolicyMaintenanceCheckpoint.RebuildCompleted)
                {
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interrupted.ApplyAsync(cancellation.Token));

        Assert.Equal(0L, CatalogAddressCount(environment));
        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal("pending", ReadState(pending!).GetProperty("catalogRebuild").GetString());

        ExternalPayloadCatalogApplicationPolicyMaintenanceResult retry =
            await new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync();

        Assert.False(retry.WasAlreadyCompleted);
        Assert.Equal(0, retry.CatalogAddressCount);
        Assert.Equal(
            "completed",
            ReadState(retry.Operation).GetProperty("catalogRebuild").GetString());
    }

    [Fact]
    public async Task ApplyAsync_ApplicationPolicyTamperAfterRebuildFailsBeforeMarkerCommit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (DurableApplicationId applicationId, _) =
            await PrepareArchiveCompletedMarkerAsync(environment);
        SeedStaleCatalogAddress(environment, 'd');

        var service = new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                    ExternalPayloadCatalogApplicationPolicyMaintenanceCheckpoint.RebuildCompleted)
                {
                    environment.Execute($"""
                        UPDATE ApplicationFormatCapturePolicy
                        SET CaptureRule = 'Allow'
                        WHERE ApplicationId = '{applicationId}' COLLATE BINARY
                          AND FormatName = 'Text' COLLATE BINARY;
                        """);
                }
            });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ApplyAsync());

        // Rebuild is independently durable, but marker publication must refuse to claim success
        // after the target application policy no longer matches the durable fingerprint.
        Assert.Equal(0L, CatalogAddressCount(environment));
        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal("pending", ReadState(pending!).GetProperty("catalogRebuild").GetString());
    }

    [Fact]
    public async Task ApplyAsync_LateCancellationAfterMarkerCommitDoesNotDemoteDurableSuccess()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareArchiveCompletedMarkerAsync(environment);

        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                    ExternalPayloadCatalogApplicationPolicyMaintenanceCheckpoint.AfterMarkerCommit)
                {
                    cancellation.Cancel();
                }
            });

        ExternalPayloadCatalogApplicationPolicyMaintenanceResult result =
            await service.ApplyAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(
            "completed",
            ReadState(result.Operation).GetProperty("catalogRebuild").GetString());
    }

    [Fact]
    public async Task ApplyAsync_ExactRetryAfterCompletedCatalogPhaseIsIdempotent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareArchiveCompletedMarkerAsync(environment);
        var service = new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        ExternalPayloadCatalogApplicationPolicyMaintenanceResult first = await service.ApplyAsync();
        environment.Factory.Modes.Clear();
        ExternalPayloadCatalogApplicationPolicyMaintenanceResult retry = await service.ApplyAsync();

        Assert.False(first.WasAlreadyCompleted);
        Assert.True(retry.WasAlreadyCompleted);
        Assert.Equal(first.Operation, retry.Operation);
        Assert.Equal(0, retry.CatalogAddressCount);
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_FingerprintMismatchFailsBeforeCatalogMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (_, PendingPolicyMaintenanceOperation operation) =
            await PrepareArchiveCompletedMarkerAsync(environment);
        SeedStaleCatalogAddress(environment, 'e');

        JsonElement state = ReadState(operation);
        string fingerprint = state.GetProperty("policyFingerprint").GetString()!;
        string replacement = string.Equals(
            fingerprint,
            new string('F', 64),
            StringComparison.Ordinal)
            ? new string('E', 64)
            : new string('F', 64);
        string tamperedState = operation.StateJson.Replace(fingerprint, replacement);
        await Pending(environment).UpdateStateAsync(operation.OperationId, tamperedState);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync());

        Assert.Equal(1L, CatalogAddressCount(environment));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_MissingTargetApplicationPolicyFailsBeforeCatalogMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (DurableApplicationId applicationId, _) =
            await PrepareArchiveCompletedMarkerAsync(environment);
        SeedStaleCatalogAddress(environment, 'f');
        environment.Execute($"""
            DELETE FROM ApplicationCapturePolicy
            WHERE ApplicationId = '{applicationId}' COLLATE BINARY;
            """);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync());

        Assert.Equal(1L, CatalogAddressCount(environment));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_GlobalMaintenanceMarkerIsRejectedBeforeCatalogMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(GlobalPolicy());
        await new ProtectedCurrentPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("Text", ClipboardCapturePolicyRule.Deny)));
        SeedStaleCatalogAddress(environment, '1');
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync());

        Assert.Equal(1L, CatalogAddressCount(environment));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    private static async Task<(DurableApplicationId ApplicationId, PendingPolicyMaintenanceOperation Operation)>
        PrepareArchiveCompletedMarkerAsync(GlobalPolicyTestEnvironment environment)
    {
        await environment.Repository.InitializeAsync(GlobalPolicy());
        DurableApplicationId applicationId = InsertApplicationIdentity(environment);
        await new ProtectedCurrentApplicationPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(applicationId, ApplicationPolicy());

        ArchiveExternalApplicationPolicyMaintenanceResult archive =
            await new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync();
        return (applicationId, archive.Operation);
    }

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

    private static ClipboardCapturePolicy GlobalPolicy() =>
        Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow));

    private static ClipboardCapturePolicy ApplicationPolicy() =>
        Policy(
            ClipboardCapturePolicyRule.Inherit,
            ("Text", ClipboardCapturePolicyRule.Deny));

    private static ClipboardCapturePolicy Policy(
        ClipboardCapturePolicyRule capture,
        params (string Name, ClipboardCapturePolicyRule Rule)[] formats) =>
        new(
            capture,
            formats.ToDictionary(
                item => item.Name,
                item => new ClipboardFormatCapturePolicy(item.Rule),
                StringComparer.Ordinal));

    private static void SeedStaleCatalogAddress(
        GlobalPolicyTestEnvironment environment,
        char shaCharacter)
    {
        string sha = new(shaCharacter, 64);
        using SqliteConnection connection = environment.Factory.Open(
            environment.CatalogPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ExternalPayloadAddressIndex (
                Sha256,
                RelativePath,
                SizeBytes)
            VALUES ($sha256, $relativePath, 1);
            """;
        command.Parameters.AddWithValue("$sha256", sha);
        command.Parameters.AddWithValue(
            "$relativePath",
            Path.Combine("2026-09-10", sha + ".png"));
        command.ExecuteNonQuery();
    }

    private static long CatalogAddressCount(GlobalPolicyTestEnvironment environment) =>
        environment.Scalar(
            "SELECT COUNT(*) FROM ExternalPayloadAddressIndex;",
            catalog: true);

    private static SqlitePendingPolicyMaintenanceRepository Pending(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static JsonElement ReadState(PendingPolicyMaintenanceOperation operation)
    {
        using JsonDocument document = JsonDocument.Parse(operation.StateJson);
        return document.RootElement.Clone();
    }
}
