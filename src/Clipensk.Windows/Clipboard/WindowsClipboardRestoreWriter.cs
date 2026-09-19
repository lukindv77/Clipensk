using Clipensk.Core.History;
using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using WindowsClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipensk.Windows.Clipboard;

/// <summary>
/// Publishes a prepared <see cref="ClipboardRestorePlan"/> to the Windows clipboard.
///
/// Stored format names are already the WinRT identifiers the capture readers observed, so they are
/// republished verbatim and capture and restore cannot drift apart over a translation table.
/// </summary>
public sealed class WindowsClipboardRestoreWriter
{
    private readonly ClipboardUpdateMonitor _monitor;

    internal WindowsClipboardRestoreWriter(ClipboardUpdateMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    /// <summary>The WinRT identifier a plan must use for its plain-text item.</summary>
    public static string PlainTextFormatName => StandardDataFormats.Text;

    /// <summary>
    /// Must be called on the thread that owns the resident message window.
    ///
    /// Every file is resolved before anything is published, so the publish itself and the sequence
    /// number it produced are read with no <c>await</c> between them. That ordering is what makes
    /// self-write suppression reliable: the pending <c>WM_CLIPBOARDUPDATE</c> cannot be dispatched
    /// until this thread returns to the message loop.
    ///
    /// The package is deliberately not flushed. A flush would re-render the content and raise a
    /// second clipboard update that the armed suppression is not for, and Clipensk is resident, so
    /// it stays available to serve the package for as long as the application runs.
    /// </summary>
    public async Task WriteAsync(
        ClipboardRestorePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var resolved = new List<(ClipboardRestoreItem Item, RandomAccessStreamReference? Stream)>(
            plan.Items.Count);
        foreach (ClipboardRestoreItem item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved.Add((item, await ResolveStreamAsync(item).ConfigureAwait(true)));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        foreach ((ClipboardRestoreItem item, RandomAccessStreamReference? stream) in resolved)
        {
            switch (item.Kind)
            {
                case ClipboardHistoryPayloadKind.Text:
                    package.SetData(item.FormatName, RequireText(item));
                    break;

                case ClipboardHistoryPayloadKind.Link:
                    package.SetData(item.FormatName, new Uri(RequireText(item)));
                    break;

                case ClipboardHistoryPayloadKind.PngImage:
                    package.SetBitmap(RequireStream(item, stream));
                    break;

                case ClipboardHistoryPayloadKind.CustomBinary:
                    package.SetData(item.FormatName, RequireStream(item, stream));
                    break;

                default:
                    throw new InvalidDataException(
                        $"Clipboard restore cannot publish payload kind '{item.Kind}'.");
            }
        }

        WindowsClipboard.SetContent(package);
        _monitor.SuppressSelfWrite(ClipboardUpdateMonitor.ReadClipboardSequenceNumber());
    }

    private static async Task<RandomAccessStreamReference?> ResolveStreamAsync(
        ClipboardRestoreItem item)
    {
        if (item.Kind is not ClipboardHistoryPayloadKind.PngImage
            and not ClipboardHistoryPayloadKind.CustomBinary)
        {
            return null;
        }

        if (item.ExternalFilePath is not { Length: > 0 } path)
        {
            throw new InvalidDataException(
                $"Payload '{item.FormatName}' is external but carries no verified file path.");
        }

        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        return RandomAccessStreamReference.CreateFromFile(file);
    }

    private static string RequireText(ClipboardRestoreItem item) =>
        item.InlineCanonicalText
            ?? throw new InvalidDataException(
                $"Payload '{item.FormatName}' is inline but carries no canonical text.");

    private static RandomAccessStreamReference RequireStream(
        ClipboardRestoreItem item,
        RandomAccessStreamReference? stream) =>
        stream
            ?? throw new InvalidDataException(
                $"Payload '{item.FormatName}' is external but was not resolved to a stream.");
}
