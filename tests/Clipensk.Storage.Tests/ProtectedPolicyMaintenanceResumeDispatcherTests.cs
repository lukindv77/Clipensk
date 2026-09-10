using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Microsoft.Data.Sqlite;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedPolicyMaintenanceResumeDispatcherTests
{
    private static readonly DateOnly DeletionDate = new(2026, 9, 10);

    [Fact]
    public async Task ResumeAsync_NoPendingOperationIsNoOp()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();

        PolicyMaintenanceResumeDispatchResult result =
            await new ProtectedPolicyMaintenanceResumeDispatcher(
                    environment.Session,
                    environment.Factory)
                .ResumeAsync(DeletionDate);

        Assert.False(result.HadPendingOperation);
        Assert.Null(result.OperationId);
        Assert.Null(result.Global);
        Assert.Null(result.Application);
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
    }

    [Fact]
    public async Task ResumeAsync_GlobalMarkerRoutesToGlobalCoordinator()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(GlobalPolicy());
        CurrentPolicyMaintenanceResult current =
            await new ProtectedCurrentPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(Policy(
                    ClipboardCapturePolicyRule.Allow,
                    ("Text", ClipboardCapturePolicyRule.Deny)));

        PolicyMaintenanceResumeDispatchResult result =
            await new ProtectedPolicyMaintenanceResumeDispatcher(
                    environment.Session,
                    environment.Factory)
                .ResumeAsync(DeletionDate);

        Assert.True(result.HadPendingOperation);
        Assert.Equal(current.Operation.OperationId, result.OperationId);
        Assert.NotNull(result.Global);
        Assert.Null(result.Application);
        Assert.True(result.Global!.HadPendingOperation);
        Assert.Equal(current.Operation.OperationId, result.Global.OperationId);
        Assert.Null(await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ResumeAsync_ApplicationMarkerRoutesToApplicationCoordinator()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(GlobalPolicy());
        DurableApplicationId applicationId = InsertApplicationIdentity(environment);
        CurrentApplicationPolicyMaintenanceResult current =
            await new ProtectedCurrentApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(applicationId, ApplicationPolicy());

        PolicyMaintenanceResumeDispatchResult result =
            await new ProtectedPolicyMaintenanceResumeDispatcher(
                    environment.Session,
                    environment.Factory)
                .ResumeAsync(DeletionDate);

        Assert.True(result.HadPendingOperation);
        Assert.Equal(current.Operation.OperationId, result.OperationId);
        Assert.Null(result.Global);
        Assert.NotNull(result.Application);
        Assert.True(result.Application!.HadPendingOperation);
        Assert.Equal(current.Operation.OperationId, result.Application.OperationId);
        Assert.Null(await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ResumeAsync_UnsupportedMarkerFailsClosedBeforeContinuationWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(GlobalPolicy());
        CurrentPolicyMaintenanceResult current =
            await new ProtectedCurrentPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(Policy(
                    ClipboardCapturePolicyRule.Allow,
                    ("Text", ClipboardCapturePolicyRule.Deny)));
        environment.Execute("""
            UPDATE PendingPolicyMaintenance
            SET OperationKind = 'UnsupportedMaintenance'
            WHERE SingletonId = 1;
            """);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedPolicyMaintenanceResumeDispatcher(
                    environment.Session,
                    environment.Factory)
                .ResumeAsync(DeletionDate));

        PendingPolicyMaintenanceOperation? pending = await Pending(environment).ReadAsync();
        Assert.NotNull(pending);
        Assert.Equal(current.Operation.OperationId, pending!.OperationId);
        Assert.Equal("UnsupportedMaintenance", pending.OperationKind);
        Assert.DoesNotContain(SqliteOpenMode.ReadWrite, environment.Factory.Modes);
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

    private static SqlitePendingPolicyMaintenanceRepository Pending(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);
}
