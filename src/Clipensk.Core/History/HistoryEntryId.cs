namespace Clipensk.Core.History;

public sealed record HistoryEntryId
{
    public HistoryEntryId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("HistoryEntryId cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static HistoryEntryId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
