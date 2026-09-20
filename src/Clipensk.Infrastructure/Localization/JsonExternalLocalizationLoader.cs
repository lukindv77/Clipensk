using System.Text.Json;

namespace Clipensk.Infrastructure.Localization;

/// <summary>
/// Reads an external translation file — a flat JSON object mapping the same string keys
/// <c>BuiltInRussianLocalizationService</c> uses (e.g. <c>"Journal.Title"</c>) to their translated
/// text — per <c>docs/REQUIREMENTS.md</c> §20. Unknown keys are harmless: nothing ever looks them
/// up. Missing keys are the caller's problem to fall back on, not this loader's: it only reads
/// whatever the file actually contains.
/// </summary>
public sealed class JsonExternalLocalizationLoader
{
    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using FileStream stream = File.OpenRead(filePath);
        Dictionary<string, string>? values = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
            stream,
            cancellationToken: cancellationToken);

        return values ?? throw new InvalidDataException(
            $"Файл перевода '{filePath}' не содержит допустимого JSON-объекта строк.");
    }
}
