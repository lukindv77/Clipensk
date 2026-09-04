namespace Clipensk.Core.History;

public readonly record struct JournalDateRange
{
    public JournalDateRange(DateOnly startDate, DateOnly endDate)
    {
        if (endDate < startDate)
        {
            throw new ArgumentOutOfRangeException(nameof(endDate), "Дата окончания периода не может быть раньше даты начала.");
        }

        StartDate = startDate;
        EndDate = endDate;
    }

    public DateOnly StartDate { get; }

    public DateOnly EndDate { get; }

    public bool Contains(DateOnly date) => date >= StartDate && date <= EndDate;

    public bool Intersects(JournalDateRange other)
    {
        return StartDate <= other.EndDate && other.StartDate <= EndDate;
    }
}
