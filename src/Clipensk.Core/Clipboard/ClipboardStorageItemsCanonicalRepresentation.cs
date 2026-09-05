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
