using Clipensk.Infrastructure.Localization;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class LocalizationTemplateWriterTests
{
    [Fact]
    public void BuildTemplate_KnownKey_HasRussianCommentAboveItsEntry()
    {
        var builtIn = new Dictionary<string, string> { ["Journal.Title"] = "Журнал" };

        string template = LocalizationTemplateWriter.BuildTemplate(builtIn);

        Assert.Contains("// Журнал", template);
        Assert.Contains("\"Journal.Title\": \"Журнал\"", template);
    }

    [Fact]
    public void BuildTemplate_MultilineValue_CommentStaysOnOneLine()
    {
        var builtIn = new Dictionary<string, string> { ["A"] = "line one\nline two" };

        string template = LocalizationTemplateWriter.BuildTemplate(builtIn);

        Assert.Contains("// line one line two", template);
        // The JSON value itself must still carry the real newline, escaped, unlike the comment.
        Assert.Contains("\"A\": \"line one\\nline two\"", template);
    }

    [Fact]
    public async Task WriteAsync_ThenLoadAsync_RoundTripsEveryBuiltInKeyExactly()
    {
        IReadOnlyDictionary<string, string> builtIn = BuiltInRussianLocalizationService.AllStrings;
        string directory = Path.Combine(Path.GetTempPath(), "clipensk-localization-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "template.json");
        try
        {
            await LocalizationTemplateWriter.WriteAsync(path, builtIn);

            IReadOnlyDictionary<string, string> loaded = await new JsonExternalLocalizationLoader().LoadAsync(path);

            Assert.Equal(builtIn.Count, loaded.Count);
            foreach ((string key, string value) in builtIn)
            {
                Assert.True(loaded.TryGetValue(key, out string? loadedValue));
                Assert.Equal(value, loadedValue);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
