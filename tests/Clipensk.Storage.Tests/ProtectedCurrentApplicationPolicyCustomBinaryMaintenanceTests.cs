using System.Text.Json;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedCurrentApplicationPolicyCustomBinaryMaintenanceTests
{
    private const string CustomFormat = "Vendor.Product.Binary";

    [Fact]
    public async Task ApplyAsync_AtomicallyPublishesMappingPolicyAndV2Marker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = GlobalPolicy();
        await environment.Repository.InitializeAsync(global);
        ApplicationId applicationId = InsertApplicationIdentity(environment);
        ClipboardCapturePolicy policy = CustomAllowPolicy();
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        CurrentApplicationPolicyMaintenanceResult result = await service.ApplyAsync(
            applicationId,
            policy,
            [new ApplicationCustomBinaryFormatConfiguration(CustomFormat, ".Foo")]);

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(
            ".foo",
            await environment.CustomBinaryConfigurations.ReadFileExtensionAsync(CustomFormat));
        AssertPolicy(
            policy,
            await ReadApplicationPolicyAsync(environment, global, applicationId));

        PendingPolicyMaintenanceOperation? marker = await Pending(environment).ReadAsync();
        Assert.Equal(result.Operation, marker);
        using JsonDocument state = JsonDocument.Parse(result.Operation.StateJson);
        Assert.Equal(2, state.RootElement.GetProperty("version").GetInt32());
        string? fingerprint = state.RootElement
            .GetProperty("customBinaryConfigurationFingerprint")
            .GetString();
        Assert.NotNull(fingerprint);
        Assert.Equal(64, fingerprint.Length);
    }

    [Fact]
    public async Task ApplyAsync_FailureAfterMappingPublishRollsBackMappingPolicyAndMarkerTogether()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = GlobalPolicy();
        await environment.Repository.InitializeAsync(global);
        ApplicationId applicationId = InsertApplicationIdentity(environment);
        ClipboardCapturePolicy policy = CustomAllowPolicy();
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint ==
                    CurrentApplicationPolicyMaintenanceCheckpoint.CustomBinaryConfigurationPublished)
                {
                    throw new InvalidOperationException("Injected mapping publication failure.");
                }
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyAsync(
                applicationId,
                policy,
                [new ApplicationCustomBinaryFormatConfiguration(CustomFormat, ".foo")]));

        Assert.Null(
            await environment.CustomBinaryConfigurations.ReadFileExtensionAsync(CustomFormat));
        Assert.Null(await ReadApplicationPolicyAsync(environment, global, applicationId));
        Assert.Null(await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ApplyAsync_ExactMappingAwareRetryIsIdempotentAndLegacyRetryIsRejected()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = GlobalPolicy();
        await environment.Repository.InitializeAsync(global);
        ApplicationId applicationId = InsertApplicationIdentity(environment);
        ClipboardCapturePolicy policy = CustomAllowPolicy();
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);
        ApplicationCustomBinaryFormatConfiguration[] mappings =
        [
            new(CustomFormat, ".foo"),
        ];

        CurrentApplicationPolicyMaintenanceResult first = await service.ApplyAsync(
            applicationId,
            policy,
            mappings);
        CurrentApplicationPolicyMaintenanceResult retry = await service.ApplyAsync(
            applicationId,
            policy,
            mappings);

        Assert.False(first.WasAlreadyCompleted);
        Assert.True(retry.WasAlreadyCompleted);
        Assert.Equal(first.Operation, retry.Operation);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyAsync(applicationId, policy));
    }

    [Fact]
    public async Task ApplyAsync_ExistingDifferentExtensionRejectsRebindWithoutMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = GlobalPolicy();
        await environment.Repository.InitializeAsync(global);
        await environment.CustomBinaryConfigurations.InitializeAsync(CustomFormat, ".foo");
        ApplicationId applicationId = InsertApplicationIdentity(environment);
        ClipboardCapturePolicy policy = CustomAllowPolicy();
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyAsync(
                applicationId,
                policy,
                [new ApplicationCustomBinaryFormatConfiguration(CustomFormat, ".bar")]));

        Assert.Equal(
            ".foo",
            await environment.CustomBinaryConfigurations.ReadFileExtensionAsync(CustomFormat));
        Assert.Null(await ReadApplicationPolicyAsync(environment, global, applicationId));
        Assert.Null(await Pending(environment).ReadAsync());
    }

    [Fact]
    public async Task ApplyAsync_ExistingSameExtensionIsReusedWithoutRebind()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy global = GlobalPolicy();
        await environment.Repository.InitializeAsync(global);
        await environment.CustomBinaryConfigurations.InitializeAsync(CustomFormat, ".foo");
        ApplicationId applicationId = InsertApplicationIdentity(environment);
        ClipboardCapturePolicy policy = CustomAllowPolicy();
        var service = new ProtectedCurrentApplicationPolicyMaintenanceService(
            environment.Session,
            environment.Factory);

        CurrentApplicationPolicyMaintenanceResult result = await service.ApplyAsync(
            applicationId,
            policy,
            [new ApplicationCustomBinaryFormatConfiguration(CustomFormat, ".FOO")]);

        Assert.False(result.WasAlreadyCompleted);
        Assert.Equal(
            ".foo",
            await environment.CustomBinaryConfigurations.ReadFileExtensionAsync(CustomFormat));
        AssertPolicy(
            policy,
            await ReadApplicationPolicyAsync(environment, global, applicationId));
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

    private static ApplicationId InsertApplicationIdentity(GlobalPolicyTestEnvironment environment)
    {
        ApplicationId applicationId = ApplicationId.New();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId}', '2026-09-11T00:00:00.0000000+00:00');
            """);
        return applicationId;
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

    private static SqlitePendingPolicyMaintenanceRepository Pending(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static void AssertPolicy(
        ClipboardCapturePolicy expected,
        ClipboardCapturePolicy? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Capture, actual.Capture);
        Assert.Equal(expected.Formats.Count, actual.Formats.Count);
        foreach ((string name, ClipboardFormatCapturePolicy format) in expected.Formats)
        {
            Assert.True(actual.Formats.TryGetValue(name, out ClipboardFormatCapturePolicy persisted));
            Assert.Equal(format, persisted);
        }
    }
}
