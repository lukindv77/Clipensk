namespace Clipensk.Core.Clipboard;

public sealed class ClipboardFormatDiscoveryStage
{
    private readonly IClipboardFormatSnapshotReader _reader;

    public ClipboardFormatDiscoveryStage(IClipboardFormatSnapshotReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public ClipboardFormatSnapshot Discover(ClipboardCapturePolicyContext policyContext)
    {
        if (policyContext.Policy.Capture != ClipboardCapturePolicyRule.Allow)
        {
            return new ClipboardFormatSnapshot(policyContext, contentSnapshot: null);
        }

        IClipboardContentSnapshot contentSnapshot = _reader.ReadSnapshot();
        return new ClipboardFormatSnapshot(policyContext, contentSnapshot);
    }
}
