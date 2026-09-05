namespace Clipensk.Core.Applications;

public sealed class ApplicationIdentityConflictException : InvalidOperationException
{
    public ApplicationIdentityConflictException(
        ApplicationIdentityObservation observation,
        ApplicationId? applicationUserModelIdApplicationId,
        ApplicationId? executablePathApplicationId)
        : base("Application identity aliases resolve to conflicting durable application identities.")
    {
        Observation = observation;
        ApplicationUserModelIdApplicationId = applicationUserModelIdApplicationId;
        ExecutablePathApplicationId = executablePathApplicationId;
    }

    public ApplicationIdentityObservation Observation { get; }

    public ApplicationId? ApplicationUserModelIdApplicationId { get; }

    public ApplicationId? ExecutablePathApplicationId { get; }
}
