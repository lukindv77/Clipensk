namespace Clipensk.Core.Applications;

public sealed record ApplicationIdentitySummary(
    ApplicationId ApplicationId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> ApplicationUserModelIds,
    IReadOnlyList<string> ExecutablePaths);

public interface IApplicationIdentityReadRepository
{
    ValueTask<IReadOnlyList<ApplicationIdentitySummary>> ListAsync(
        CancellationToken cancellationToken = default);
}
