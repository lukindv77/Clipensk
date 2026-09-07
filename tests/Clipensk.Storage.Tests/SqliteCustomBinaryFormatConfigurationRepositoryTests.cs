using System.Data;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteCustomBinaryFormatConfigurationRepositoryTests
{
    [Fact]
    public async Task MissingMapping_IsNullAndProviderFailsClosed()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        Assert.Null(await environment.CustomBinaryConfigurations.ReadFileExtensionAsync("Contoso.Custom"));
        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);

        var provider = new RepositoryClipboardCustomBinaryFileExtensionProvider(
            environment.CustomBinaryConfigurations);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await provider.GetExtensionAsync("Contoso.Custom"));
    }

    [Fact]
    public async Task Initialize_NormalizesAndRoundTripsAcrossSessionsWithOrdinalFormatNames()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.CustomBinaryConfigurations.InitializeAsync("Contoso.Custom", " DAT ");
        Assert.Equal(".dat", await environment.CustomBinaryConfigurations.ReadFileExtensionAsync("Contoso.Custom"));
        Assert.Null(await environment.CustomBinaryConfigurations.ReadFileExtensionAsync("contoso.custom"));

        environment.ReopenSession();
        var provider = new RepositoryClipboardCustomBinaryFileExtensionProvider(
            environment.CustomBinaryConfigurations);
        Assert.Equal(".dat", await provider.GetExtensionAsync("Contoso.Custom"));
    }

    [Fact]
    public async Task Initialize_RejectsRebindEvenToSameExtension()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.CustomBinaryConfigurations.InitializeAsync("Contoso.Custom", ".dat");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await environment.CustomBinaryConfigurations.InitializeAsync("Contoso.Custom", ".dat"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await environment.CustomBinaryConfigurations.InitializeAsync("Contoso.Custom", ".bin"));
        Assert.Equal(".dat", await environment.CustomBinaryConfigurations.ReadFileExtensionAsync("Contoso.Custom"));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("../escape.bin")]
    [InlineData(@".\..\escape.bin")]
    public async Task Initialize_RejectsInvalidExtensionBeforeOpening(string extension)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await environment.CustomBinaryConfigurations.InitializeAsync("Contoso.Custom", extension));
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
        var repository = environment.CustomBinaryConfigurations;
        if (reason == "caller") cancellation.Cancel();
        if (reason == "lock") Assert.True(environment.Lifecycle.TryBeginLock());
        if (reason == "dispose") environment.Session.Dispose();
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.ReadFileExtensionAsync("Contoso.Custom", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.InitializeAsync("Contoso.Custom", ".dat", cancellation.Token));
        Assert.Empty(environment.Factory.Modes);
    }

    [Fact]
    public async Task CancellationAtOpen_ClosesConnection()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        environment.Factory.OnOpen = (_, _) => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await environment.CustomBinaryConfigurations.ReadFileExtensionAsync(
                "Contoso.Custom", cancellation.Token));
        Assert.Equal(ConnectionState.Closed, environment.Factory.LastConnection!.State);
    }

    [Fact]
    public async Task CancellationDuringInsert_RollsBackAndAllowsRetry()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_custom_extension AFTER INSERT ON CustomBinaryFormatConfiguration
            BEGIN SELECT cancel_setup(); END;
            """);
        environment.Factory.OnOpen = (connection, _) => connection.CreateFunction("cancel_setup", () =>
        {
            cancellation.Cancel();
            return 0;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await environment.CustomBinaryConfigurations.InitializeAsync(
                "Contoso.Custom", ".dat", cancellation.Token));
        environment.Factory.OnOpen = null;
        Assert.Null(await environment.CustomBinaryConfigurations.ReadFileExtensionAsync("Contoso.Custom"));
        environment.Execute("DROP TRIGGER cancel_custom_extension;");
        await environment.CustomBinaryConfigurations.InitializeAsync("Contoso.Custom", ".dat");
        Assert.Equal(".dat", await environment.CustomBinaryConfigurations.ReadFileExtensionAsync("Contoso.Custom"));
    }

    [Fact]
    public async Task MalformedPersistedExtension_IsRejected()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            PRAGMA ignore_check_constraints = ON;
            INSERT INTO CustomBinaryFormatConfiguration VALUES ('Contoso.Custom', 'DAT');
            """);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await environment.CustomBinaryConfigurations.ReadFileExtensionAsync("Contoso.Custom"));
    }
}
