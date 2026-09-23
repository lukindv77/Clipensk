using System.Text.Json;
using Clipensk.Core.Settings;

namespace Clipensk.Infrastructure.Settings;

public sealed class JsonApplicationSettingsStore : IApplicationSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _settingsPath;

    public JsonApplicationSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new ApplicationSettings();
        }

        await using FileStream stream = new(
            _settingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);

        ApplicationSettings? settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(
            stream,
            SerializerOptions,
            cancellationToken);

        return Validate(settings ?? new ApplicationSettings());
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        string? directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SerializerOptions,
                    cancellationToken);

                await stream.FlushAsync(cancellationToken);

                // The replace below is the commit point of a data root relocation, after which the
                // old data root is deleted: the new settings must be on disk before they replace the
                // old ones, or a power loss could leave an empty settings file naming no storage.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static ApplicationSettings Validate(ApplicationSettings settings)
    {
        if (settings.PendingDataRootRelocation is { } relocation)
        {
            relocation.Validate();
            if (string.IsNullOrWhiteSpace(settings.DataRootPath))
            {
                throw new InvalidDataException("A pending data root relocation needs a configured data root.");
            }
        }

        settings.ArchiveRotation?.Validate();
        if (settings.DefaultJournalPeriodDays is int days)
        {
            DefaultJournalPeriod.Validate(days);
        }
        if (settings.AutoLockAfterMinutes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "Auto-lock duration must be a positive number of minutes.");
        }
        if (settings.ActiveLocalizationFileName is string fileName &&
            (string.IsNullOrWhiteSpace(fileName) ||
             fileName.Contains('/') ||
             fileName.Contains('\\') ||
             fileName is "." or ".."))
        {
            // Path.GetFileName/DirectorySeparatorChar are platform-dependent (backslash is not a
            // separator on the Linux host this test suite runs on), so both slash variants are
            // checked explicitly rather than relying on them — this file name is later combined
            // with the Languages directory path, and a missed separator would let it escape that
            // directory on the Windows target this app actually ships for.
            throw new ArgumentException(
                "ActiveLocalizationFileName must be a bare file name without directory separators.",
                nameof(settings));
        }
        return settings;
    }
}
