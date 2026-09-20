using System.Text.Json;
using Clipensk.Infrastructure.Localization;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class JsonExternalLocalizationLoaderTests
{
    [Fact]
    public async Task LoadAsync_ValidFile_ReturnsAllEntries()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "en.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "Journal.Title": "Journal",
                  "Journal.Empty": "No entries for the selected period."
                }
                """);
            var loader = new JsonExternalLocalizationLoader();

            IReadOnlyDictionary<string, string> loaded = await loader.LoadAsync(path);

            Assert.Equal(2, loaded.Count);
            Assert.Equal("Journal", loaded["Journal.Title"]);
            Assert.Equal("No entries for the selected period.", loaded["Journal.Empty"]);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_EmptyObject_ReturnsEmptyDictionary()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "en.json");
        try
        {
            await File.WriteAllTextAsync(path, "{}");
            var loader = new JsonExternalLocalizationLoader();

            IReadOnlyDictionary<string, string> loaded = await loader.LoadAsync(path);

            Assert.Empty(loaded);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_MissingFile_Throws()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "missing.json");
        try
        {
            var loader = new JsonExternalLocalizationLoader();

            await Assert.ThrowsAsync<FileNotFoundException>(() => loader.LoadAsync(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_Throws()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "en.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json");
            var loader = new JsonExternalLocalizationLoader();

            await Assert.ThrowsAsync<JsonException>(() => loader.LoadAsync(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_TopLevelArray_Throws()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "en.json");
        try
        {
            await File.WriteAllTextAsync(path, """["Journal.Title", "Journal"]""");
            var loader = new JsonExternalLocalizationLoader();

            await Assert.ThrowsAsync<JsonException>(() => loader.LoadAsync(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAsync_NonStringValue_Throws()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "en.json");
        try
        {
            await File.WriteAllTextAsync(path, """{ "Journal.Title": 42 }""");
            var loader = new JsonExternalLocalizationLoader();

            await Assert.ThrowsAsync<JsonException>(() => loader.LoadAsync(path));
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
            "clipensk-localization-tests-" + Guid.NewGuid().ToString("N"));
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
