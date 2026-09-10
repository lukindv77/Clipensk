namespace Clipensk.Core.Applications;

public interface IApplicationIdentityCatalog
{
    ValueTask<IReadOnlyList<ApplicationIdentityCatalogEntry>> ReadAllAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ApplicationIdentityCatalogEntry
{
    public ApplicationIdentityCatalogEntry(
        ApplicationId applicationId,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<string> applicationUserModelIds,
        IReadOnlyList<string> executablePaths)
    {
        ApplicationId = applicationId ?? throw new ArgumentNullException(nameof(applicationId));
        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Application identity creation time must be expressed in UTC.",
                nameof(createdAtUtc));
        }

        CreatedAtUtc = createdAtUtc;
        ApplicationUserModelIds = SnapshotAliases(
            applicationUserModelIds,
            nameof(applicationUserModelIds));
        ExecutablePaths = SnapshotAliases(
            executablePaths,
            nameof(executablePaths));
    }

    public ApplicationId ApplicationId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<string> ApplicationUserModelIds { get; }

    public IReadOnlyList<string> ExecutablePaths { get; }

    private static IReadOnlyList<string> SnapshotAliases(
        IReadOnlyList<string> aliases,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(aliases, parameterName);
        string[] snapshot = aliases.ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Application identity aliases must be non-empty exact values.",
                parameterName);
        }
        if (snapshot.Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Application identity aliases must not contain duplicates.",
                parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }
}
