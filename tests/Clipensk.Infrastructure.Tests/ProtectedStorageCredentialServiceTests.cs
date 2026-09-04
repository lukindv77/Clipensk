using Clipensk.Core.Security;
using Clipensk.Infrastructure.Security;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class ProtectedStorageCredentialServiceTests
{
    private static readonly KeyDerivationProfile TestProfile = new(
        ProfileVersion: 1,
        Argon2Version: 0x13,
        MemoryKiB: 19_456,
        Iterations: 2,
        Parallelism: 1,
        SaltLengthBytes: 16,
        MasterKeyLengthBytes: 32);

    [Fact]
    public async Task FirstPassword_InitializesMetadata_AndSubsequentUnlockValidatesPassword()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var service = new FileProtectedStorageCredentialService(TestProfile);
            Assert.Equal(
                ProtectedStorageCredentialState.Uninitialized,
                await service.GetStateAsync(root));

            ProtectedStorageUnlockResult first =
                await service.UnlockOrInitializeAsync(root, "пароль-Clipensk-1");
            Assert.True(first.IsSuccess);
            Assert.True(first.WasInitialized);
            using (Assert.IsType<MasterKeyLease>(first.MasterKey))
            {
            }

            Assert.Equal(
                ProtectedStorageCredentialState.Ready,
                await service.GetStateAsync(root));

            ProtectedStorageUnlockResult second =
                await service.UnlockOrInitializeAsync(root, "пароль-Clipensk-1");
            Assert.True(second.IsSuccess);
            Assert.False(second.WasInitialized);
            using (Assert.IsType<MasterKeyLease>(second.MasterKey))
            {
            }

            ProtectedStorageUnlockResult wrong =
                await service.UnlockOrInitializeAsync(root, "другой-пароль");
            Assert.Equal(ProtectedStorageUnlockStatus.InvalidPassword, wrong.Status);
            Assert.Null(wrong.MasterKey);

            string metadata = await File.ReadAllTextAsync(
                Path.Combine(root, FileProtectedStorageCredentialService.MetadataFileName));
            Assert.DoesNotContain("пароль-Clipensk-1", metadata, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptMetadata_IsFailClosed_AndIsNotReinitialized()
    {
        string root = CreateTemporaryDirectory();
        string metadataPath = Path.Combine(root, FileProtectedStorageCredentialService.MetadataFileName);
        try
        {
            await File.WriteAllTextAsync(metadataPath, "{ definitely-not-valid-json");
            var service = new FileProtectedStorageCredentialService(TestProfile);

            Assert.Equal(
                ProtectedStorageCredentialState.Invalid,
                await service.GetStateAsync(root));

            ProtectedStorageUnlockResult result =
                await service.UnlockOrInitializeAsync(root, "new-password");

            Assert.Equal(ProtectedStorageUnlockStatus.InvalidMetadata, result.Status);
            Assert.Null(result.MasterKey);
            Assert.Equal("{ definitely-not-valid-json", await File.ReadAllTextAsync(metadataPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "Clipensk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
