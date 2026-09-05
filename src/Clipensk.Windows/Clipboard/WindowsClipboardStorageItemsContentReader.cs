using Clipensk.Core.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Clipensk.Windows.Clipboard;

internal sealed class WindowsClipboardStorageItemsContentReader : IClipboardStorageItemsContentReader
{
    public bool SupportsFormat(string formatName)
    {
        return string.Equals(formatName, StandardDataFormats.StorageItems, StringComparison.Ordinal);
    }

    public async ValueTask<IReadOnlyList<ClipboardStorageItemMetadata>> ReadAsync(
        IClipboardContentSnapshot contentSnapshot,
        string formatName)
    {
        ArgumentNullException.ThrowIfNull(contentSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);

        if (!SupportsFormat(formatName))
        {
            throw new NotSupportedException(
                $"Clipboard format '{formatName}' is not the supported storage-items format.");
        }

        if (contentSnapshot is not WindowsClipboardContentSnapshot windowsSnapshot)
        {
            throw new ArgumentException(
                "Clipboard content snapshot was not created by the Windows clipboard reader.",
                nameof(contentSnapshot));
        }

        if (!windowsSnapshot.AvailableFormats.Contains(formatName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Clipboard content snapshot does not contain format '{formatName}'.");
        }

        IReadOnlyList<IStorageItem> storageItems = await windowsSnapshot.Content.GetStorageItemsAsync();
        ClipboardPreferredFileOperation preferredOperation = MapPreferredOperation(
            windowsSnapshot.Content.RequestedOperation);
        var result = new ClipboardStorageItemMetadata[storageItems.Count];

        for (int index = 0; index < storageItems.Count; index++)
        {
            IStorageItem item = storageItems[index];
            bool isDirectory = item.IsOfType(StorageItemTypes.Folder);
            string extension = isDirectory ? string.Empty : Path.GetExtension(item.Name);

            result[index] = new ClipboardStorageItemMetadata(
                item.Path,
                item.Name,
                extension,
                isDirectory,
                index,
                preferredOperation);
        }

        return Array.AsReadOnly(result);
    }

    private static ClipboardPreferredFileOperation MapPreferredOperation(
        DataPackageOperation requestedOperation)
    {
        return requestedOperation switch
        {
            DataPackageOperation.Copy => ClipboardPreferredFileOperation.Copy,
            DataPackageOperation.Move => ClipboardPreferredFileOperation.Move,
            DataPackageOperation.Link => ClipboardPreferredFileOperation.Link,
            _ => ClipboardPreferredFileOperation.Unknown,
        };
    }
}
