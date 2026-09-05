using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;

namespace Clipensk.Storage.Clipboard;

internal sealed class EditableSqliteClipboardCapturePolicyRepository
    : IEditableClipboardCapturePolicyRepository
{
    private readonly SqliteClipboardCapturePolicyRepository _inner;

    public EditableSqliteClipboardCapturePolicyRepository(
        SqliteClipboardCapturePolicyRepository inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public ValueTask<ClipboardCapturePolicy> GetGlobalPolicyAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetGlobalPolicyAsync(cancellationToken);

    public ValueTask<ClipboardCapturePolicy?> GetApplicationPolicyAsync(
        ApplicationId applicationId,
        CancellationToken cancellationToken = default) =>
        _inner.GetApplicationPolicyAsync(applicationId, cancellationToken);

    public ValueTask SetApplicationPolicyAsync(
        ApplicationId applicationId,
        ClipboardCapturePolicy policy,
        CancellationToken cancellationToken = default) =>
        _inner.SetApplicationPolicyAsync(applicationId, policy, cancellationToken);

    public ValueTask DeleteApplicationPolicyAsync(
        ApplicationId applicationId,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteApplicationPolicyAsync(applicationId, cancellationToken);
}
