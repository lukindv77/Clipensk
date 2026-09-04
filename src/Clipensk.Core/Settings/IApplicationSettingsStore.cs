namespace Clipensk.Core.Settings;

public interface IApplicationSettingsStore
{
    Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default);
}
