namespace Clipensk.Storage.Clipboard;

public interface ICustomBinaryFormatConfigurationRepository
{
    ValueTask<string?> ReadFileExtensionAsync(
        string formatName,
        CancellationToken cancellationToken = default);

    ValueTask InitializeAsync(
        string formatName,
        string fileExtension,
        CancellationToken cancellationToken = default);
}
