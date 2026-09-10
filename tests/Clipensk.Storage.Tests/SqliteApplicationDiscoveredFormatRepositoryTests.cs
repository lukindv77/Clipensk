using Clipensk.Core.Applications;
using Clipensk.Storage.Applications;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteApplicationDiscoveredFormatRepositoryTests
{
    [Fact]
    public async Task ObserveAsync_DedupesNamesAndTracksEarliestAndLatestObservation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ApplicationId applicationId = InsertApplication(environment);
        var repository = new SqliteApplicationDiscoveredFormatRepository(
            environment.Session,
            environment.Factory);
        DateTimeOffset middle = Utc(2026, 1, 2);
        DateTimeOffset earlier = Utc(2026, 1, 1);
        DateTimeOffset later = Utc(2026, 1, 3);

        await repository.ObserveAsync(
            applicationId,
            ["Private.Z", "Text", "Private.Z"],
            middle);
        await repository.ObserveAsync(applicationId, ["Private.Z"], earlier);
        await repository.ObserveAsync(
            applicationId,
            ["Private.Z", "HTML Format"],
            later);

        IReadOnlyList<ApplicationDiscoveredFormat> formats =
            await repository.ListAsync(applicationId);

        Assert.Equal(["HTML Format", "Private.Z", "Text"],
            formats.Select(format => format.FormatName));
        Assert.Equal(later, formats[0].FirstSeenAtUtc);
        Assert.Equal(later, formats[0].LastSeenAtUtc);
        Assert.Equal(earlier, formats[1].FirstSeenAtUtc);
        Assert.Equal(later, formats[1].LastSeenAtUtc);
        Assert.Equal(middle, formats[2].FirstSeenAtUtc);
        Assert.Equal(middle, formats[2].LastSeenAtUtc);
        Assert.All(formats, format => Assert.Equal(applicationId, format.ApplicationId));
    }

    [Fact]
    public async Task ObserveAsync_PreservesBinaryCaseDistinctFormatNames()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ApplicationId applicationId = InsertApplication(environment);
        var repository = new SqliteApplicationDiscoveredFormatRepository(
            environment.Session,
            environment.Factory);

        await repository.ObserveAsync(
            applicationId,
            ["private.format", "Private.Format"],
            Utc(2026, 2, 1));

        IReadOnlyList<ApplicationDiscoveredFormat> formats =
            await repository.ListAsync(applicationId);
        Assert.Equal(["Private.Format", "private.format"],
            formats.Select(format => format.FormatName));
    }

    [Fact]
    public async Task ObserveAsync_EmptyCollectionDoesNotOpenDatabase()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqliteApplicationDiscoveredFormatRepository(
            environment.Session,
            environment.Factory);
        environment.Factory.Modes.Clear();

        await repository.ObserveAsync(
            new ApplicationId(Guid.NewGuid()),
            [],
            Utc(2026, 3, 1));

        Assert.Empty(environment.Factory.Modes);
    }

    [Fact]
    public async Task ObserveAsync_UnknownApplicationIsRejectedByForeignKey()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqliteApplicationDiscoveredFormatRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<SqliteException>(() => repository.ObserveAsync(
            new ApplicationId(Guid.NewGuid()),
            ["Private.Format"],
            Utc(2026, 4, 1)).AsTask());
    }

    [Fact]
    public async Task ObserveAsync_CallerCancellationPreventsWrite()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ApplicationId applicationId = InsertApplication(environment);
        var repository = new SqliteApplicationDiscoveredFormatRepository(
            environment.Session,
            environment.Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ObserveAsync(
            applicationId,
            ["Private.Format"],
            Utc(2026, 5, 1),
            cancellation.Token).AsTask());

        Assert.Empty(environment.Factory.Modes);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationDiscoveredFormat;"));
    }

    [Fact]
    public async Task ListAsync_RejectsNonCanonicalPersistedUtcTimestamp()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ApplicationId applicationId = InsertApplication(environment);
        environment.Execute($"""
            INSERT INTO ApplicationDiscoveredFormat (
                ApplicationId, FormatName, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES (
                '{applicationId}',
                'Private.Format',
                '2026-06-01T00:00:00Z',
                '2026-06-01T00:00:00Z');
            """);
        var repository = new SqliteApplicationDiscoveredFormatRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ListAsync(applicationId).AsTask());
    }

    [Fact]
    public async Task ListAsync_RejectsLastSeenEarlierThanFirstSeen()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ApplicationId applicationId = InsertApplication(environment);
        environment.Execute($"""
            INSERT INTO ApplicationDiscoveredFormat (
                ApplicationId, FormatName, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES (
                '{applicationId}',
                'Private.Format',
                '2026-07-02T00:00:00.0000000+00:00',
                '2026-07-01T00:00:00.0000000+00:00');
            """);
        var repository = new SqliteApplicationDiscoveredFormatRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ListAsync(applicationId).AsTask());
    }

    private static ApplicationId InsertApplication(GlobalPolicyTestEnvironment environment)
    {
        var applicationId = new ApplicationId(Guid.NewGuid());
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{applicationId}', '2026-01-01T00:00:00.0000000+00:00');
            """);
        return applicationId;
    }

    private static DateTimeOffset Utc(int year, int month, int day) =>
        new(year, month, day, 12, 0, 0, TimeSpan.Zero);
}
