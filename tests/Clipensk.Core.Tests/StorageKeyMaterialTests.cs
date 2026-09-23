using System.Security.Cryptography;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class StorageKeyMaterialTests : IDisposable
{
    private readonly string _root;

    public StorageKeyMaterialTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "clipensk-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheStorageKey_IsTheMasterKeyFollowedByTheSalt()
    {
        byte[] masterKey = RandomNumberGenerator.GetBytes(32);
        byte[] salt = StorageSalt.Create(KeyDerivationProfile.ProductionV1);

        byte[] material = StorageKeyMaterial.Compose(masterKey, salt);

        Assert.Equal(48, material.Length);
        Assert.Equal(masterKey, StorageKeyMaterial.GetMasterKey(material).ToArray());
        Assert.Equal(salt, StorageKeyMaterial.GetSalt(material).ToArray());
        Assert.Throws<ArgumentException>(() => StorageKeyMaterial.Compose(masterKey, salt.AsSpan(1)));
        Assert.Throws<ArgumentException>(() => StorageKeyMaterial.GetSalt(masterKey));
    }

    [Fact]
    public void TheSalt_CarriesTheProfileNumberInItsFirstByte_AndIsOtherwiseRandom()
    {
        byte[] first = StorageSalt.Create(KeyDerivationProfile.ProductionV1);
        byte[] second = StorageSalt.Create(KeyDerivationProfile.ProductionV1);

        Assert.Equal(16, first.Length);
        Assert.Equal(1, StorageSalt.GetProfileVersion(first));
        Assert.Equal(1, StorageSalt.GetProfileVersion(second));
        Assert.NotEqual(first.AsSpan(1).ToArray(), second.AsSpan(1).ToArray());
    }

    [Fact]
    public void StorageDatabases_AreCurrentThenCatalogThenArchivesByName()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Current"));
        Directory.CreateDirectory(Path.Combine(_root, "Archive", "nested"));
        foreach (string relativePath in new[]
                 {
                     Path.Combine("Archive", "archive_000002.db"),
                     Path.Combine("Archive", "archive_000001_0001.db"),
                     Path.Combine("Archive", "notes.db"),
                     Path.Combine("Archive", "nested", "archive_000003.db"),
                     Path.Combine("Current", "storage-catalog.db"),
                     Path.Combine("Current", "current.db"),
                     Path.Combine("Current", "source-backup.db"),
                 })
        {
            File.WriteAllBytes(Path.Combine(_root, relativePath), [1]);
        }

        Assert.Equal(
            [
                Path.Combine("Current", "current.db"),
                Path.Combine("Current", "storage-catalog.db"),
                Path.Combine("Archive", "archive_000001_0001.db"),
                Path.Combine("Archive", "archive_000002.db"),
            ],
            StorageDatabaseFiles.EnumerateExisting(_root).Select(path => Path.GetRelativePath(_root, path)));
        Assert.Empty(StorageDatabaseFiles.EnumerateExisting(Path.Combine(_root, "Missing")));
    }

    [Theory]
    [InlineData("Current/current.db", true)]
    [InlineData("Current/storage-catalog.db", true)]
    [InlineData("Archive/archive_000001.db", true)]
    [InlineData("Archive/archive_000001_0002.db", true)]
    [InlineData("Archive/nested/archive_000001.db", false)]
    [InlineData("Current/source-backup.db", false)]
    [InlineData("Files/2026-09-01/archive_000001.db", false)]
    [InlineData("current.db", false)]
    public void IsStorageDatabase_NamesOnlyTheDatabasesThatCarryTheSalt(string relativePath, bool expected)
    {
        string path = relativePath.Replace('/', Path.DirectorySeparatorChar);

        Assert.Equal(expected, StorageDatabaseFiles.IsStorageDatabase(path));
    }

    [Fact]
    public void TryReadSalt_ReadsTheFirstSixteenBytes_AndRefusesShorterFiles()
    {
        string full = Path.Combine(_root, "full.db");
        string shortFile = Path.Combine(_root, "short.db");
        byte[] salt = StorageSalt.Create(KeyDerivationProfile.ProductionV1);
        File.WriteAllBytes(full, [.. salt, 9, 9, 9]);
        File.WriteAllBytes(shortFile, salt.AsSpan(0, 15).ToArray());
        byte[] read = new byte[16];

        Assert.True(StorageDatabaseFiles.TryReadSalt(full, read));
        Assert.Equal(salt, read);
        Assert.False(StorageDatabaseFiles.TryReadSalt(shortFile, read));
        Assert.False(StorageDatabaseFiles.TryReadSalt(Path.Combine(_root, "missing.db"), read));
    }
}
