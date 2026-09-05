using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed class ProtectedClipboardCaptureServices
{
    private ProtectedClipboardCaptureServices(
        IApplicationIdentityRegistry applicationIdentityRegistry,
        SqliteClipboardCapturePolicyRepository policyRepository,
        IClipboardCapturePolicyProvider policyProvider)
    {
        ApplicationIdentityRegistry = applicationIdentityRegistry;
        PolicyRepository = policyRepository;
        PolicyProvider = policyProvider;
    }

    public IApplicationIdentityRegistry ApplicationIdentityRegistry { get; }

    public SqliteClipboardCapturePolicyRepository PolicyRepository { get; }

    public IClipboardCapturePolicyProvider PolicyProvider { get; }

    public static ProtectedClipboardCaptureServices Create(
        ProtectedStorageSessionLease session,
        ClipboardCapturePolicy globalPolicy,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(globalPolicy);
        if (!session.IsActive)
        {
            throw new InvalidOperationException(
                "Protected clipboard services require an active protected storage session.");
        }

        var identityRepository = new SqliteApplicationIdentityRepository(
            session,
            connectionFactory);
        var identityRegistry = new RepositoryApplicationIdentityRegistry(identityRepository);
        var policyRepository = new SqliteClipboardCapturePolicyRepository(
            session,
            globalPolicy,
            connectionFactory);
        var policyProvider = new RepositoryClipboardCapturePolicyProvider(policyRepository);

        return new ProtectedClipboardCaptureServices(
            identityRegistry,
            policyRepository,
            policyProvider);
    }
}
