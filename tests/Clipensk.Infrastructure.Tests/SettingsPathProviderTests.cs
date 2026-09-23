using Clipensk.Infrastructure.Settings;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class SettingsPathProviderTests
{
    [Fact]
    public void Settings_LiveBesideTheProgram()
    {
        // Each Windows user runs their own unpacked copy of Clipensk, so the settings in its folder
        // are that user's (docs/OPEN_QUESTIONS.md §12, 2026-09-23).
        string programDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

        Assert.Equal(programDirectory, SettingsPathProvider.GetProgramDirectory());
        Assert.Equal(Path.Combine(programDirectory, "settings.json"), SettingsPathProvider.GetDefaultSettingsPath());
    }

    [Fact]
    public void DefaultDataRootAndInstanceLock_StayInTheCurrentUsersOwnApplicationDirectory()
    {
        string userRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipensk");

        // The default storage is placed only where the user already has private access, and the
        // single-instance lock is per Windows user whichever copy of the program runs.
        Assert.Equal(Path.Combine(userRoot, "Data"), SettingsPathProvider.GetDefaultDataRootPath());
        Assert.Equal(Path.Combine(userRoot, "instance.lock"), SettingsPathProvider.GetInstanceLockPath());
    }

    [Fact]
    public void InstanceLock_IsNeitherBesideTheSettingsNorInsideTheDataRoot()
    {
        string lockDirectory = Path.GetDirectoryName(SettingsPathProvider.GetInstanceLockPath())!;

        Assert.NotEqual(SettingsPathProvider.GetProgramDirectory(), lockDirectory);
        Assert.NotEqual(SettingsPathProvider.GetDefaultDataRootPath(), lockDirectory);
    }

    [Fact]
    public void CanCreateFilesIn_ProbesWithoutLeavingAnything()
    {
        string directory = Path.Combine(Path.GetTempPath(), "clipensk-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(SettingsPathProvider.CanCreateFilesIn(directory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));

            Assert.False(SettingsPathProvider.CanCreateFilesIn(Path.Combine(directory, "missing")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
