using System.Data;
using Clipensk.Core.Clipboard;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteGlobalClipboardCapturePolicyRepositoryTests
{
    private static ClipboardCapturePolicy ExplicitPolicy() => new(
        ClipboardCapturePolicyRule.Allow,
        new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
        {
            ["Text"] = new(ClipboardCapturePolicyRule.Allow, 8192),
            ["text"] = new(ClipboardCapturePolicyRule.Deny),
            ["HTML Format"] = new(ClipboardCapturePolicyRule.Deny, 4096),
            ["Contoso.Custom"] = new(ClipboardCapturePolicyRule.Allow),
        });

    [Fact]
    public async Task NewStorage_IsUnconfiguredAndConstructorDoesNotOpenDatabase()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        var repository = environment.Repository;
        Assert.Empty(environment.Factory.Modes);
        Assert.Null(await repository.ReadAsync());
        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalCapturePolicy;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalFormatCapturePolicy;"));
    }

    [Fact]
    public async Task Initialize_PersistsExactRulesAndLimitsAcrossSessionsAndIsStorageScoped()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var otherStorage = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        await environment.Repository.InitializeAsync(ExplicitPolicy());
        Assert.Equal(new[] { SqliteOpenMode.ReadWrite }, environment.Factory.Modes);
        environment.ReopenSession();
        ClipboardCapturePolicy? stored = await environment.Repository.ReadAsync();
        Assert.NotNull(stored);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, stored.Capture);
        Assert.Equal(4, stored.Formats.Count);
        foreach (var pair in ExplicitPolicy().Formats)
        {
            Assert.Equal(pair.Value, stored.Formats[pair.Key]);
        }
        Assert.Null(await otherStorage.Repository.ReadAsync());
    }

    [Fact]
    public async Task ExplicitDeny_IsConfiguredAndCannotBeReplacedEvenBySameValue()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var deny = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        await environment.Repository.InitializeAsync(deny);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await environment.Repository.InitializeAsync(deny));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await environment.Repository.InitializeAsync(ExplicitPolicy()));
        ClipboardCapturePolicy? stored = await environment.Repository.ReadAsync();
        Assert.NotNull(stored);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, stored.Capture);
        Assert.Empty(stored.Formats);
    }

    [Fact]
    public async Task ConcurrentFirstSetup_ExactlyOnePolicyWinsWithoutMixingFormats()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        async Task<bool> Attempt(ClipboardCapturePolicy policy)
        {
            try
            {
                // Separate factories avoid sharing test instrumentation across threads.
                var repository = new Clipensk.Storage.Clipboard.SqliteGlobalClipboardCapturePolicyRepository(
                    environment.Session, new GlobalPolicyTestEnvironment.TestConnectionFactory());
                await repository.InitializeAsync(policy);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        bool[] results = await Task.WhenAll(
            Task.Run(() => Attempt(ExplicitPolicy())),
            Task.Run(() => Attempt(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny))));
        Assert.Single(results.Where(won => won));
        ClipboardCapturePolicy? stored = await environment.Repository.ReadAsync();
        Assert.NotNull(stored);
        Assert.Equal(stored.Capture == ClipboardCapturePolicyRule.Allow ? 4 : 0, stored.Formats.Count);
    }

    [Theory]
    [InlineData(ClipboardCapturePolicyRule.Inherit, false)]
    [InlineData((ClipboardCapturePolicyRule)99, false)]
    [InlineData(ClipboardCapturePolicyRule.Inherit, true)]
    [InlineData((ClipboardCapturePolicyRule)99, true)]
    public async Task Initialize_RejectsUnresolvedOrUnknownRulesBeforeOpening(ClipboardCapturePolicyRule rule, bool format)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var policy = format
            ? new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow,
                new Dictionary<string, ClipboardFormatCapturePolicy> { ["Text"] = new(rule) })
            : new ClipboardCapturePolicy(rule);
        environment.Factory.Modes.Clear();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await environment.Repository.InitializeAsync(policy));
        Assert.Empty(environment.Factory.Modes);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("lock")]
    [InlineData("dispose")]
    public async Task RevokedAccess_CancelsReadAndWriteBeforeOpening(string reason)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var repository = environment.Repository;
        if (reason == "caller") cancellation.Cancel();
        if (reason == "lock") Assert.True(environment.Lifecycle.TryBeginLock());
        if (reason == "dispose") environment.Session.Dispose();
        environment.Factory.Modes.Clear();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repository.ReadAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await repository.InitializeAsync(ExplicitPolicy(), cancellation.Token));
        Assert.Empty(environment.Factory.Modes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtOpen_ClosesConnection(bool write)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        environment.Factory.OnOpen = (_, _) => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (write) await environment.Repository.InitializeAsync(ExplicitPolicy(), cancellation.Token);
            else await environment.Repository.ReadAsync(cancellation.Token);
        });
        Assert.Equal(ConnectionState.Closed, environment.Factory.LastConnection!.State);
    }

    [Fact]
    public async Task FormatInsertFailure_RollsBackWholePolicyAndAllowsRetry()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            CREATE TRIGGER fail_format BEFORE INSERT ON GlobalFormatCapturePolicy
            WHEN NEW.FormatName = 'HTML Format' BEGIN SELECT RAISE(ABORT, 'test failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(async () => await environment.Repository.InitializeAsync(ExplicitPolicy()));
        Assert.Null(await environment.Repository.ReadAsync());
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalFormatCapturePolicy;"));
        environment.Execute("DROP TRIGGER fail_format;");
        await environment.Repository.InitializeAsync(ExplicitPolicy());
        Assert.NotNull(await environment.Repository.ReadAsync());
    }

    [Fact]
    public async Task CancellationDuringInsert_RollsBackBeforeCommit()
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
    [InlineData("PRAGMA user_version = 9;")]
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
}
