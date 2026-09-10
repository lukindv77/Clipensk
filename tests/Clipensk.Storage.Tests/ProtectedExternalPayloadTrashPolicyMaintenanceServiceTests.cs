using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedExternalPayloadTrashPolicyMaintenanceServiceTests
{
    [Fact]
    public async Task ApplyAsync_CollectsOrphanAndMarksOnlyExternalTrashPhaseCompleted()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        PendingPolicyMaintenanceOperation catalogCompleted =
            await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly storedDate = new(2026, 9, 1);
        DateOnly deletionDate = new(2026, 9, 10);
        byte[] bytes = [1, 2, 3, 4, 5];
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            storedDate,
            deletionDate,
            bytes);

        var service = new ProtectedExternalPayloadTrashPolicyMaintenanceService(
            environment.Session,
            environment.Factory);
        ExternalPayloadTrashPolicyMaintenanceResult result =
            await service.ApplyAsync(deletionDate);

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(catalogCompleted.OperationId, result.Operation.OperationId);
        Assert.Equal(1, result.CollectedFileCount);
        Assert.Equal([orphan.TrashRelativePath], result.TrashRelativePaths);
        Assert.False(File.Exists(orphan.SourcePath));
        Assert.Equal(bytes, File.ReadAllBytes(orphan.TrashPath));

        JsonElement state = ReadState(result.Operation);
        Assert.Equal("completed", state.GetProperty("currentPhase").GetString());
        Assert.Equal(
            "completed",
            state.GetProperty("archiveExternalReferenceCleanup").GetString());
        Assert.Equal("completed", state.GetProperty("catalogRebuild").GetString());
        Assert.Equal("completed", state.GetProperty("externalTrashCollection").GetString());
        Assert.Equal("pending", state.GetProperty("completion").GetString());
    }

    [Fact]
    public async Task ApplyAsync_CatalogPendingFailsBeforeFilesystemMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareArchiveCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 2),
            deletionDate,
            [10, 20, 30]);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedExternalPayloadTrashPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(deletionDate));

        Assert.True(File.Exists(orphan.SourcePath));
        Assert.False(File.Exists(orphan.TrashPath));
        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(
            "pending",
            ReadState(pending!).GetProperty("externalTrashCollection").GetString());
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_CancellationAfterCollectionIsResumableAndRetryCompletesMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        byte[] bytes = [7, 8, 9, 10];
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 3),
            deletionDate,
            bytes);

        using var cancellation = new CancellationTokenSource();
        var interrupted = new ProtectedExternalPayloadTrashPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ExternalPayloadTrashPolicyMaintenanceCheckpoint.CollectionCompleted)
                {
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interrupted.ApplyAsync(deletionDate, cancellation.Token));

        Assert.False(File.Exists(orphan.SourcePath));
        Assert.Equal(bytes, File.ReadAllBytes(orphan.TrashPath));
        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(
            "pending",
            ReadState(pending!).GetProperty("externalTrashCollection").GetString());

        ExternalPayloadTrashPolicyMaintenanceResult retry =
            await new ProtectedExternalPayloadTrashPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(deletionDate);

        Assert.False(retry.WasAlreadyCompleted);
        Assert.Equal(0, retry.CollectedFileCount);
        Assert.Empty(retry.TrashRelativePaths);
        Assert.Equal(
            "completed",
            ReadState(retry.Operation).GetProperty("externalTrashCollection").GetString());
    }

    [Fact]
    public async Task ApplyAsync_PolicyTamperAfterCollectionFailsBeforeMarkerCommit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        byte[] bytes = [11, 12, 13];
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 4),
            deletionDate,
            bytes);

        var service = new ProtectedExternalPayloadTrashPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ExternalPayloadTrashPolicyMaintenanceCheckpoint.CollectionCompleted)
                {
                    environment.Execute("""
                        UPDATE GlobalFormatCapturePolicy
                        SET CaptureRule = 'Allow'
                        WHERE FormatName = 'Text' COLLATE BINARY;
                        """);
                }
            });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ApplyAsync(deletionDate));

        Assert.False(File.Exists(orphan.SourcePath));
        Assert.Equal(bytes, File.ReadAllBytes(orphan.TrashPath));
        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(
            "pending",
            ReadState(pending!).GetProperty("externalTrashCollection").GetString());
    }

    [Fact]
    public async Task ApplyAsync_LateCancellationAfterMarkerCommitDoesNotDemoteDurableSuccess()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedExternalPayloadTrashPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ExternalPayloadTrashPolicyMaintenanceCheckpoint.AfterMarkerCommit)
                {
                    cancellation.Cancel();
                }
            });

        ExternalPayloadTrashPolicyMaintenanceResult result =
            await service.ApplyAsync(deletionDate, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(
            "completed",
            ReadState(result.Operation).GetProperty("externalTrashCollection").GetString());
        Assert.Equal("pending", ReadState(result.Operation).GetProperty("completion").GetString());
    }

    [Fact]
    public async Task ApplyAsync_ExactRetryAfterCompletedExternalTrashPhaseIsIdempotent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        var service = new ProtectedExternalPayloadTrashPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        ExternalPayloadTrashPolicyMaintenanceResult first =
            await service.ApplyAsync(deletionDate);
        environment.Factory.Modes.Clear();
        ExternalPayloadTrashPolicyMaintenanceResult retry =
            await service.ApplyAsync(deletionDate);

        Assert.False(first.WasAlreadyCompleted);
        Assert.True(retry.WasAlreadyCompleted);
        Assert.Equal(first.Operation, retry.Operation);
        Assert.Equal(0, retry.CollectedFileCount);
        Assert.Empty(retry.TrashRelativePaths);
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_FingerprintMismatchFailsBeforeFilesystemMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        PendingPolicyMaintenanceOperation operation =
            await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 5),
            deletionDate,
            [21, 22, 23]);

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
            new ProtectedExternalPayloadTrashPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(deletionDate));

        Assert.True(File.Exists(orphan.SourcePath));
        Assert.False(File.Exists(orphan.TrashPath));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_CollectorFailureLeavesMarkerPendingAndSourceIntact()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        byte[] expectedBytes = [31, 32, 33, 34];
        byte[] wrongBytes = [34, 33, 32, 31];
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 6),
            deletionDate,
            expectedBytes,
            wrongBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedExternalPayloadTrashPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(deletionDate));

        Assert.True(File.Exists(orphan.SourcePath));
        Assert.False(File.Exists(orphan.TrashPath));
        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(
            "pending",
            ReadState(pending!).GetProperty("externalTrashCollection").GetString());
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

    private static async Task<PendingPolicyMaintenanceOperation> PrepareCatalogCompletedMarkerAsync(
        GlobalPolicyTestEnvironment environment)
    {
        await PrepareArchiveCompletedMarkerAsync(environment);
        ExternalPayloadCatalogPolicyMaintenanceResult catalog =
            await new ProtectedExternalPayloadCatalogPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync();
        return catalog.Operation;
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

    private static ManagedOrphan WriteManagedOrphan(
        GlobalPolicyTestEnvironment environment,
        DateOnly storedDate,
        DateOnly deletionDate,
        byte[] expectedBytes,
        byte[]? actualBytes = null)
    {
        string sha256 = Convert
            .ToHexString(SHA256.HashData(expectedBytes))
            .ToLowerInvariant();
        string filesRelativePath = Path.Combine(
            storedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            sha256 + ".png");
        string sourcePath = Path.Combine(environment.Root, "Files", filesRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllBytes(sourcePath, actualBytes ?? expectedBytes);

        string trashRelativePath = Path.Combine(
            "Trash",
            deletionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            filesRelativePath);
        return new ManagedOrphan(
            sourcePath,
            Path.Combine(environment.Root, trashRelativePath),
            trashRelativePath);
    }

    private static SqlitePendingPolicyMaintenanceRepository Pending(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static JsonElement ReadState(PendingPolicyMaintenanceOperation operation)
    {
        using JsonDocument document = JsonDocument.Parse(operation.StateJson);
        return document.RootElement.Clone();
    }

    private sealed record ManagedOrphan(
        string SourcePath,
        string TrashPath,
        string TrashRelativePath);
}
