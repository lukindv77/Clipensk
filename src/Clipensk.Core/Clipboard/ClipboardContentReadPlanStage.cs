namespace Clipensk.Core.Clipboard;

public sealed class ClipboardContentReadPlanStage
{
    private readonly ClipboardContentReaderRouter _router;

    public ClipboardContentReadPlanStage(ClipboardContentReaderRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public ClipboardContentReadPlan Create(ClipboardFormatSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var routes = new List<ClipboardContentReaderRoute>();
        var unsupportedFormats = new List<ClipboardSelectedFormat>();

        foreach (ClipboardSelectedFormat selectedFormat in selection.Formats)
        {
            ClipboardContentReaderRoute? route = _router.TryRoute(selectedFormat);
            if (route.HasValue)
            {
                routes.Add(route.Value);
            }
            else
            {
                unsupportedFormats.Add(selectedFormat);
            }
        }

        return new ClipboardContentReadPlan(selection, routes, unsupportedFormats);
    }
}
