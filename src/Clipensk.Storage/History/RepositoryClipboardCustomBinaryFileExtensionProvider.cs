using Clipensk.Storage.Clipboard;

namespace Clipensk.Storage.History;

public sealed class RepositoryClipboardCustomBinaryFileExtensionProvider :
    IClipboardCustomBinaryFileExtensionProvider
{
    private readonly ICustomBinaryFormatConfigurationRepository _repository;

    public RepositoryClipboardCustomBinaryFileExtensionProvider(
        ICustomBinaryFormatConfigurationRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async ValueTask<string> GetExtensionAsync(
        string formatName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        string? extension = await _repository.ReadFileExtensionAsync(
            formatName,
            cancellationToken).ConfigureAwait(false);
        if (extension is null)
        {
            throw new InvalidDataException(
                $"Custom binary format '{formatName}' has no configured file extension.");
        }

        return extension;
    }
}
