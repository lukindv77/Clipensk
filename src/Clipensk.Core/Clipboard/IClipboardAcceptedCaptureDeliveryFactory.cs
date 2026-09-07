using Clipensk.Core.Applications;

namespace Clipensk.Core.Clipboard;

/// <summary>Builds an inert delivery graph from explicit policy, sink and durable identity dependencies.</summary>
public interface IClipboardAcceptedCaptureDeliveryFactory
{
    /// <summary>
    /// Construction must not consume the queue, read clipboard data, open storage, start a worker,
    /// or acquire resources requiring separate teardown. The caller adds protected-session cancellation.
    /// </summary>
    IClipboardAcceptedCaptureDelivery Create(
        IClipboardCapturePolicyProvider policyProvider,
        IClipboardAcceptedCaptureSink sink,
        IApplicationIdentityRegistry identityRegistry);
}
