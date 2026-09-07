namespace Clipensk.Core.History;

public enum ClipboardHistoryPhysicalLocationKind
{
    Current = 0,
    Archive = 1,
}

public sealed record ClipboardHistoryPhysicalLocation
{
    public ClipboardHistoryPhysicalLocation(
        ClipboardHistoryPhysicalLocationKind kind,
        Guid? databaseId = null,
        string? fileName = null)
    {
        if (kind == ClipboardHistoryPhysicalLocationKind.Current)
        {
            if (databaseId is not null || fileName is not null)
            {
                throw new ArgumentException(
                    "Current history location must not carry archive identity metadata.");
            }
        }
        else if (kind == ClipboardHistoryPhysicalLocationKind.Archive)
        {
            if (databaseId is not Guid archiveDatabaseId || archiveDatabaseId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Archive history location requires a non-empty DatabaseId.",
                    nameof(databaseId));
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        DatabaseId = databaseId;
        FileName = fileName;
    }

    public ClipboardHistoryPhysicalLocationKind Kind { get; }

    public Guid? DatabaseId { get; }

    public string? FileName { get; }

    public static ClipboardHistoryPhysicalLocation Current { get; } =
        new(ClipboardHistoryPhysicalLocationKind.Current);

    public static ClipboardHistoryPhysicalLocation Archive(
        Guid databaseId,
        string fileName) =>
        new(ClipboardHistoryPhysicalLocationKind.Archive, databaseId, fileName);
}

public sealed record UnifiedClipboardHistoryEntry
{
    public UnifiedClipboardHistoryEntry(
        ClipboardHistoryEntry entry,
        IReadOnlyList<ClipboardHistoryPhysicalLocation> locations)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        ArgumentNullException.ThrowIfNull(locations);
        if (locations.Count == 0)
        {
            throw new ArgumentException(
                "A unified history entry must have at least one physical location.",
                nameof(locations));
        }

        ClipboardHistoryPhysicalLocation[] snapshot = locations.ToArray();
        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "A unified history entry cannot contain duplicate physical locations.",
                nameof(locations));
        }
        if (snapshot.Count(item => item.Kind == ClipboardHistoryPhysicalLocationKind.Current) > 1)
        {
            throw new ArgumentException(
                "A unified history entry cannot contain multiple Current locations.",
                nameof(locations));
        }

        Locations = Array.AsReadOnly(snapshot);
    }

    public ClipboardHistoryEntry Entry { get; }

    public IReadOnlyList<ClipboardHistoryPhysicalLocation> Locations { get; }
}
