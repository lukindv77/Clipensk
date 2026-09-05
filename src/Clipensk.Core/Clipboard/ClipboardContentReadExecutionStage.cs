namespace Clipensk.Core.Clipboard;

public sealed class ClipboardContentReadExecutionStage
{
    private readonly IClipboardTextContentReader _textReader;
    private readonly IClipboardPngImageContentReader _pngImageReader;
    private readonly IClipboardLinkContentReader _linkReader;
    private readonly IClipboardStorageItemsContentReader _storageItemsReader;

    public ClipboardContentReadExecutionStage(
        IClipboardTextContentReader textReader,
        IClipboardPngImageContentReader pngImageReader,
        IClipboardLinkContentReader linkReader,
        IClipboardStorageItemsContentReader storageItemsReader)
    {
        _textReader = textReader ?? throw new ArgumentNullException(nameof(textReader));
        _pngImageReader = pngImageReader ?? throw new ArgumentNullException(nameof(pngImageReader));
        _linkReader = linkReader ?? throw new ArgumentNullException(nameof(linkReader));
        _storageItemsReader = storageItemsReader ?? throw new ArgumentNullException(nameof(storageItemsReader));
    }

    public async ValueTask<ClipboardContentReadExecution> ExecuteAsync(
        ClipboardContentReadPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        IClipboardContentSnapshot? contentSnapshot = plan.Selection.Snapshot.ContentSnapshot;
        if (plan.Routes.Count > 0 && contentSnapshot is null)
        {
            throw new InvalidOperationException(
                "Clipboard content read plan contains routes but has no retained content snapshot.");
        }

        var capturedContent = new List<ClipboardCapturedContent>();
        var sizeRejectedFormats = new List<ClipboardSelectedFormat>();
        var deferredFormats = new List<ClipboardSelectedFormat>();

        foreach (ClipboardContentReaderRoute route in plan.Routes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ClipboardSelectedFormat selectedFormat = route.SelectedFormat;
            string formatName = selectedFormat.FormatName;

            switch (route.ReaderKind)
            {
                case ClipboardContentReaderKind.Text:
                {
                    EnsureSupported(_textReader.SupportsFormat(formatName), route);
                    string value = await _textReader.ReadAsync(contentSnapshot!, formatName);
                    cancellationToken.ThrowIfCancellationRequested();
                    long byteCount = ClipboardCanonicalPayloadSize.MeasureUtf8Text(value);
                    if (ClipboardCanonicalPayloadSize.IsWithinLimit(byteCount, selectedFormat.MaxBytes))
                    {
                        capturedContent.Add(new ClipboardCapturedTextContent(route, value, byteCount));
                    }
                    else
                    {
                        sizeRejectedFormats.Add(selectedFormat);
                    }

                    break;
                }

                case ClipboardContentReaderKind.PngImage:
                {
                    EnsureSupported(_pngImageReader.SupportsFormat(formatName), route);
                    byte[] pngBytes = await _pngImageReader.ReadNormalizedPngAsync(contentSnapshot!, formatName);
                    cancellationToken.ThrowIfCancellationRequested();
                    long byteCount = ClipboardCanonicalPayloadSize.MeasureBinary(pngBytes);
                    if (ClipboardCanonicalPayloadSize.IsWithinLimit(byteCount, selectedFormat.MaxBytes))
                    {
                        capturedContent.Add(new ClipboardCapturedPngImageContent(route, pngBytes));
                    }
                    else
                    {
                        sizeRejectedFormats.Add(selectedFormat);
                    }

                    break;
                }

                case ClipboardContentReaderKind.Link:
                {
                    EnsureSupported(_linkReader.SupportsFormat(formatName), route);
                    Uri value = await _linkReader.ReadAsync(contentSnapshot!, formatName);
                    cancellationToken.ThrowIfCancellationRequested();
                    long byteCount = ClipboardCanonicalPayloadSize.MeasureLink(value);
                    if (ClipboardCanonicalPayloadSize.IsWithinLimit(byteCount, selectedFormat.MaxBytes))
                    {
                        capturedContent.Add(new ClipboardCapturedLinkContent(route, value, byteCount));
                    }
                    else
                    {
                        sizeRejectedFormats.Add(selectedFormat);
                    }

                    break;
                }

                case ClipboardContentReaderKind.StorageItems:
                {
                    if (selectedFormat.MaxBytes.HasValue)
                    {
                        deferredFormats.Add(selectedFormat);
                        break;
                    }

                    EnsureSupported(_storageItemsReader.SupportsFormat(formatName), route);
                    IReadOnlyList<ClipboardStorageItemMetadata> items =
                        await _storageItemsReader.ReadAsync(contentSnapshot!, formatName);
                    cancellationToken.ThrowIfCancellationRequested();
                    capturedContent.Add(new ClipboardCapturedStorageItemsContent(route, items));
                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(route),
                        route.ReaderKind,
                        "Unsupported clipboard content reader kind.");
            }
        }

        return new ClipboardContentReadExecution(
            plan,
            capturedContent,
            sizeRejectedFormats,
            deferredFormats);
    }

    private static void EnsureSupported(bool supported, ClipboardContentReaderRoute route)
    {
        if (!supported)
        {
            throw new InvalidOperationException(
                $"Clipboard reader route for '{route.SelectedFormat.FormatName}' no longer matches its reader capability.");
        }
    }
}
