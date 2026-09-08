namespace Clipensk.Storage.Clipboard;

public sealed class PendingPolicyMaintenanceException : InvalidOperationException
{
    public PendingPolicyMaintenanceException()
        : base("Clipboard capture cannot be composed while policy maintenance is pending.")
    {
    }
}
