using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedCapturePolicyPublishServiceTests
{
    private static readonly ClipboardCapturePolicy AllowText = new(
        ClipboardCapturePolicyRule.Allow,
        new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
        {
            ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1024),
        });

    private static readonly ClipboardCapturePolicy DenyAll = new(ClipboardCapturePolicyRule.Deny);

    [Fact]
    public async Task GlobalEdit_PublishesThePolicyWithoutTouchingHistoryOrCreatingAMarker()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationId application = InsertApplicationIdentity(environment);
        InsertInlinePayload(environment, "Text", application);

        await Service(environment).PublishGlobalPolicyAsync(DenyAll);

        ClipboardCapturePolicy? stored = await environment.Repository.ReadAsync();
        Assert.NotNull(stored);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, stored!.Capture);
        Assert.Empty(stored.Formats);
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryPayload;"));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task GlobalEdit_RequiresTheInitialSetup()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).PublishGlobalPolicyAsync(DenyAll));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalCapturePolicy;"));
    }

    [Fact]
    public async Task ConfiguredRootEdit_PublishesThePolicyAndMappingsWithoutTouchingHistory()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationId application = InsertApplicationIdentity(environment);
        environment.Execute($"INSERT INTO ApplicationCapturePolicy VALUES ('{application}', 'Allow');");
        InsertInlinePayload(environment, "Text", application);
        var edited = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Deny,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Vendor.Binary"] = new(ClipboardCapturePolicyRule.Allow),
            });

        await Service(environment).PublishApplicationPolicyAsync(
            application,
            edited,
            [new ApplicationCustomBinaryFormatConfiguration("Vendor.Binary", ".VBIN")]);

        Assert.Equal(1, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationCapturePolicy WHERE ApplicationId = '{application}' AND CaptureRule = 'Deny';"));
        Assert.Equal(1, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationFormatCapturePolicy WHERE ApplicationId = '{application}' AND FormatName = 'Vendor.Binary';"));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM CustomBinaryFormatConfiguration WHERE FormatName = 'Vendor.Binary' AND FileExtension = '.vbin';"));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryPayload;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task UnconfiguredRoot_IsNotAnEditAndIsRejected()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationId application = InsertApplicationIdentity(environment);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).PublishApplicationPolicyAsync(application, DenyAll));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationCapturePolicy;"));
    }

    [Fact]
    public async Task GroupMember_IsRejected()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationId root = InsertApplicationIdentity(environment);
        ApplicationId member = InsertApplicationIdentity(environment);
        environment.Execute($"""
            INSERT INTO ApplicationCapturePolicy VALUES ('{root}', 'Allow');
            INSERT INTO ApplicationGroupMember VALUES ('{member}', '{root}', NULL, '2026-09-22T10:00:00.0000000+00:00');
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).PublishApplicationPolicyAsync(member, DenyAll));
        Assert.Equal(0, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationCapturePolicy WHERE ApplicationId = '{member}';"));
    }

    [Fact]
    public async Task ConflictingCustomBinaryRebind_RollsBackThePolicyToo()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationId application = InsertApplicationIdentity(environment);
        environment.Execute($"""
            INSERT INTO ApplicationCapturePolicy VALUES ('{application}', 'Allow');
            INSERT INTO CustomBinaryFormatConfiguration VALUES ('Vendor.Binary', '.vbin');
            """);
        var edited = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Deny,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Vendor.Binary"] = new(ClipboardCapturePolicyRule.Allow),
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).PublishApplicationPolicyAsync(
                application,
                edited,
                [new ApplicationCustomBinaryFormatConfiguration("Vendor.Binary", ".other")]));

        Assert.Equal(1, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationCapturePolicy WHERE ApplicationId = '{application}' AND CaptureRule = 'Allow';"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationFormatCapturePolicy;"));
    }

    [Fact]
    public async Task PendingMaintenanceMarker_BlocksEveryPublication()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationId application = InsertApplicationIdentity(environment);
        const string EmptyState = "{}";
        environment.Execute($"""
            INSERT INTO ApplicationCapturePolicy VALUES ('{application}', 'Allow');
            INSERT INTO PendingPolicyMaintenance VALUES (
                1, '55555555-5555-5555-5555-555555555555', 'SomeOperation', '{EmptyState}',
                '2026-09-22T10:00:00.0000000+00:00', '2026-09-22T10:00:00.0000000+00:00');
            """);

        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Service(environment).PublishGlobalPolicyAsync(DenyAll));
        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Service(environment).PublishApplicationPolicyAsync(application, DenyAll));

        ClipboardCapturePolicy? global = await environment.Repository.ReadAsync();
        Assert.Equal(ClipboardCapturePolicyRule.Allow, global!.Capture);
        Assert.Equal(1, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationCapturePolicy WHERE ApplicationId = '{application}' AND CaptureRule = 'Allow';"));
    }

    [Fact]
    public async Task CancellationBeforeCommit_LeavesThePolicyUnchanged()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(environment).PublishGlobalPolicyAsync(DenyAll, cancellation.Token));

        ClipboardCapturePolicy? global = await environment.Repository.ReadAsync();
        Assert.Equal(ClipboardCapturePolicyRule.Allow, global!.Capture);
    }

    private static ProtectedCapturePolicyPublishService Service(GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static ApplicationId InsertApplicationIdentity(GlobalPolicyTestEnvironment environment)
    {
        ApplicationId applicationId = ApplicationId.New();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId}', '2026-09-10T00:00:00.0000000+00:00');
            """);
        return applicationId;
    }

    private static void InsertInlinePayload(
        GlobalPolicyTestEnvironment environment,
        string formatName,
        ApplicationId sourceApplicationId)
    {
        Guid eventId = Guid.NewGuid();
        environment.Execute($"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '{eventId:D}', '2026-09-10T00:00:00.0000000+00:00', 0, 'UTC', '2026-09-10',
                '{sourceApplicationId}', NULL, NULL, NULL);
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
            VALUES (
                '{eventId:D}', 0, '{formatName}', 'Text', 4, 'text', 'text', NULL, NULL, NULL);
            """);
    }
}
