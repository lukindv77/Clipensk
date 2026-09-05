namespace Clipensk.Core.Clipboard;

public sealed class ClipboardCaptureReadExecutionPipeline
{
    private readonly ClipboardCaptureReadPlanningPipeline _planningPipeline;
    private readonly ClipboardContentReadExecutionStage _executionStage;

    public ClipboardCaptureReadExecutionPipeline(
        ClipboardCaptureReadPlanningPipeline planningPipeline,
        ClipboardContentReadExecutionStage executionStage)
    {
        _planningPipeline = planningPipeline ?? throw new ArgumentNullException(nameof(planningPipeline));
        _executionStage = executionStage ?? throw new ArgumentNullException(nameof(executionStage));
    }

    public async ValueTask<ClipboardContentReadExecution> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        ClipboardContentReadPlan plan = await _planningPipeline
            .ProcessNextAsync(cancellationToken)
            .ConfigureAwait(false);

        return await _executionStage
            .ExecuteAsync(plan, cancellationToken)
            .ConfigureAwait(false);
    }
}
