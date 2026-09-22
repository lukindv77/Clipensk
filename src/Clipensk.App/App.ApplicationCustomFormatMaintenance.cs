using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;

namespace Clipensk.App;

public partial class App
{
    internal Task<bool> TryApplyApplicationCapturePolicyAndCustomMappingsChangeAsync(
        ProtectedStorageSessionLease session,
        global::Clipensk.Core.Applications.ApplicationId applicationId,
        global::Clipensk.Core.Clipboard.ClipboardCapturePolicy policy,
        IReadOnlyList<ApplicationCustomBinaryFormatConfiguration> customBinaryConfigurations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(applicationId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(customBinaryConfigurations);

        return TryApplyApplicationCapturePolicyCoreAsync(
            session,
            applicationId,
            policy,
            customBinaryConfigurations,
            cancellationToken);
    }
}
