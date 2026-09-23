using Clipensk.Core.Settings;

namespace Clipensk.Infrastructure.Settings;

/// <summary>
/// Keeps the data root path and the relocation marker in the application settings, per
/// <c>docs/DATA_ROOT_RELOCATION_PROTOCOL.md</c> §5: both change in one atomic settings write, and
/// every other setting is preserved as stored.
/// </summary>
public sealed class SettingsDataRootLocationStore : IDataRootLocationStore
{
    private readonly IApplicationSettingsStore _settings;

    public SettingsDataRootLocationStore(IApplicationSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<DataRootLocationState> ReadAsync(CancellationToken cancellationToken = default)
    {
        ApplicationSettings settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new DataRootLocationState(settings.DataRootPath, settings.PendingDataRootRelocation);
    }

    public async Task WriteAsync(DataRootLocationState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplicationSettings settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _settings.SaveAsync(
                settings with
                {
                    DataRootPath = state.DataRootPath,
                    PendingDataRootRelocation = state.PendingRelocation,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
