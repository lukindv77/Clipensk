namespace Clipensk.Core.Clipboard;

public sealed record ClipboardContentReadPlan
{
    public ClipboardContentReadPlan(
        ClipboardFormatSelection selection,
        IEnumerable<ClipboardContentReaderRoute> routes,
        IEnumerable<ClipboardSelectedFormat> unsupportedFormats)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(unsupportedFormats);

        Selection = selection;
        Routes = Array.AsReadOnly(routes.ToArray());
        UnsupportedFormats = Array.AsReadOnly(unsupportedFormats.ToArray());
    }

    public ClipboardFormatSelection Selection { get; }

    public IReadOnlyList<ClipboardContentReaderRoute> Routes { get; }

    public IReadOnlyList<ClipboardSelectedFormat> UnsupportedFormats { get; }
}
