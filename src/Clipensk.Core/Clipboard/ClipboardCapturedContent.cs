namespace Clipensk.Core.Clipboard;

public abstract class ClipboardCapturedContent
{
    protected ClipboardCapturedContent(
        ClipboardContentReaderRoute route,
        long? canonicalByteCount)
    {
        Route = route;
        CanonicalByteCount = canonicalByteCount;
    }

    public ClipboardContentReaderRoute Route { get; }

    public ClipboardSelectedFormat SelectedFormat => Route.SelectedFormat;

    public long? CanonicalByteCount { get; }
}

public sealed class ClipboardCapturedTextContent : ClipboardCapturedContent
{
    public ClipboardCapturedTextContent(
        ClipboardContentReaderRoute route,
        string value,
        long canonicalByteCount,
        string? searchText = null)
        : base(route, canonicalByteCount)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        SearchText = searchText;
    }

    public string Value { get; }

    public string? SearchText { get; }
}

public sealed class ClipboardCapturedLinkContent : ClipboardCapturedContent
{
    public ClipboardCapturedLinkContent(
        ClipboardContentReaderRoute route,
        Uri value,
        long canonicalByteCount)
        : base(route, canonicalByteCount)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public Uri Value { get; }
}

public sealed class ClipboardCapturedPngImageContent : ClipboardCapturedContent
{
    private readonly byte[] _pngBytes;

    public ClipboardCapturedPngImageContent(
        ClipboardContentReaderRoute route,
        ReadOnlySpan<byte> pngBytes)
        : base(route, pngBytes.Length)
    {
        _pngBytes = pngBytes.ToArray();
    }

    public ReadOnlyMemory<byte> PngBytes => _pngBytes;
}

public sealed class ClipboardCapturedCustomBinaryContent : ClipboardCapturedContent
{
    private readonly byte[] _bytes;

    public ClipboardCapturedCustomBinaryContent(
        ClipboardContentReaderRoute route,
        ReadOnlySpan<byte> bytes)
        : base(route, bytes.Length)
    {
        _bytes = bytes.ToArray();
    }

    public ReadOnlyMemory<byte> Bytes => _bytes;
}

public sealed class ClipboardCapturedStorageItemsContent : ClipboardCapturedContent
{
    public ClipboardCapturedStorageItemsContent(
        ClipboardContentReaderRoute route,
        IEnumerable<ClipboardStorageItemMetadata> items)
        : base(route, canonicalByteCount: null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = Array.AsReadOnly(items.ToArray());
    }

    public IReadOnlyList<ClipboardStorageItemMetadata> Items { get; }
}
