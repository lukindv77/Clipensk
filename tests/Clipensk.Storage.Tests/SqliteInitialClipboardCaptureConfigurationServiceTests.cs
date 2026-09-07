using Clipensk.Core.Clipboard;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteInitialClipboardCaptureConfigurationServiceTests
{
    [Fact]
    public async Task InitializeAsync_WritesPolicyAndCustomExtensionInOneSetup()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var service = new SqliteInitialClipboardCaptureConfigurationService(
            environment.Session,
            environment.Factory);
        ClipboardCapturePolicy policy = CreatePolicy();

        await service.InitializeAsync(
            policy,
            [new InitialCustomBinaryFormatConfiguration("Vendor.Binary", "BIN")]);

        ClipboardCapturePolicy? storedPolicy = await environment.Repository.ReadAsync();
        Assert.NotNull(storedPolicy);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, storedPolicy.Capture);
        Assert.Equal(
            new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Allow, 4096),
            storedPolicy.Formats["Vendor.Binary"]);
        Assert.Equal(
            ".bin",
            await environment.CustomBinaryConfigurations.ReadFileExtensionAsync("Vendor.Binary"));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM GlobalCapturePolicy;"));
        Assert.Equal(2, environment.Scalar("SELECT COUNT(*) FROM GlobalFormatCapturePolicy;"));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM CustomBinaryFormatConfiguration;"));
    }

    [Fact]
    public async Task InitializeAsync_CustomInsertFailure_RollsBackPolicyAndMappings()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (mode != SqliteOpenMode.ReadWrite)
            {
                return;
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER FailInitialCustomBinaryInsert
                BEFORE INSERT ON CustomBinaryFormatConfiguration
                BEGIN
                    SELECT RAISE(ABORT, 'injected custom configuration failure');
                END;
                """;
            command.ExecuteNonQuery();
        };
        var service = new SqliteInitialClipboardCaptureConfigurationService(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<SqliteException>(() => service.InitializeAsync(
            CreatePolicy(),
            [new InitialCustomBinaryFormatConfiguration("Vendor.Binary", ".bin")]).AsTask());

        environment.Factory.OnOpen = null;
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalCapturePolicy;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalFormatCapturePolicy;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM CustomBinaryFormatConfiguration;"));
    }

    [Fact]
    public async Task InitializeAsync_ExistingCustomConfiguration_RejectsWithoutWritingPolicy()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.CustomBinaryConfigurations.InitializeAsync("Vendor.Binary", ".bin");
        var service = new SqliteInitialClipboardCaptureConfigurationService(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync(
            CreatePolicy(),
            [new InitialCustomBinaryFormatConfiguration("Vendor.Binary", ".bin")]).AsTask());

        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalCapturePolicy;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalFormatCapturePolicy;"));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM CustomBinaryFormatConfiguration;"));
    }

    [Fact]
    public async Task InitializeAsync_InvalidCustomConfiguration_FailsBeforeOpeningDatabase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        var service = new SqliteInitialClipboardCaptureConfigurationService(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<ArgumentException>(() => service.InitializeAsync(
            CreatePolicy(),
            [new InitialCustomBinaryFormatConfiguration("WaveAudio", ".wav")]).AsTask());

        Assert.Empty(environment.Factory.Modes);
    }

    [Fact]
    public async Task InitializeAsync_CustomMappingMustBelongToAllowedPolicyFormat()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        var service = new SqliteInitialClipboardCaptureConfigurationService(
            environment.Session,
            environment.Factory);
        var policy = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Deny,
            new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
            {
                ["Vendor.Binary"] = new(ClipboardCapturePolicyRule.Deny),
            });

        await Assert.ThrowsAsync<ArgumentException>(() => service.InitializeAsync(
            policy,
            [new InitialCustomBinaryFormatConfiguration("Vendor.Binary", ".bin")]).AsTask());

        Assert.Empty(environment.Factory.Modes);
    }

    private static ClipboardCapturePolicy CreatePolicy() => new(
        ClipboardCapturePolicyRule.Deny,
        new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
        {
            ["Text"] = new(ClipboardCapturePolicyRule.Allow, 1024),
            ["Vendor.Binary"] = new(ClipboardCapturePolicyRule.Allow, 4096),
        });
}
