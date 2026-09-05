namespace Clipensk.Core.Applications;

public readonly record struct ApplicationId
{
    public ApplicationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("ApplicationId cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ApplicationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
