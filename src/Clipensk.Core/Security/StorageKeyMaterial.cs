using System.Security.Cryptography;

namespace Clipensk.Core.Security;

/// <summary>
/// The key every protected database of one storage is opened with: the MasterKey followed by the
/// storage salt. SQLCipher gets both (<c>x'&lt;key&gt;&lt;salt&gt;'</c>) and writes the salt into
/// the first 16 bytes of each database file, so every database carries what is needed to derive
/// the MasterKey again from the password (<c>docs/CRYPTOGRAPHY.md</c> §3, §6).
/// </summary>
public static class StorageKeyMaterial
{
    public const int MasterKeyLengthBytes = 32;
    public const int SaltLengthBytes = StorageSalt.LengthBytes;
    public const int LengthBytes = MasterKeyLengthBytes + SaltLengthBytes;

    public static byte[] Compose(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> salt)
    {
        if (masterKey.Length != MasterKeyLengthBytes)
        {
            throw new ArgumentException("MasterKey должен содержать 32 байта.", nameof(masterKey));
        }
        if (salt.Length != SaltLengthBytes)
        {
            throw new ArgumentException("Соль хранилища должна содержать 16 байт.", nameof(salt));
        }

        byte[] material = new byte[LengthBytes];
        masterKey.CopyTo(material);
        salt.CopyTo(material.AsSpan(MasterKeyLengthBytes));
        return material;
    }

    public static ReadOnlySpan<byte> GetMasterKey(ReadOnlySpan<byte> material) =>
        Validate(material)[..MasterKeyLengthBytes];

    public static ReadOnlySpan<byte> GetSalt(ReadOnlySpan<byte> material) =>
        Validate(material)[MasterKeyLengthBytes..];

    private static ReadOnlySpan<byte> Validate(ReadOnlySpan<byte> material)
    {
        if (material.Length != LengthBytes)
        {
            throw new ArgumentException("Ключ хранилища должен содержать 48 байт: MasterKey и соль.", nameof(material));
        }

        return material;
    }
}

/// <summary>
/// The storage salt: byte 0 is the number of the KDF profile the MasterKey is derived with, the
/// other 15 bytes are random. Created once with the storage and never changed.
/// </summary>
public static class StorageSalt
{
    public const int LengthBytes = 16;

    public static byte[] Create(KeyDerivationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        byte[] salt = RandomNumberGenerator.GetBytes(LengthBytes);
        salt[0] = checked((byte)profile.ProfileVersion);
        return salt;
    }

    public static int GetProfileVersion(ReadOnlySpan<byte> salt)
    {
        if (salt.Length != LengthBytes)
        {
            throw new ArgumentException("Соль хранилища должна содержать 16 байт.", nameof(salt));
        }

        return salt[0];
    }
}
