using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedGlobalPolicyMaintenanceCompletionAndResumeTests
{
    private static readonly DateOnly DeletionDate = new(2026, 9, 10);

    [Fact]
    public async Task CompleteAsync_MarksCompletionDurablyThenClearsMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        PendingPolicyMaintenanceOperation trashCompleted =
            await PrepareTrashCompletedMarkerAsync(environment);
        var checkpoints = new List<GlobalPolicyMaintenanceCompletionCheckpoint>();
        var service = new ProtectedGlobalPolicyMaintenanceCompletionService(
            environment.Session,
            environment.Factory,
            checkpoints.Add);

        GlobalPolicyMaintenanceCompletionResult result = await service.CompleteAsync();

        Assert.False(result.WasCompletionAlreadyCompleted);
        Assert.Equal(trashCompleted.OperationId, result.Operation.OperationId);
        Assert.Equal(
            "completed",
            ReadState(result.Operation).GetProperty("completion").GetString());
        Assert.Equal(
            [
                GlobalPolicyMaintenanceCompletionCheckpoint.BeforeCompletionCommit,
                GlobalPolicyMaintenanceCompletionCheckpoint.AfterCompletionCommit,
                GlobalPolicyMaintenanceCompletionCheckpoint.BeforeMarkerClearCommit,
                GlobalPolicyMaintenanceCompletionCheckpoint.AfterMarkerClearCommit,
            ],
            checkpoints);
        Assert.Null(await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task CompleteAsync_CancellationAfterCompletionCommitLeavesResumableCompletedMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        PendingPolicyMaintenanceOperation trashCompleted =
            await PrepareTrashCompletedMarkerAsync(environment);
        using var cancellation = new CancellationTokenSource();
        var interrupted = new ProtectedGlobalPolicyMaintenanceCompletionService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == GlobalPolicyMaintenanceCompletionCheckpoint.AfterCompletionCommit)
                {
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interrupted.CompleteAsync(cancellation.Token));

        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(trashCompleted.OperationId, pending!.OperationId);
        Assert.Equal("completed", ReadState(pending).GetProperty("completion").GetString());

        GlobalPolicyMaintenanceCompletionResult retry =
            await new ProtectedGlobalPolicyMaintenanceCompletionService(
                    environment.Session,
                    environment.Factory)
                .CompleteAsync();

        Assert.True(retry.WasCompletionAlreadyCompleted);
        Assert.Equal(trashCompleted.OperationId, retry.Operation.OperationId);
        Assert.Null(await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task CompleteAsync_LateCancellationAfterMarkerClearCommitDoesNotDemoteDurableSuccess()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await PrepareTrashCompletedMarkerAsync(environment);
        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedGlobalPolicyMaintenanceCompletionService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == GlobalPolicyMaintenanceCompletionCheckpoint.AfterMarkerClearCommit)
                {
                    cancellation.Cancel();
                }
            });

        GlobalPolicyMaintenanceCompletionResult result =
            await service.CompleteAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.WasCompletionAlreadyCompleted);
        Assert.Null(await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task CompleteAsync_TrashPendingFailsBeforeWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        PendingPolicyMaintenanceOperation catalogCompleted =
            await PrepareCatalogCompletedMarkerAsync(environment);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedGlobalPolicyMaintenanceCompletionService(
                    environment.Session,
                    environment.Factory)
                .CompleteAsync());

        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(catalogCompleted.OperationId, pending!.OperationId);
        Assert.Equal("pending", ReadState(pending).GetProperty("completion").GetString());
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task CompleteAsync_FingerprintMismatchFailsBeforeWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        PendingPolicyMaintenanceOperation operation =
            await PrepareTrashCompletedMarkerAsync(environment);
        JsonElement state = ReadState(operation);
        string fingerprint = state.GetProperty("policyFingerprint").GetString()!;
        string replacement = string.Equals(
            fingerprint,
            new string('F', 64),
            StringComparison.Ordinal)
            ? new string('E', 64)
            : new string('F', 64);
        await Pending(environment).UpdateStateAsync(
            operation.OperationId,
            operation.StateJson.Replace(fingerprint, replacement));
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ProtectedGlobalPolicyMaintenanceCompletionService(
                    environment.Session,
                    environment.Factory)
                .CompleteAsync());

        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(operation.OperationId, pending!.OperationId);
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ResumeAsync_NoPendingOperationIsNoOp()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        var coordinator = new ProtectedGlobalPolicyMaintenanceResumeCoordinator(
            environment.Session,
            environment.Factory);

        GlobalPolicyMaintenanceResumeResult result =
            await coordinator.ResumeAsync(DeletionDate);

        Assert.False(result.HadPendingOperation);
        Assert.Null(result.OperationId);
        Assert.Null(result.Archive);
        Assert.Null(result.Catalog);
        Assert.Null(result.Trash);
        Assert.Null(result.Completion);
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ResumeAsync_ContinuesCurrentThroughAllPhasesAndClearsMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        CurrentPolicyMaintenanceResult current =
            await new ProtectedCurrentPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(Policy(
                    ClipboardCapturePolicyRule.Allow,
                    ("Text", ClipboardCapturePolicyRule.Deny)));
        var coordinator = new ProtectedGlobalPolicyMaintenanceResumeCoordinator(
            environment.Session,
            environment.Factory);

        GlobalPolicyMaintenanceResumeResult result =
            await coordinator.ResumeAsync(DeletionDate);

        Assert.True(result.HadPendingOperation);
        Assert.Equal(current.Operation.OperationId, result.OperationId);
        Assert.NotNull(result.Archive);
        Assert.NotNull(result.Catalog);
        Assert.NotNull(result.Trash);
        Assert.NotNull(result.Completion);
        Assert.False(result.Archive!.WasAlreadyCompleted);
        Assert.False(result.Catalog!.WasAlreadyCompleted);
        Assert.False(result.Trash!.WasAlreadyCompleted);
        Assert.False(result.Completion!.WasCompletionAlreadyCompleted);
        Assert.Null(await Pending(environment).ReadAsync());

        GlobalPolicyMaintenanceResumeResult retry =
            await coordinator.ResumeAsync(DeletionDate);
        Assert.False(retry.HadPendingOperation);
        Assert.Null(retry.OperationId);
    }

    [Fact]
    public async Task ResumeAsync_CompletedMarkerAfterInterruptedFinalizerClearsWithoutRepeatingPhases()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        PendingPolicyMaintenanceOperation trashCompleted =
            await PrepareTrashCompletedMarkerAsync(environment);
        using var cancellation = new CancellationTokenSource();
        var interrupted = new ProtectedGlobalPolicyMaintenanceCompletionService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == GlobalPolicyMaintenanceCompletionCheckpoint.AfterCompletionCommit)
                {
                    cancellation.Cancel();
                }
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interrupted.CompleteAsync(cancellation.Token));

        var coordinator = new ProtectedGlobalPolicyMaintenanceResumeCoordinator(
            environment.Session,
            environment.Factory);
        GlobalPolicyMaintenanceResumeResult result =
            await coordinator.ResumeAsync(DeletionDate);

        Assert.True(result.HadPendingOperation);
        Assert.Equal(trashCompleted.OperationId, result.OperationId);
        Assert.NotNull(result.Archive);
        Assert.NotNull(result.Catalog);
        Assert.NotNull(result.Trash);
        Assert.NotNull(result.Completion);
        Assert.True(result.Archive!.WasAlreadyCompleted);
        Assert.True(result.Catalog!.WasAlreadyCompleted);
        Assert.True(result.Trash!.WasAlreadyCompleted);
        Assert.True(result.Completion!.WasCompletionAlreadyCompleted);
        Assert.Null(await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ResumeAsync_DifferentOperationKindFailsBeforeContinuationWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Policy(
            ClipboardCapturePolicyRule.Allow,
            ("Text", ClipboardCapturePolicyRule.Allow)));
        CurrentPolicyMaintenanceResult current =
            await new ProtectedCurrentPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(Policy(
                    ClipboardCapturePolicyRule.Allow,
                    ("Text", ClipboardCapturePolicyRule.Deny)));
        environment.Execute("""
            UPDATE PendingPolicyMaintenance
            SET OperationKind = 'OtherMaintenance'
            WHERE SingletonId = 1;
            """);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedGlobalPolicyMaintenanceResumeCoordinator(
                    environment.Session,
                    environment.Factory)
                .ResumeAsync(DeletionDate));

        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(current.Operation.OperationId, pending!.OperationId);
        Assert.Equal("OtherMaintenance", pending.OperationKind);
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

    private static async Task<PendingPolicyMaintenanceOperation> PrepareTrashCompletedMarkerAsync(
        GlobalPolicyTestEnvironment environment)
    {
        await PrepareCatalogCompletedMarkerAsync(environment);
        ExternalPayloadTrashPolicyMaintenanceResult trash =
            await new ProtectedExternalPayloadTrashPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(DeletionDate);
        return trash.Operation;
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

    private static SqlitePendingPolicyMaintenanceRepository Pending(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static JsonElement ReadState(PendingPolicyMaintenanceOperation operation)
    {
        using JsonDocument document = JsonDocument.Parse(operation.StateJson);
        return document.RootElement.Clone();
    }
}
