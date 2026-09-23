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
    public async Task GroupEdit_PublishesThePolicyAndMappingsWithoutTouchingHistory()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationId application = InsertApplicationIdentity(environment);
        ApplicationGroupId group = ApplicationGroupTestData.InsertGroup(environment, "Editors", AllowText, application);
        InsertInlinePayload(environment, "Text", application);
        var edited = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Deny,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Vendor.Binary"] = new(ClipboardCapturePolicyRule.Allow),
            });

        await Service(environment).PublishGroupPolicyAsync(
            group,
            edited,
            [new ApplicationCustomBinaryFormatConfiguration("Vendor.Binary", ".VBIN")]);

        ApplicationGroup stored = ApplicationGroupTestData.ReadGroups(environment).FindGroup(group)!;
        Assert.Equal(ClipboardCapturePolicyRule.Deny, stored.Policy.Capture);
        Assert.Equal(["Vendor.Binary"], stored.Policy.Formats.Keys);
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM CustomBinaryFormatConfiguration WHERE FormatName = 'Vendor.Binary' AND FileExtension = '.vbin';"));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryPayload;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task GroupEdit_RejectsAnUnknownGroupAndAnInheritingPolicyWithoutWriting()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationGroupId group = ApplicationGroupTestData.InsertGroup(environment, "Editors", AllowText);
        var inheriting = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Inherit);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).PublishGroupPolicyAsync(ApplicationGroupId.New(), DenyAll));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service(environment).PublishGroupPolicyAsync(group, inheriting));

        Assert.Equal(ClipboardCapturePolicyRule.Allow, ApplicationGroupTestData.ReadGroups(environment).FindGroup(group)!.Policy.Capture);
    }

    [Fact]
    public async Task ConflictingCustomBinaryRebind_RollsBackTheGroupPolicyToo()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationGroupId group = ApplicationGroupTestData.InsertGroup(environment, "Editors", AllowText);
        environment.Execute("INSERT INTO CustomBinaryFormatConfiguration VALUES ('Vendor.Binary', '.vbin');");
        var edited = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Deny,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Vendor.Binary"] = new(ClipboardCapturePolicyRule.Allow),
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).PublishGroupPolicyAsync(
                group,
                edited,
                [new ApplicationCustomBinaryFormatConfiguration("Vendor.Binary", ".other")]));

        ApplicationGroup stored = ApplicationGroupTestData.ReadGroups(environment).FindGroup(group)!;
        Assert.Equal(ClipboardCapturePolicyRule.Allow, stored.Policy.Capture);
        Assert.Equal(["Text"], stored.Policy.Formats.Keys);
    }

    [Fact]
    public async Task Rename_KeepsNamesUniqueWithoutRegardToCase()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationGroupId browsers = ApplicationGroupTestData.InsertGroup(environment, "Браузеры", AllowText);
        ApplicationGroupId editors = ApplicationGroupTestData.InsertGroup(environment, "Editors", AllowText);

        await Assert.ThrowsAsync<ApplicationGroupNameTakenException>(() =>
            Service(environment).RenameGroupAsync(editors, ApplicationGroupName.Create("БРАУЗЕРЫ")));
        await Service(environment).RenameGroupAsync(browsers, ApplicationGroupName.Create("браузеры"));
        await Service(environment).RenameGroupAsync(editors, ApplicationGroupName.Create("Редакторы"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).RenameGroupAsync(ApplicationGroupId.New(), ApplicationGroupName.Create("Другое")));

        ApplicationGroupDirectory groups = ApplicationGroupTestData.ReadGroups(environment);
        Assert.Equal("браузеры", groups.FindGroup(browsers)!.Name.Value);
        Assert.Equal("Редакторы", groups.FindGroup(editors)!.Name.Value);
    }

    [Fact]
    public async Task DeleteEmptyGroup_RefusesAGroupWithMembersAndDeletesAnEmptyOne()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationId application = InsertApplicationIdentity(environment);
        ApplicationGroupId used = ApplicationGroupTestData.InsertGroup(environment, "Used", AllowText, application);
        ApplicationGroupId empty = ApplicationGroupTestData.InsertGroup(environment, "Empty", AllowText);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(environment).DeleteEmptyGroupAsync(used));
        await Service(environment).DeleteEmptyGroupAsync(empty);

        ApplicationGroupDirectory groups = ApplicationGroupTestData.ReadGroups(environment);
        Assert.NotNull(groups.FindGroup(used));
        Assert.Null(groups.FindGroup(empty));
        Assert.Equal(0, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationGroupFormatCapturePolicy WHERE GroupId = '{empty}';"));
    }

    [Fact]
    public async Task PendingMaintenanceMarker_BlocksEveryPublication()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(AllowText);
        ApplicationGroupId group = ApplicationGroupTestData.InsertGroup(environment, "Editors", AllowText);
        ApplicationGroupId empty = ApplicationGroupTestData.InsertGroup(environment, "Empty", AllowText);
        const string EmptyState = "{}";
        environment.Execute($"""
            INSERT INTO PendingPolicyMaintenance VALUES (
                1, '55555555-5555-5555-5555-555555555555', 'SomeOperation', '{EmptyState}',
                '2026-09-22T10:00:00.0000000+00:00', '2026-09-22T10:00:00.0000000+00:00');
            """);

        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Service(environment).PublishGlobalPolicyAsync(DenyAll));
        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Service(environment).PublishGroupPolicyAsync(group, DenyAll));
        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Service(environment).RenameGroupAsync(group, ApplicationGroupName.Create("Other")));
        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Service(environment).DeleteEmptyGroupAsync(empty));

        ClipboardCapturePolicy? global = await environment.Repository.ReadAsync();
        Assert.Equal(ClipboardCapturePolicyRule.Allow, global!.Capture);
        ApplicationGroupDirectory groups = ApplicationGroupTestData.ReadGroups(environment);
        Assert.Equal("Editors", groups.FindGroup(group)!.Name.Value);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, groups.FindGroup(group)!.Policy.Capture);
        Assert.NotNull(groups.FindGroup(empty));
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
                '{eventId:D}', '2026-09-10T00:00:00.0000000Z', 0, 'UTC', '2026-09-10',
                '{sourceApplicationId}', NULL, NULL, NULL);
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256, ExternalRelativePath, ExternalSizeBytes)
            VALUES (
                '{eventId:D}', 0, '{formatName}', 'Text', 4, 'text', 'text', NULL, NULL, NULL);
            """);
    }
}
