using Clipensk.Infrastructure.Settings;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class SettingsPathProviderTests
{
    [Fact]
    public void AllPaths_StayInsideTheCurrentUsersOwnApplicationDirectory()
    {
        string userRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipensk");

        // The product decision is that every Windows user gets their own settings and storage,
        // placed by default only where that user already has private access — so none of these may
        // resolve to a shared location.
        Assert.Equal(Path.Combine(userRoot, "settings.json"), SettingsPathProvider.GetDefaultSettingsPath());
        Assert.Equal(Path.Combine(userRoot, "Data"), SettingsPathProvider.GetDefaultDataRootPath());
        Assert.Equal(Path.Combine(userRoot, "instance.lock"), SettingsPathProvider.GetInstanceLockPath());
    }

    [Fact]
    public void DefaultDataRoot_IsNotTheSettingsFileOrTheInstanceLock()
    {
        string dataRoot = SettingsPathProvider.GetDefaultDataRootPath();

        Assert.NotEqual(SettingsPathProvider.GetDefaultSettingsPath(), dataRoot);
        Assert.NotEqual(SettingsPathProvider.GetInstanceLockPath(), dataRoot);
    }

    [Fact]
    public void InstanceLock_SitsBesideSettingsRatherThanInsideTheDataRoot()
    {
        // The guard must work before any data root exists, and must not move when the user later
        // relocates their storage.
        Assert.Equal(
            Path.GetDirectoryName(SettingsPathProvider.GetDefaultSettingsPath()),
            Path.GetDirectoryName(SettingsPathProvider.GetInstanceLockPath()));
    }
}
