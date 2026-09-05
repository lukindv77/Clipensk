namespace Clipensk.Core.Clipboard;

public sealed class ClipboardAcceptedCaptureStage
{
    public ClipboardAcceptedCapture? Create(ClipboardContentReadExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (execution.CapturedContent.Count == 0)
        {
            return null;
        }

        ClipboardCaptureContext captureContext =
            execution.Plan.Selection.Snapshot.PolicyContext.CaptureContext;

        return new ClipboardAcceptedCapture(
            captureContext,
            execution.CapturedContent);
    }
}
