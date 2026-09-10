namespace Clipensk.Core.Applications;

public sealed record ApplicationDiscoveredFormat(
    ApplicationId ApplicationId,
    string FormatName,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc);

public interface IApplicationDiscoveredFormatRepository
{
    ValueTask ObserveAsync(
        ApplicationId applicationId,
        IReadOnlyCollection<string> formatNames,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ApplicationDiscoveredFormat>> ListAsync(
        ApplicationId applicationId,
        CancellationToken cancellationToken = default);
}
