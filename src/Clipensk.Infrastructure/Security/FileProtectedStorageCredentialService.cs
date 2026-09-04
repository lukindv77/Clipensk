using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clipensk.Core.Security;
using Konscious.Security.Cryptography;

namespace Clipensk.Infrastructure.Security;

public sealed class FileProtectedStorageCredentialService : IProtectedStorageCredentialService
{
    public const string MetadataFileName = "storage-crypto.json";

    private const int MetadataSchemaVersion = 1;
    private const string AlgorithmName = "argon2id";
    private static readonly byte[] VerifierDomain = Encoding.UTF8.GetBytes("Clipensk.MasterKeyVerifier.v1\0");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly KeyDerivationProfile _initializationProfile;

    public FileProtectedStorageCredentialService(KeyDerivationProfile? initializationProfile = null)
    {
        _initializationProfile = initializationProfile ?? KeyDerivationProfile.ProductionV1;
        _initializationProfile.Validate();
    }

    public async Task<ProtectedStorageCredentialState> GetStateAsync(
        string dataRootPath,
        CancellationToken cancellationToken = default)
    {
        string metadataPath = GetMetadataPath(dataRootPath);
        if (!File.Exists(metadataPath))
        {
            return ProtectedStorageCredentialState.Uninitialized;
        }

        CryptoMetadataDocument? document;
        try
        {
            document = await LoadDocumentAsync(metadataPath, cancellationToken);
        }
        catch (JsonException)
        {
            return ProtectedStorageCredentialState.Invalid;
        }

        return TryDecodeDocument(document, out _)
            ? ProtectedStorageCredentialState.Ready
            : ProtectedStorageCredentialState.Invalid;
    }

    public async Task<ProtectedStorageUnlockResult> UnlockOrInitializeAsync(
        string dataRootPath,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        ArgumentException.ThrowIfNullOrEmpty(password);

        string normalizedRoot = Path.GetFullPath(dataRootPath);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException(normalizedRoot);
        }

        string metadataPath = Path.Combine(normalizedRoot, MetadataFileName);
        CryptoMetadataDocument? document;
        try
        {
            document = await LoadDocumentAsync(metadataPath, cancellationToken);
        }
        catch (JsonException)
        {
            return new ProtectedStorageUnlockResult(
                ProtectedStorageUnlockStatus.InvalidMetadata,
                WasInitialized: false,
                MasterKey: null);
        }

        if (document is null)
        {
            return await InitializeAsync(normalizedRoot, password, cancellationToken);
        }

        if (!TryDecodeDocument(document, out DecodedCryptoMetadata decoded))
        {
            return new ProtectedStorageUnlockResult(
                ProtectedStorageUnlockStatus.InvalidMetadata,
                WasInitialized: false,
                MasterKey: null);
        }

        byte[]? masterKey = await DeriveMasterKeyAsync(password, decoded.Salt, decoded.Profile, cancellationToken);
        try
        {
            byte[] verifier = ComputeVerifier(masterKey, decoded.StorageId);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(verifier, decoded.Verifier))
                {
                    return new ProtectedStorageUnlockResult(
                        ProtectedStorageUnlockStatus.InvalidPassword,
                        WasInitialized: false,
                        MasterKey: null);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(verifier);
            }

            var lease = new MasterKeyLease(masterKey);
            masterKey = null;
            return new ProtectedStorageUnlockResult(
                ProtectedStorageUnlockStatus.Success,
                WasInitialized: false,
                MasterKey: lease);
        }
        finally
        {
            if (masterKey is not null)
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
    }

    private async Task<ProtectedStorageUnlockResult> InitializeAsync(
        string normalizedRoot,
        string password,
        CancellationToken cancellationToken)
    {
        KeyDerivationProfile profile = _initializationProfile;
        Guid storageId = Guid.NewGuid();
        byte[] salt = RandomNumberGenerator.GetBytes(profile.SaltLengthBytes);
        byte[]? masterKey = await DeriveMasterKeyAsync(password, salt, profile, cancellationToken);

        try
        {
            byte[] verifier = ComputeVerifier(masterKey, storageId);
            try
            {
                var document = new CryptoMetadataDocument
                {
                    SchemaVersion = MetadataSchemaVersion,
                    StorageId = storageId.ToString("D"),
                    Algorithm = AlgorithmName,
                    ProfileVersion = profile.ProfileVersion,
                    Argon2Version = profile.Argon2Version,
                    MemoryKiB = profile.MemoryKiB,
                    Iterations = profile.Iterations,
                    Parallelism = profile.Parallelism,
                    SaltLengthBytes = profile.SaltLengthBytes,
                    MasterKeyLengthBytes = profile.MasterKeyLengthBytes,
                    SaltBase64 = Convert.ToBase64String(salt),
                    VerifierBase64 = Convert.ToBase64String(verifier),
                };

                await SaveNewDocumentAsync(
                    Path.Combine(normalizedRoot, MetadataFileName),
                    document,
                    cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(verifier);
            }

            var lease = new MasterKeyLease(masterKey);
            masterKey = null;
            return new ProtectedStorageUnlockResult(
                ProtectedStorageUnlockStatus.Success,
                WasInitialized: true,
                MasterKey: lease);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            if (masterKey is not null)
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
    }

    private static Task<byte[]> DeriveMasterKeyAsync(
        string password,
        byte[] salt,
        KeyDerivationProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            try
            {
                using var argon2 = new Argon2id(passwordBytes)
                {
                    Salt = salt,
                    MemorySize = profile.MemoryKiB,
                    Iterations = profile.Iterations,
                    DegreeOfParallelism = profile.Parallelism,
                };

                return argon2.GetBytes(profile.MasterKeyLengthBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }
        }, cancellationToken);
    }

    private static byte[] ComputeVerifier(ReadOnlySpan<byte> masterKey, Guid storageId)
    {
        Span<byte> storageIdBytes = stackalloc byte[16];
        if (!storageId.TryWriteBytes(storageIdBytes))
        {
            throw new InvalidOperationException("Не удалось сериализовать StorageId.");
        }

        byte[] message = new byte[VerifierDomain.Length + storageIdBytes.Length];
        VerifierDomain.CopyTo(message, 0);
        storageIdBytes.CopyTo(message.AsSpan(VerifierDomain.Length));
        return HMACSHA256.HashData(masterKey, message);
    }

    private static bool TryDecodeDocument(
        CryptoMetadataDocument? document,
        out DecodedCryptoMetadata decoded)
    {
        decoded = default;

        if (document is null ||
            document.SchemaVersion != MetadataSchemaVersion ||
            !string.Equals(document.Algorithm, AlgorithmName, StringComparison.Ordinal) ||
            !Guid.TryParse(document.StorageId, out Guid storageId))
        {
            return false;
        }

        try
        {
            var profile = new KeyDerivationProfile(
                document.ProfileVersion,
                document.Argon2Version,
                document.MemoryKiB,
                document.Iterations,
                document.Parallelism,
                document.SaltLengthBytes,
                document.MasterKeyLengthBytes);
            profile.Validate();

            byte[] salt = Convert.FromBase64String(document.SaltBase64 ?? string.Empty);
            byte[] verifier = Convert.FromBase64String(document.VerifierBase64 ?? string.Empty);

            if (salt.Length != profile.SaltLengthBytes || verifier.Length != 32)
            {
                return false;
            }

            decoded = new DecodedCryptoMetadata(storageId, profile, salt, verifier);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<CryptoMetadataDocument?> LoadDocumentAsync(
        string metadataPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            metadataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);

        return await JsonSerializer.DeserializeAsync<CryptoMetadataDocument>(
            stream,
            SerializerOptions,
            cancellationToken);
    }

    private static async Task SaveNewDocumentAsync(
        string metadataPath,
        CryptoMetadataDocument document,
        CancellationToken cancellationToken)
    {
        string temporaryPath = metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, metadataPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetMetadataPath(string dataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        return Path.Combine(Path.GetFullPath(dataRootPath), MetadataFileName);
    }

    private sealed record CryptoMetadataDocument
    {
        public int SchemaVersion { get; init; }
        public string? StorageId { get; init; }
        public string? Algorithm { get; init; }
        public int ProfileVersion { get; init; }
        public int Argon2Version { get; init; }
        public int MemoryKiB { get; init; }
        public int Iterations { get; init; }
        public int Parallelism { get; init; }
        public int SaltLengthBytes { get; init; }
        public int MasterKeyLengthBytes { get; init; }
        public string? SaltBase64 { get; init; }
        public string? VerifierBase64 { get; init; }
    }

    private readonly record struct DecodedCryptoMetadata(
        Guid StorageId,
        KeyDerivationProfile Profile,
        byte[] Salt,
        byte[] Verifier);
}
