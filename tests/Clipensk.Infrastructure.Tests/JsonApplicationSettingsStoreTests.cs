using Clipensk.Core.Settings;
using Clipensk.Infrastructure.Settings;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class JsonApplicationSettingsStoreTests
{
    [Fact]
    public async Task LoadAsync_LegacySettingsWithoutArchiveRotation_PreservesDisabledDefault()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 1,
                  "DataRootPath": "C:\\Data",
                  "TrashRetentionDays": 30,
                  "PasswordHint": ""
                }
                """);
            var store = new JsonApplicationSettingsStore(path);

            ApplicationSettings loaded = await store.LoadAsync();

            Assert.Equal(1, loaded.SchemaVersion);
            Assert.Equal("C:\\Data", loaded.DataRootPath);
            Assert.Null(loaded.ArchiveRotation);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAndLoadAsync_ConfiguredArchiveRotation_RoundTrips()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var expectedRotation = new ArchiveRotationSettings
            {
                MaxRecordCount = 100_000,
                MaxBytes = 512L * 1024 * 1024,
                MaxCalendarDays = 30,
                ThresholdMode = ArchiveRotationThresholdMode.Any,
            };
            var expected = new ApplicationSettings
            {
                DataRootPath = "C:\\Data",
                ArchiveRotation = expectedRotation,
            };
            var store = new JsonApplicationSettingsStore(path);

            await store.SaveAsync(expected);
            ApplicationSettings loaded = await store.LoadAsync();

            Assert.Equal(expectedRotation, loaded.ArchiveRotation);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAndLoadAsync_SingleArchiveRotationThreshold_IsAccepted()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var expectedRotation = new ArchiveRotationSettings
            {
                MaxCalendarDays = 14,
            };
            var store = new JsonApplicationSettingsStore(path);

            await store.SaveAsync(new ApplicationSettings { ArchiveRotation = expectedRotation });
            ApplicationSettings loaded = await store.LoadAsync();

            Assert.Equal(expectedRotation, loaded.ArchiveRotation);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_MultipleArchiveRotationThresholdsWithoutMode_FailsClosed()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 1,
                  "ArchiveRotation": {
                    "MaxRecordCount": 1000,
                    "MaxCalendarDays": 30
                  }
                }
                """);
            var store = new JsonApplicationSettingsStore(path);

            await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_NonPositiveArchiveRotationThreshold_FailsClosed()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 1,
                  "ArchiveRotation": {
                    "MaxBytes": 0
                  }
                }
                """);
            var store = new JsonApplicationSettingsStore(path);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.LoadAsync());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAsync_EmptyArchiveRotation_FailsBeforeCreatingSettingsFile()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonApplicationSettingsStore(path);
            var settings = new ApplicationSettings
            {
                ArchiveRotation = new ArchiveRotationSettings(),
            };

            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings));

            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAndLoadAsync_ConfiguredDefaultJournalPeriod_RoundTrips()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var expected = new ApplicationSettings { DefaultJournalPeriodDays = 30 };
            var store = new JsonApplicationSettingsStore(path);

            await store.SaveAsync(expected);
            ApplicationSettings loaded = await store.LoadAsync();

            Assert.Equal(30, loaded.DefaultJournalPeriodDays);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_LegacySettingsWithoutDefaultJournalPeriod_LeavesItUnset()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 1,
                  "PasswordHint": ""
                }
                """);
            var store = new JsonApplicationSettingsStore(path);

            ApplicationSettings loaded = await store.LoadAsync();

            Assert.Null(loaded.DefaultJournalPeriodDays);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAsync_NonPositiveDefaultJournalPeriod_FailsBeforeCreatingSettingsFile()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonApplicationSettingsStore(path);
            var settings = new ApplicationSettings { DefaultJournalPeriodDays = 0 };

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(settings));

            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAndLoadAsync_ConfiguredAutoLockDuration_RoundTrips()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var expected = new ApplicationSettings
            {
                AutoLockEnabled = true,
                AutoLockAfterMinutes = 15,
            };
            var store = new JsonApplicationSettingsStore(path);

            await store.SaveAsync(expected);
            ApplicationSettings loaded = await store.LoadAsync();

            Assert.True(loaded.AutoLockEnabled);
            Assert.Equal(15, loaded.AutoLockAfterMinutes);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_LegacySettingsWithoutAutoLockDuration_LeavesItUnset()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 1,
                  "AutoLockEnabled": true,
                  "PasswordHint": ""
                }
                """);
            var store = new JsonApplicationSettingsStore(path);

            ApplicationSettings loaded = await store.LoadAsync();

            Assert.True(loaded.AutoLockEnabled);
            Assert.Null(loaded.AutoLockAfterMinutes);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAsync_NonPositiveAutoLockDuration_FailsBeforeCreatingSettingsFile()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonApplicationSettingsStore(path);
            var settings = new ApplicationSettings { AutoLockAfterMinutes = 0 };

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(settings));

            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAndLoadAsync_ActiveLocalizationFileName_RoundTrips()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var expected = new ApplicationSettings { ActiveLocalizationFileName = "en.json" };
            var store = new JsonApplicationSettingsStore(path);

            await store.SaveAsync(expected);
            ApplicationSettings loaded = await store.LoadAsync();

            Assert.Equal("en.json", loaded.ActiveLocalizationFileName);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_LegacySettingsWithoutActiveLocalizationFileName_LeavesItUnset()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 1,
                  "PasswordHint": ""
                }
                """);
            var store = new JsonApplicationSettingsStore(path);

            ApplicationSettings loaded = await store.LoadAsync();

            Assert.Null(loaded.ActiveLocalizationFileName);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("..\\en.json")]
    [InlineData("../en.json")]
    [InlineData("sub\\en.json")]
    [InlineData("sub/en.json")]
    [InlineData("C:\\en.json")]
    [InlineData("   ")]
    public async Task SaveAsync_LocalizationFileNameWithDirectoryComponents_FailsBeforeCreatingSettingsFile(
        string fileName)
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonApplicationSettingsStore(path);
            var settings = new ApplicationSettings { ActiveLocalizationFileName = fileName };

            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(settings));

            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAndLoadAsync_AutostartEnabled_RoundTrips()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var expected = new ApplicationSettings { AutostartEnabled = true };
            var store = new JsonApplicationSettingsStore(path);

            await store.SaveAsync(expected);
            ApplicationSettings loaded = await store.LoadAsync();

            Assert.True(loaded.AutostartEnabled);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_LegacySettingsWithoutAutostartEnabled_DefaultsToFalse()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "SchemaVersion": 1,
                  "PasswordHint": ""
                }
                """);
            var store = new JsonApplicationSettingsStore(path);

            ApplicationSettings loaded = await store.LoadAsync();

            Assert.False(loaded.AutostartEnabled);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "clipensk-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
