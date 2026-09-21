using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Clipensk.Infrastructure.Localization;

/// <summary>
/// Builds a translation template for every key <see cref="BuiltInRussianLocalizationService"/>
/// defines, per the product decision in <c>docs/OPEN_QUESTIONS.md</c> §9: besides the key and a
/// value, each entry carries a Russian-language comment that the application generates — the
/// built-in Russian text itself, since it already says exactly what the string means. The value
/// starts out as that same Russian text too, as a starting point for a translator to overwrite.
///
/// The comment is a JSON5-style <c>//</c> line comment, not a JSON value, so the file stays a plain
/// flat "key → value" object per §9's already-implemented format — a translator's editor, and
/// <see cref="JsonExternalLocalizationLoader"/>, both see ordinary JSON once the comment lines are
/// skipped.
/// </summary>
public static class LocalizationTemplateWriter
{
    // The default encoder escapes non-ASCII characters as \uXXXX, which would turn every Russian
    // comment and value into unreadable noise for the translator this file is written for.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string BuildTemplate(IReadOnlyDictionary<string, string> builtIn)
    {
        ArgumentNullException.ThrowIfNull(builtIn);

        string[] keys = builtIn.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

        var text = new StringBuilder();
        text.Append('{').Append('\n');
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            string value = builtIn[key];

            text.Append("  // ").Append(AsSingleLine(value)).Append('\n');
            text.Append("  ")
                .Append(JsonSerializer.Serialize(key, SerializerOptions))
                .Append(": ")
                .Append(JsonSerializer.Serialize(value, SerializerOptions));
            text.Append(i < keys.Length - 1 ? "," : string.Empty).Append('\n');
        }

        text.Append('}').Append('\n');
        return text.ToString();
    }

    public static async Task WriteAsync(
        string filePath,
        IReadOnlyDictionary<string, string> builtIn,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string template = BuildTemplate(builtIn);
        await File.WriteAllTextAsync(filePath, template, cancellationToken);
    }

    private static string AsSingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
