namespace Clipensk.Core.Clipboard;

public sealed class ClipboardAcceptedCaptureDeliveryPipeline
{
    private readonly ClipboardCaptureReadExecutionPipeline _executionPipeline;
    private readonly ClipboardAcceptedCaptureSinkStage _sinkStage;

    public ClipboardAcceptedCaptureDeliveryPipeline(
        ClipboardCaptureReadExecutionPipeline executionPipeline,
        ClipboardAcceptedCaptureSinkStage sinkStage)
    {
        _executionPipeline = executionPipeline
            ?? throw new ArgumentNullException(nameof(executionPipeline));
        _sinkStage = sinkStage ?? throw new ArgumentNullException(nameof(sinkStage));
    }

    public async ValueTask<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        ClipboardContentReadExecution execution = await _executionPipeline
            .ProcessNextAsync(cancellationToken)
            .ConfigureAwait(false);

        return await _sinkStage
            .StoreAsync(execution, cancellationToken)
            .ConfigureAwait(false);
    }
}
