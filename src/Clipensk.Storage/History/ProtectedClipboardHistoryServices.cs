using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.History;

public sealed class ProtectedClipboardHistoryServices
{
    private ProtectedClipboardHistoryServices(
        SqliteExternalPayloadAddressIndex addressIndex,
        CatalogClipboardExternalPayloadAddressResolver externalPayloadResolver,
        SqliteClipboardHistorySink historySink,
        ICurrentClipboardHistoryRepository historyRepository)
    {
        AddressIndex = addressIndex;
        ExternalPayloadResolver = externalPayloadResolver;
        HistorySink = historySink;
        HistoryRepository = historyRepository;
    }

    public SqliteExternalPayloadAddressIndex AddressIndex { get; }

    public CatalogClipboardExternalPayloadAddressResolver ExternalPayloadResolver { get; }

    public IClipboardAcceptedCaptureSink HistorySink { get; }

    public ICurrentClipboardHistoryRepository HistoryRepository { get; }

    public static ProtectedClipboardHistoryServices Create(
        ProtectedStorageSessionLease session,
        IClipboardCustomBinaryFileExtensionProvider customBinaryExtensionProvider,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(customBinaryExtensionProvider);
        if (!session.IsActive)
        {
            throw new InvalidOperationException(
                "Protected clipboard history services require an active protected storage session.");
        }

        var addressIndex = new SqliteExternalPayloadAddressIndex(
            session,
            connectionFactory);
        var externalPayloadResolver = new CatalogClipboardExternalPayloadAddressResolver(
            addressIndex,
            new ExternalPayloadStore(Path.Combine(session.DataRootPath, "Files")),
            customBinaryExtensionProvider);
        var historySink = new SqliteClipboardHistorySink(
            session,
            externalPayloadResolver,
            connectionFactory);

        var historyRepository = new SqliteCurrentClipboardHistoryRepository(
            session,
            connectionFactory);

        return new ProtectedClipboardHistoryServices(
            addressIndex,
            externalPayloadResolver,
            historySink,
            historyRepository);
    }
}
