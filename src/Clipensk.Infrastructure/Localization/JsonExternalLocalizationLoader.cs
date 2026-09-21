using System.Text.Json;

namespace Clipensk.Infrastructure.Localization;

/// <summary>
/// Reads an external translation file — a flat JSON object mapping the same string keys
/// <c>BuiltInRussianLocalizationService</c> uses (e.g. <c>"Journal.Title"</c>) to their translated
/// text — per <c>docs/REQUIREMENTS.md</c> §20. Unknown keys are harmless: nothing ever looks them
/// up. Missing keys are the caller's problem to fall back on, not this loader's: it only reads
/// whatever the file actually contains.
///
/// Tolerates <c>//</c> line comments and a trailing comma, per the product decision in
/// <c>docs/OPEN_QUESTIONS.md</c> §9: <see cref="LocalizationTemplateWriter"/> generates exactly this
/// shape when it exports a translation template, and this loader must read that template back — both
/// untouched and after a translator fills in values — the same way it reads a plain hand-written file.
/// </summary>
public sealed class JsonExternalLocalizationLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using FileStream stream = File.OpenRead(filePath);
        Dictionary<string, string>? values = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
            stream,
            Options,
            cancellationToken);

        return values ?? throw new InvalidDataException(
            $"Файл перевода '{filePath}' не содержит допустимого JSON-объекта строк.");
    }
}
