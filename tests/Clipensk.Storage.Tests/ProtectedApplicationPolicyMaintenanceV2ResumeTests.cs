using System.Text.Json;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedApplicationPolicyMaintenanceV2ResumeTests
{
    private const string CustomFormat = "Vendor.Product.Binary";
    private static readonly DateOnly DeletionDate = new(2026, 9, 10);

    [Fact]
    public async Task ResumeAsync_ContinuesV2MappingAwareMarkerThroughAllPhases()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(GlobalPolicy());
        DurableApplicationId applicationId = InsertApplicationIdentity(environment);
        ClipboardCapturePolicy applicationPolicy = CustomAllowPolicy();

        CurrentApplicationPolicyMaintenanceResult current =
            await new ProtectedCurrentApplicationPolicyMaintenanceService(
                    environment.Session,
                    environment.Factory)
                .ApplyAsync(
                    applicationId,
                    applicationPolicy,
                    [new ApplicationCustomBinaryFormatConfiguration(CustomFormat, ".FOO")]);

        JsonElement currentState = ReadState(current.Operation);
        Assert.Equal(2, currentState.GetProperty("version").GetInt32());
        string? configurationFingerprint = currentState
            .GetProperty("customBinaryConfigurationFingerprint")
            .GetString();
        Assert.NotNull(configurationFingerprint);
        Assert.Equal(64, configurationFingerprint.Length);
        Assert.Equal(
            ".foo",
            await environment.CustomBinaryConfigurations.ReadFileExtensionAsync(CustomFormat));

        var coordinator = new ProtectedApplicationPolicyMaintenanceResumeCoordinator(
            environment.Session,
            environment.Factory);
        ApplicationPolicyMaintenanceResumeResult result =
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
        Assert.Equal(
            ".foo",
            await environment.CustomBinaryConfigurations.ReadFileExtensionAsync(CustomFormat));

        ApplicationPolicyMaintenanceResumeResult retry =
            await coordinator.ResumeAsync(DeletionDate);
        Assert.False(retry.HadPendingOperation);
        Assert.Null(retry.OperationId);
    }

    private static ClipboardCapturePolicy GlobalPolicy() =>
        new(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow),
            });

    private static ClipboardCapturePolicy CustomAllowPolicy() =>
        new(
            ClipboardCapturePolicyRule.Inherit,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                [CustomFormat] = new(ClipboardCapturePolicyRule.Allow, 1024),
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

    private static SqlitePendingPolicyMaintenanceRepository Pending(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static JsonElement ReadState(PendingPolicyMaintenanceOperation operation)
    {
        using JsonDocument document = JsonDocument.Parse(operation.StateJson);
        return document.RootElement.Clone();
    }
}
