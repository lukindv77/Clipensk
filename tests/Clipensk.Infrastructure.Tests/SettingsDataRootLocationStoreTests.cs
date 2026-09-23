using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Settings;
using Clipensk.Infrastructure.Storage;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class SettingsDataRootLocationStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _settingsPath;
    private readonly string _source;
    private readonly string _target;

    public SettingsDataRootLocationStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "clipensk-location-" + Guid.NewGuid().ToString("N"));
        _settingsPath = Path.Combine(_root, "Program", "settings.json");
        _source = Path.Combine(_root, "Source");
        _target = Path.Combine(_root, "Target");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Write_ChangesPathAndMarkerTogetherAndKeepsEveryOtherSetting()
    {
        var settings = new JsonApplicationSettingsStore(_settingsPath);
        await settings.SaveAsync(new ApplicationSettings
        {
            DataRootPath = _source,
            PasswordHint = "hint",
            AutoLockAfterMinutes = 5,
            ActiveLocalizationFileName = "en.json",
        });
        var store = new SettingsDataRootLocationStore(settings);
        DataRootRelocationMarker marker = Marker(DataRootRelocationPhase.Switched);

        await store.WriteAsync(new DataRootLocationState(_target, marker));

        DataRootLocationState read = await store.ReadAsync();
        Assert.Equal(_target, read.DataRootPath);
        Assert.Equal(marker, read.PendingRelocation);
        ApplicationSettings reloaded = await settings.LoadAsync();
        Assert.Equal("hint", reloaded.PasswordHint);
        Assert.Equal(5, reloaded.AutoLockAfterMinutes);
        Assert.Equal("en.json", reloaded.ActiveLocalizationFileName);

        await store.WriteAsync(new DataRootLocationState(_target, null));

        Assert.Null((await settings.LoadAsync()).PendingDataRootRelocation);
    }

    [Fact]
    public async Task InvalidMarkerInTheSettingsFile_FailsClosedOnLoad()
    {
        var settings = new JsonApplicationSettingsStore(_settingsPath);
        await settings.SaveAsync(new ApplicationSettings
        {
            DataRootPath = _source,
            PendingDataRootRelocation = Marker(DataRootRelocationPhase.Copying),
        });
        string json = await File.ReadAllTextAsync(_settingsPath);
        await File.WriteAllTextAsync(
            _settingsPath,
            json.Replace(
                System.Text.Json.JsonSerializer.Serialize(_target),
                System.Text.Json.JsonSerializer.Serialize(Path.Combine(_source, "Inner")),
                StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(() => settings.LoadAsync());
    }

    [Fact]
    public async Task MarkerWithoutADataRoot_IsRejected()
    {
        var settings = new JsonApplicationSettingsStore(_settingsPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => settings.SaveAsync(new ApplicationSettings
        {
            PendingDataRootRelocation = Marker(DataRootRelocationPhase.Copying),
        }));
        Assert.False(File.Exists(_settingsPath));
    }

    [Fact]
    public async Task Relocation_ThroughTheSettingsFile_MovesTheStorageAndRecordsTheNewPath()
    {
        Directory.CreateDirectory(Path.Combine(_source, "Current"));
        await File.WriteAllTextAsync(Path.Combine(_source, "storage-crypto.json"), "{}");
        await File.WriteAllBytesAsync(Path.Combine(_source, "Current", "current.db"), [1, 2, 3, 4]);
        var settings = new JsonApplicationSettingsStore(_settingsPath);
        await settings.SaveAsync(new ApplicationSettings { DataRootPath = _source, PasswordHint = "hint" });
        var service = new DataRootRelocationService(new SettingsDataRootLocationStore(settings), _ => long.MaxValue);

        DataRootRelocationResult result = await service.RelocateAsync(_target);

        ApplicationSettings reloaded = await settings.LoadAsync();
        Assert.Equal(_target, reloaded.DataRootPath);
        Assert.Null(reloaded.PendingDataRootRelocation);
        Assert.Equal("hint", reloaded.PasswordHint);
        Assert.Equal(_target, result.DataRootPath);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Path.Combine(_target, "Current", "current.db")));
        Assert.False(Directory.Exists(_source));
    }

    private DataRootRelocationMarker Marker(DataRootRelocationPhase phase) =>
        new(
            Guid.NewGuid(),
            _source,
            _target,
            TargetExisted: false,
            phase,
            new DateTimeOffset(2026, 9, 23, 7, 0, 0, TimeSpan.Zero));
}
