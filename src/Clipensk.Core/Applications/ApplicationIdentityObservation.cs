namespace Clipensk.Core.Applications;

public readonly record struct ApplicationIdentityObservation(
    string? ApplicationUserModelId,
    string? ExecutablePath)
{
    public bool HasResolvableEvidence =>
        !string.IsNullOrWhiteSpace(ApplicationUserModelId) ||
        !string.IsNullOrWhiteSpace(ExecutablePath);
}
