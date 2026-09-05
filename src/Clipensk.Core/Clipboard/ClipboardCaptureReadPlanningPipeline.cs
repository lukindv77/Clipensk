namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCaptureReadPlanningPipeline
{
    private readonly ClipboardCapturePipeline _capturePipeline;
    private readonly ClipboardContentReadPlanStage _readPlanStage;

    public ClipboardCaptureReadPlanningPipeline(
        ClipboardCapturePipeline capturePipeline,
        ClipboardContentReadPlanStage readPlanStage)
    {
        _capturePipeline = capturePipeline ?? throw new ArgumentNullException(nameof(capturePipeline));
        _readPlanStage = readPlanStage ?? throw new ArgumentNullException(nameof(readPlanStage));
    }

    public async ValueTask<ClipboardContentReadPlan> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        ClipboardFormatSelection selection = await _capturePipeline
            .ProcessNextAsync(cancellationToken)
            .ConfigureAwait(false);

        return _readPlanStage.Create(selection);
    }
}
