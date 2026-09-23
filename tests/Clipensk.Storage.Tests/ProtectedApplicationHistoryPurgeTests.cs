using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Microsoft.Data.Sqlite;
using Xunit;
using static Clipensk.Storage.Tests.ApplicationGroupTestData;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedApplicationHistoryPurgeTests
{
    private static readonly DateOnly DeletionDate = new(2026, 9, 22);

    private static readonly ClipboardCapturePolicy Global = Policy(
        ClipboardCapturePolicyRule.Allow,
        ("Text", ClipboardCapturePolicyRule.Allow),
        ("HTML Format", ClipboardCapturePolicyRule.Allow),
        ("PNG", ClipboardCapturePolicyRule.Allow));

    private static readonly ClipboardCapturePolicy DenyHtmlAndPng = Policy(
        ClipboardCapturePolicyRule.Inherit,
        ("HTML Format", ClipboardCapturePolicyRule.Deny),
        ("PNG", ClipboardCapturePolicyRule.Deny));

    [Fact]
    public async Task FirstAssignment_PurgesCurrentPerRepresentationAcrossTheGroupOnly()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        DurableApplicationId member = InsertIdentity(environment);
        DurableApplicationId other = InsertIdentity(environment);
        AddMember(environment, member, root);

        Guid mixed = InsertEvent(environment, null, root, ("Text", "Text"), ("HTML Format", "Text"));
        Guid htmlOnly = InsertEvent(environment, null, member, ("HTML Format", "Text"));
        Guid alreadyEmpty = InsertEvent(environment, null, root);
        Guid textOnly = InsertEvent(environment, null, member, ("Text", "Text"));
        Guid otherHtml = InsertEvent(environment, null, other, ("HTML Format", "Text"));
        Guid unknownSource = InsertEvent(environment, null, null, ("HTML Format", "Text"));

        ApplicationHistoryPurgeStartResult result = await Service(environment)
            .StartFirstAssignmentAsync(root, DenyHtmlAndPng);

        Assert.Equal(2, result.CurrentSummary.DeletedRepresentations);
        Assert.Equal(2, result.CurrentSummary.DeletedRecords);
        Assert.Equal(1, result.CurrentSummary.TrimmedRecords);
        Assert.Equal(2, result.CurrentSummary.DeletedRepresentationsByFormat["HTML Format"]);

        Assert.Equal(1, PayloadCount(environment, null, mixed));
        Assert.Equal(0, EventCount(environment, null, htmlOnly));
        Assert.Equal(0, EventCount(environment, null, alreadyEmpty));
        Assert.Equal(1, PayloadCount(environment, null, textOnly));
        Assert.Equal(1, PayloadCount(environment, null, otherHtml));
        Assert.Equal(1, PayloadCount(environment, null, unknownSource));

        Assert.Equal(1, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationCapturePolicy WHERE ApplicationId = '{root}';"));
        Assert.Equal(1, environment.Scalar(
            $"SELECT COUNT(*) FROM PendingPolicyMaintenance WHERE OperationKind = '{ProtectedApplicationHistoryPurgeService.OperationKind}';"));
    }

    [Fact]
    public async Task FirstAssignment_IgnoresMaxBytesWhenPurging()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        Guid largeText = InsertEvent(environment, null, root, ("Text", "Text"));
        var tinyLimit = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Inherit,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1),
            });

        ApplicationHistoryPurgeStartResult result = await Service(environment)
            .StartFirstAssignmentAsync(root, tinyLimit);

        Assert.True(result.CurrentSummary.IsEmpty);
        Assert.Equal(1, PayloadCount(environment, null, largeText));
    }

    [Fact]
    public async Task Resume_PurgesArchivesFullyMovesTheOrphanedFileToTrashAndClearsTheMarker()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        DurableApplicationId other = InsertIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, root);
        InsertArchiveIdentity(environment, archive, other);

        Guid archivedHtml = InsertEvent(environment, archive, root, ("HTML Format", "Text"), ("Text", "Text"));
        (Guid archivedImage, string sourcePath, string trashPath) =
            InsertArchivedImage(environment, archive, root, DeletionDate);
        Guid otherHtml = InsertEvent(environment, archive, other, ("HTML Format", "Text"));

        await Service(environment).StartFirstAssignmentAsync(root, DenyHtmlAndPng);
        ApplicationHistoryPurgeResumeResult result = await Continuation(environment)
            .ResumeAsync(DeletionDate);

        Assert.Equal(1, result.ArchiveDatabaseCount);
        Assert.Equal(2, result.ArchiveSummary.DeletedRepresentations);
        Assert.Equal(1, result.ArchiveSummary.DeletedExternalReferences);
        Assert.Equal(1, result.ArchiveSummary.DeletedRecords);
        Assert.Equal(1, result.ArchiveSummary.TrimmedRecords);

        Assert.Equal(1, PayloadCount(environment, archive, archivedHtml));
        Assert.Equal(0, EventCount(environment, archive, archivedImage));
        Assert.Equal(1, PayloadCount(environment, archive, otherHtml));
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(trashPath));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Resume_AfterACrashBetweenArchiveCommitAndPhaseMark_IsIdempotent()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        ArchiveFileName archive = await CreateArchiveAsync(environment);
        InsertArchiveIdentity(environment, archive, root);
        Guid archivedHtml = InsertEvent(environment, archive, root, ("HTML Format", "Text"));
        await Service(environment).StartFirstAssignmentAsync(root, DenyHtmlAndPng);

        var crashing = new ProtectedApplicationHistoryPurgeContinuation(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ApplicationHistoryPurgeContinuationCheckpoint.ArchiveDatabaseCommitted)
                {
                    throw new IOException("Simulated crash after the archive commit.");
                }
            });
        await Assert.ThrowsAsync<IOException>(() => crashing.ResumeAsync(DeletionDate));
        Assert.Equal(0, EventCount(environment, archive, archivedHtml));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));

        ApplicationHistoryPurgeResumeResult retry = await Continuation(environment).ResumeAsync(DeletionDate);

        Assert.True(retry.ArchiveSummary.IsEmpty);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Resume_DispatchedFromTheGenericResumeEntryPoint()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        await Service(environment).StartFirstAssignmentAsync(root, DenyHtmlAndPng);

        PolicyMaintenanceResumeDispatchResult result =
            await new ProtectedPolicyMaintenanceResumeDispatcher(environment.Session, environment.Factory)
                .ResumeAsync(DeletionDate);

        Assert.True(result.HadPendingOperation);
        Assert.NotNull(result.HistoryPurge);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Resume_FailsClosedWhenTheRootPolicyChangedWhilePending()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        await Service(environment).StartFirstAssignmentAsync(root, DenyHtmlAndPng);
        environment.Execute($"UPDATE ApplicationCapturePolicy SET CaptureRule = 'Allow' WHERE ApplicationId = '{root}';");

        await Assert.ThrowsAsync<InvalidDataException>(() => Continuation(environment).ResumeAsync(DeletionDate));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Start_RejectsConfiguredRootsMembersAndPendingMarkersWithoutWriting()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId configured = InsertIdentity(environment);
        DurableApplicationId member = InsertIdentity(environment);
        DurableApplicationId unconfigured = InsertIdentity(environment);
        environment.Execute($"INSERT INTO ApplicationCapturePolicy VALUES ('{configured}', 'Allow');");
        AddMember(environment, member, configured);
        Guid memberHtml = InsertEvent(environment, null, member, ("HTML Format", "Text"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).StartFirstAssignmentAsync(configured, DenyHtmlAndPng));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(environment).StartFirstAssignmentAsync(member, DenyHtmlAndPng));

        await Service(environment).StartFirstAssignmentAsync(unconfigured, DenyHtmlAndPng);
        DurableApplicationId another = InsertIdentity(environment);
        await Assert.ThrowsAsync<PendingPolicyMaintenanceException>(() =>
            Service(environment).StartFirstAssignmentAsync(another, DenyHtmlAndPng));

        Assert.Equal(1, PayloadCount(environment, null, memberHtml));
        Assert.Equal(0, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationCapturePolicy WHERE ApplicationId IN ('{member}', '{another}');"));
    }

    [Fact]
    public async Task Start_FailureBeforeCommitLeavesNoPolicyMarkerOrDeletion()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        Guid html = InsertEvent(environment, null, root, ("HTML Format", "Text"));
        var failing = new ProtectedApplicationHistoryPurgeService(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (checkpoint == ApplicationHistoryPurgeStartCheckpoint.BeforeCommit)
                {
                    throw new IOException("Simulated failure before COMMIT.");
                }
            });

        await Assert.ThrowsAsync<IOException>(() => failing.StartFirstAssignmentAsync(root, DenyHtmlAndPng));

        Assert.Equal(1, PayloadCount(environment, null, html));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationCapturePolicy;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task Purge_OverwritesDeletedContentInTheDatabaseFile()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(Global);
        DurableApplicationId root = InsertIdentity(environment);
        const string Secret = "PURGE-ME-7f3c2a91-secret-content";
        InsertEvent(environment, null, root, ("HTML Format", "Text"), secretText: Secret);
        Assert.Contains(Secret, ReadDatabaseText(environment.CurrentPath), StringComparison.Ordinal);

        await Service(environment).StartFirstAssignmentAsync(root, DenyHtmlAndPng);

        Assert.DoesNotContain(Secret, ReadDatabaseText(environment.CurrentPath), StringComparison.Ordinal);
    }

    private static ProtectedApplicationHistoryPurgeService Service(GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static ProtectedApplicationHistoryPurgeContinuation Continuation(GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static ClipboardCapturePolicy Policy(
        ClipboardCapturePolicyRule capture,
        params (string Name, ClipboardCapturePolicyRule Rule)[] formats) =>
        new(
            capture,
            formats.ToDictionary(
                item => item.Name,
                item => new ClipboardFormatCapturePolicy(item.Rule),
                StringComparer.Ordinal));

    private static string ReadDatabaseText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}
