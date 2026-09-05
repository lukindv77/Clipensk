using Clipensk.Storage.ExternalFiles;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ExternalPayloadStoreTests
{
    [Fact]
    public async Task StoreNormalizedPngAsync_WritesContentAddressedFile()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var store = new ExternalPayloadStore(root);
            byte[] bytes = [1, 2, 3, 4];
            DateOnly date = new(2026, 9, 5);

            ExternalPayloadAddress address = await store.StoreNormalizedPngAsync(date, bytes);

            string path = Path.Combine(root, address.RelativePath);
            Assert.True(File.Exists(path));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(bytes.Length, address.SizeBytes);
            Assert.EndsWith(".png", address.RelativePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task StoreNormalizedPngAsync_ConcurrentDuplicatesProduceOnePhysicalFile()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var store = new ExternalPayloadStore(root);
            byte[] bytes = [5, 6, 7, 8];
            DateOnly date = new(2026, 9, 5);

            Task<ExternalPayloadAddress>[] writes = Enumerable.Range(0, 8)
                .Select(_ => store.StoreNormalizedPngAsync(date, bytes).AsTask())
                .ToArray();

            ExternalPayloadAddress[] addresses = await Task.WhenAll(writes);

            Assert.All(addresses, address => Assert.Equal(addresses[0], address));
            Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task StoreNormalizedPngAsync_CorruptedExistingPayloadFailsClosed()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var store = new ExternalPayloadStore(root);
            byte[] bytes = [1, 2, 3];
            ExternalPayloadAddress address = await store.StoreNormalizedPngAsync(
                new DateOnly(2026, 9, 5),
                bytes);
            string path = Path.Combine(root, address.RelativePath);
            await File.WriteAllBytesAsync(path, [9, 8, 7]);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await store.StoreNormalizedPngAsync(new DateOnly(2026, 9, 5), bytes);
            });
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task StoreCustomBinaryAsync_RejectsAddressEscapingFilesRoot()
    {
        string root = CreateTemporaryRoot();
        try
        {
            var store = new ExternalPayloadStore(root);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await store.StoreCustomBinaryAsync(
                    new DateOnly(2026, 9, 5),
                    new byte[] { 1, 2, 3 },
                    @".\..\..\..\escape.bin");
            });
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task StoreNormalizedPngAsync_PreCanceledRequestDoesNotCreateFilesRoot()
    {
        string root = CreateTemporaryRoot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new ExternalPayloadStore(root);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await store.StoreNormalizedPngAsync(
                new DateOnly(2026, 9, 5),
                new byte[] { 1, 2, 3 },
                cancellation.Token);
        });

        Assert.False(Directory.Exists(root));
    }

    private static string CreateTemporaryRoot()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "Clipensk.Storage.Tests",
            Guid.NewGuid().ToString("N"));
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
