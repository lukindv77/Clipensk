using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteGlobalClipboardCapturePolicyRepositoryTests
{
    [Fact]
    public async Task Unconfigured_ReadsNullWithoutFallback()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Assert.Null(await environment.Repository.ReadAsync());
    }

    [Fact]
    public async Task Initialize_RoundTripsExplicitPolicyAcrossNewSession()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardCapturePolicy expected = ExplicitPolicy();
        await environment.Repository.InitializeAsync(expected);
        environment.ReopenSession();
        ClipboardCapturePolicy? actual = await environment.Repository.ReadAsync();
        Assert.NotNull(actual);
        Assert.Equal(expected.Capture, actual.Capture);
        Assert.Equal(expected.Formats, actual.Formats);
    }

    [Fact]
    public async Task Initialize_ExplicitDenyIsDurable()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny));
        ClipboardCapturePolicy? actual = await environment.Repository.ReadAsync();
        Assert.NotNull(actual);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, actual.Capture);
        Assert.Empty(actual.Formats);
    }

    [Fact]
    public async Task Initialize_RepeatedWriteIsRejectedWithoutChangingExistingPolicy()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(ExplicitPolicy());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny)));
        ClipboardCapturePolicy? actual = await environment.Repository.ReadAsync();
        Assert.NotNull(actual);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, actual.Capture);
    }

    [Fact]
    public async Task Initialize_DoesNotPersistInheritOrDenySizeDefaults()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var policy = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, null),
                ["Html"] = new(ClipboardCapturePolicyRule.Deny, null),
            });

        await environment.Repository.InitializeAsync(policy);

        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM GlobalCapturePolicy WHERE CaptureRule = 'Inherit';"));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM GlobalFormatCapturePolicy WHERE CaptureRule = 'Inherit';"));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM GlobalFormatCapturePolicy WHERE CaptureRule = 'Deny' AND MaxBytes IS NOT NULL;"));
    }

    [Fact]
    public async Task Initialize_UsesSingleWriteTransactionAndCommitsOnce()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        int beginCount = 0;
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (mode == SqliteOpenMode.ReadWrite)
            {
                connection.CreateFunction("commit_probe", () =>
                {
                    beginCount++;
                    return 0;
                });
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TEMP TRIGGER probe AFTER INSERT ON GlobalCapturePolicy
                    BEGIN SELECT commit_probe(); END;
                    """;
                command.ExecuteNonQuery();
            }
        };

        await environment.Repository.InitializeAsync(ExplicitPolicy());
        environment.Factory.OnOpen = null;
        Assert.Equal(1, beginCount);
        Assert.Single(environment.Factory.Modes.Where(static mode => mode == SqliteOpenMode.ReadWrite));
        Assert.NotNull(await environment.Repository.ReadAsync());
    }

    [Fact]
    public async Task LockCancelsRepositoryAndOldSessionCannotBeReused()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Assert.True(environment.Lifecycle.TryBeginLock());
        environment.Lifecycle.CompleteLock();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await environment.Repository.ReadAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await environment.Repository.InitializeAsync(ExplicitPolicy()));
    }

    [Fact]
    public async Task CallerCancellationStopsReadAndWriteBeforeOpen()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int before = environment.Factory.Modes.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await environment.Repository.ReadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await environment.Repository.InitializeAsync(ExplicitPolicy(), cancellation.Token));
        Assert.Equal(before, environment.Factory.Modes.Count);
    }

    [Fact]
    public async Task CancellationBeforeCommitRollsBackFirstWrite()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_policy AFTER INSERT ON GlobalCapturePolicy
            BEGIN SELECT cancel_setup(); END;
            """);
        environment.Factory.OnOpen = (connection, _) => connection.CreateFunction("cancel_setup", () =>
        {
            cancellation.Cancel();
            return 0;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny), cancellation.Token));
        environment.Factory.OnOpen = null;
        Assert.Null(await environment.Repository.ReadAsync());
    }

    [Theory]
    [InlineData("UPDATE DatabaseIdentity SET StorageId = '00000000-0000-0000-0000-000000000001';")]
    [InlineData("UPDATE DatabaseIdentity SET DatabaseRole = 'StorageCatalog';")]
    [InlineData("UPDATE DatabaseIdentity SET SchemaVersion = 4; PRAGMA user_version = 4;")]
    [InlineData("PRAGMA user_version = 8;")]
    [InlineData("ALTER TABLE GlobalCapturePolicy ADD COLUMN Unexpected TEXT;")]
    public async Task InvalidIdentityOrSchema_FailsReadAndWrite(string corruption)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute(corruption);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await environment.Repository.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await environment.Repository.InitializeAsync(ExplicitPolicy()));
    }

    [Theory]
    [InlineData("PRAGMA ignore_check_constraints = ON; INSERT INTO GlobalCapturePolicy VALUES (1, 'Inherit');")]
    [InlineData("INSERT INTO GlobalCapturePolicy VALUES (1, 'Allow'); PRAGMA ignore_check_constraints = ON; INSERT INTO GlobalFormatCapturePolicy VALUES (1, 'Text', 'Allow', -1);")]
    [InlineData("INSERT INTO GlobalCapturePolicy VALUES (1, 'Allow'); INSERT INTO GlobalFormatCapturePolicy VALUES (1, 'Text', 'Allow', 1.5);")]
    [InlineData("INSERT INTO GlobalCapturePolicy VALUES (1, 'Allow'); INSERT INTO GlobalFormatCapturePolicy VALUES (1, ' ', 'Allow', NULL);")]
    [InlineData("PRAGMA foreign_keys = OFF; INSERT INTO GlobalFormatCapturePolicy VALUES (1, 'Text', 'Allow', NULL);")]
    public async Task MalformedPersistedPolicy_IsNotTreatedAsUnconfigured(string corruption)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute(corruption);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await environment.Repository.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await environment.Repository.InitializeAsync(ExplicitPolicy()));
    }

    private static ClipboardCapturePolicy ExplicitPolicy() => new(
        ClipboardCapturePolicyRule.Allow,
        new Dictionary<string, ClipboardFormatCapturePolicy>
        {
            ["Text"] = new(ClipboardCapturePolicyRule.Allow, 4096),
            ["Html"] = new(ClipboardCapturePolicyRule.Deny, null),
        });
}
