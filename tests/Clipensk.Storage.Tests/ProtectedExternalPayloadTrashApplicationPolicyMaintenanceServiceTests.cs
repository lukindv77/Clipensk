using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedExternalPayloadTrashApplicationPolicyMaintenanceServiceTests
{
    [Fact]
    public async Task ApplyAsync_CollectsOrphanAndMarksOnlyApplicationTrashPhaseCompleted()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (DurableApplicationId applicationId, PendingPolicyMaintenanceOperation catalogCompleted) =
            await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        byte[] bytes = [1, 2, 3, 4, 5];
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 1),
            deletionDate,
            bytes);

        ExternalPayloadTrashApplicationPolicyMaintenanceResult result =
            await new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(deletionDate);

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(catalogCompleted.OperationId, result.Operation.OperationId);
        Assert.Equal(1, result.CollectedFileCount);
        Assert.Equal([orphan.TrashRelativePath], result.TrashRelativePaths);
        Assert.False(File.Exists(orphan.SourcePath));
        Assert.Equal(bytes, File.ReadAllBytes(orphan.TrashPath));

        JsonElement state = ReadState(result.Operation);
        Assert.Equal(applicationId.ToString(), state.GetProperty("applicationId").GetString());
        Assert.Equal("completed", state.GetProperty("currentPhase").GetString());
        Assert.Equal("completed", state.GetProperty("archiveExternalReferenceCleanup").GetString());
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
            new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
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
        var interrupted = new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                    ExternalPayloadTrashApplicationPolicyMaintenanceCheckpoint.CollectionCompleted)
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

        ExternalPayloadTrashApplicationPolicyMaintenanceResult retry =
            await new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
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
    public async Task ApplyAsync_ApplicationPolicyTamperAfterCollectionFailsBeforeMarkerCommit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (DurableApplicationId applicationId, _) =
            await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        byte[] bytes = [11, 12, 13];
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 4),
            deletionDate,
            bytes);

        var service = new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                    ExternalPayloadTrashApplicationPolicyMaintenanceCheckpoint.CollectionCompleted)
                {
                    environment.Execute($"""
                        UPDATE ApplicationFormatCapturePolicy
                        SET CaptureRule = 'Allow'
                        WHERE ApplicationId = '{applicationId}' COLLATE BINARY
                          AND FormatName = 'Text' COLLATE BINARY;
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
        var service = new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                    ExternalPayloadTrashApplicationPolicyMaintenanceCheckpoint.AfterMarkerCommit)
                {
                    cancellation.Cancel();
                }
            });

        ExternalPayloadTrashApplicationPolicyMaintenanceResult result =
            await service.ApplyAsync(deletionDate, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(
            "completed",
            ReadState(result.Operation).GetProperty("externalTrashCollection").GetString());
        Assert.Equal("pending", ReadState(result.Operation).GetProperty("completion").GetString());
    }

    [Fact]
    public async Task ApplyAsync_ExactRetryAfterCompletedTrashPhaseIsIdempotent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        var service = new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        ExternalPayloadTrashApplicationPolicyMaintenanceResult first =
            await service.ApplyAsync(deletionDate);
        environment.Factory.Modes.Clear();
        ExternalPayloadTrashApplicationPolicyMaintenanceResult retry =
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
        (_, PendingPolicyMaintenanceOperation operation) =
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
            new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(deletionDate));

        Assert.True(File.Exists(orphan.SourcePath));
        Assert.False(File.Exists(orphan.TrashPath));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_MissingTargetApplicationPolicyFailsBeforeFilesystemMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        (DurableApplicationId applicationId, _) =
            await PrepareCatalogCompletedMarkerAsync(environment);
        DateOnly deletionDate = new(2026, 9, 10);
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 6),
            deletionDate,
            [31, 32, 33]);
        environment.Execute($"""
            DELETE FROM ApplicationCapturePolicy
            WHERE ApplicationId = '{applicationId}' COLLATE BINARY;
            """);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(deletionDate));

        Assert.True(File.Exists(orphan.SourcePath));
        Assert.False(File.Exists(orphan.TrashPath));
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ApplyAsync_GlobalMaintenanceMarkerIsRejectedBeforeFilesystemMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(GlobalPolicy());
        await new ProtectedCurrentPolicyMaintenanceService(
                environment.Session,
                environment.Factory)
            .ApplyAsync(Policy(
                ClipboardCapturePolicyRule.Allow,
                ("Text", ClipboardCapturePolicyRule.Deny)));
        DateOnly deletionDate = new(2026, 9, 10);
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 7),
            deletionDate,
            [41, 42, 43]);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
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
        byte[] expectedBytes = [51, 52, 53, 54];
        byte[] wrongBytes = [54, 53, 52, 51];
        ManagedOrphan orphan = WriteManagedOrphan(
            environment,
            new DateOnly(2026, 9, 8),
            deletionDate,
            expectedBytes,
            wrongBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
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

    private static async Task<(DurableApplicationId ApplicationId, PendingPolicyMaintenanceOperation Operation)>
        PrepareCatalogCompletedMarkerAsync(GlobalPolicyTestEnvironment environment)
    {
        (DurableApplicationId applicationId, _) =
            await PrepareArchiveCompletedMarkerAsync(environment);
        ExternalPayloadCatalogApplicationPolicyMaintenanceResult catalog =
            await new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync();
        return (applicationId, catalog.Operation);
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
