using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Clipensk.Core.Clipboard;

public sealed record ClipboardStorageItemsCanonicalRepresentation(
    string Text,
    long ByteCount);

public static class ClipboardStorageItemsCanonicalizer
{
    public const int CurrentVersion = 1;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    public static ClipboardStorageItemsCanonicalRepresentation Create(
        IReadOnlyList<ClipboardStorageItemMetadata> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteStartArray("items");

            for (int index = 0; index < items.Count; index++)
            {
                ClipboardStorageItemMetadata item = items[index];
                ValidateItem(item, index);

                writer.WriteStartObject();
                writer.WriteNumber("order", item.Order);
                writer.WriteString("fullPath", item.FullPath);
                writer.WriteString("name", item.Name);
                writer.WriteString("extension", item.Extension);
                writer.WriteBoolean("isDirectory", item.IsDirectory);
                writer.WriteString("preferredOperation", MapOperation(item.PreferredOperation));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        string text = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return new ClipboardStorageItemsCanonicalRepresentation(text, buffer.WrittenCount);
    }

    /// <summary>
    /// Reads the full paths back out of a canonical representation, in stored item order.
    ///
    /// This reader lives beside the writer on purpose: the canonical schema has exactly one
    /// producer and one consumer, and separating them is how the two drift. It fails closed on an
    /// unknown version, because a future version may carry different semantics for the same field
    /// names, and on any item that does not carry a usable path.
    /// </summary>
    public static IReadOnlyList<string> ReadFullPaths(string canonicalText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalText);

        using JsonDocument document = Parse(canonicalText);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "Canonical storage-items representation must be a JSON object.");
        }

        if (!root.TryGetProperty("version", out JsonElement version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int versionValue))
        {
            throw new InvalidDataException(
                "Canonical storage-items representation carries no readable version.");
        }

        if (versionValue != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Canonical storage-items representation version {versionValue} is not supported.");
        }

        if (!root.TryGetProperty("items", out JsonElement items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Canonical storage-items representation carries no item array.");
        }

        var paths = new List<string>(items.GetArrayLength());
        int expectedOrder = 0;
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Canonical storage item must be a JSON object.");
            }

            if (!item.TryGetProperty("order", out JsonElement order) ||
                order.ValueKind != JsonValueKind.Number ||
                !order.TryGetInt32(out int orderValue) ||
                orderValue != expectedOrder)
            {
                throw new InvalidDataException(
                    "Canonical storage item order must be zero-based and contiguous.");
            }

            if (!item.TryGetProperty("fullPath", out JsonElement fullPath) ||
                fullPath.ValueKind != JsonValueKind.String ||
                fullPath.GetString() is not { Length: > 0 } path ||
                string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("Canonical storage item carries no full path.");
            }

            paths.Add(path);
            expectedOrder++;
        }

        if (paths.Count == 0)
        {
            throw new InvalidDataException(
                "Canonical storage-items representation contains no items.");
        }

        return paths;
    }

    private static JsonDocument Parse(string canonicalText)
    {
        try
        {
            return JsonDocument.Parse(canonicalText);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Canonical storage-items representation is not valid JSON.",
                exception);
        }
    }

    private static void ValidateItem(ClipboardStorageItemMetadata item, int expectedOrder)
    {
        if (item.Order != expectedOrder)
        {
            throw new InvalidDataException(
                $"Storage item order must be zero-based and contiguous. Expected {expectedOrder}, got {item.Order}.");
        }

        if (item.FullPath is null)
        {
            throw new InvalidDataException("Storage item full path cannot be null.");
        }
        if (item.Name is null)
        {
            throw new InvalidDataException("Storage item name cannot be null.");
        }
        if (item.Extension is null)
        {
            throw new InvalidDataException("Storage item extension cannot be null.");
        }
    }

    private static string MapOperation(ClipboardPreferredFileOperation operation)
    {
        return operation switch
        {
            ClipboardPreferredFileOperation.Unknown => "unknown",
            ClipboardPreferredFileOperation.Copy => "copy",
            ClipboardPreferredFileOperation.Move => "move",
            ClipboardPreferredFileOperation.Link => "link",
            _ => throw new InvalidDataException(
                $"Unsupported preferred file operation value '{operation}'."),
        };
    }
}
