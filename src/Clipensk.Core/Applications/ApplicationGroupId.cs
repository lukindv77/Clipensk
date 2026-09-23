namespace Clipensk.Core.Applications;

/// <summary>
/// Durable key of a user application group, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2. The
/// default group is the global capture policy and has no <see cref="ApplicationGroupId"/>.
/// </summary>
public sealed record ApplicationGroupId
{
    public ApplicationGroupId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("ApplicationGroupId cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ApplicationGroupId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
