using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedExternalPayloadCatalogPolicyMaintenanceServiceTests
{
    [Fact]
    public async Task ApplyAsync_RebuildsCatalogAndMarksOnlyCatalogPhaseCompleted()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        PendingPolicyMaintenanceOperation archiveCompleted =
            await PrepareArchiveCompletedMarkerAsync(environment);
        SeedStaleCatalogAddress(environment, 'a');

        var service = new ProtectedExternalPayloadCatalogPolicyMaintenanceService(
            environment.Session,
            environment.Factory);
        ExternalPayloadCatalogPolicyMaintenanceResult result = await service.ApplyAsync();

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(0, result.CatalogAddressCount);
        Assert.Equal(archiveCompleted.OperationId, result.Operation.OperationId);
        Assert.Equal(0L, CatalogAddressCount(environment));

        JsonElement state = ReadState(result.Operation);
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
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        CurrentPolicyMaintenanceResult current = await new ProtectedCurrentPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("Text", ClipboardCapturePolicyRule.Deny)));
        SeedStaleCatalogAddress(environment, 'b');
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedExternalPayloadCatalogPolicyMaintenanceService(
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
        var interrupted = new ProtectedExternalPayloadCatalogPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ExternalPayloadCatalogPolicyMaintenanceCheckpoint.RebuildCompleted)
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

        ExternalPayloadCatalogPolicyMaintenanceResult retry =
            await new ProtectedExternalPayloadCatalogPolicyMaintenanceService(
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
    public async Task ApplyAsync_LateCancellationAfterMarkerCommitDoesNotDemoteDurableSuccess()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareArchiveCompletedMarkerAsync(environment);

        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedExternalPayloadCatalogPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ExternalPayloadCatalogPolicyMaintenanceCheckpoint.AfterMarkerCommit)
                {
                    cancellation.Cancel();
                }
            });

        ExternalPayloadCatalogPolicyMaintenanceResult result =
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
        var service = new ProtectedExternalPayloadCatalogPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        ExternalPayloadCatalogPolicyMaintenanceResult first = await service.ApplyAsync();
        environment.Factory.Modes.Clear();
        ExternalPayloadCatalogPolicyMaintenanceResult retry = await service.ApplyAsync();

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
        PendingPolicyMaintenanceOperation operation =
            await PrepareArchiveCompletedMarkerAsync(environment);
        SeedStaleCatalogAddress(environment, 'd');

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
            new ProtectedExternalPayloadCatalogPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync());

        Assert.Equal(1L, CatalogAddressCount(environment));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    private static async Task<PendingPolicyMaintenanceOperation> PrepareArchiveCompletedMarkerAsync(
        GlobalPolicyTestEnvironment environment)
    {
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        await new ProtectedCurrentPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("Text", ClipboardCapturePolicyRule.Deny)));

        ArchiveExternalPolicyMaintenanceResult archive =
            await new ProtectedArchiveExternalPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync();
        return archive.Operation;
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
            Path.Combine("2026-09-01", sha + ".png"));
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
