namespace Clipensk.Core.Security;

public sealed record KeyDerivationProfile(
    int ProfileVersion,
    int Argon2Version,
    int MemoryKiB,
    int Iterations,
    int Parallelism,
    int SaltLengthBytes,
    int MasterKeyLengthBytes)
{
    // RFC 9106, section 4, second recommended Argon2id profile:
    // 64 MiB, 3 passes, 4 lanes, 128-bit salt, 256-bit output.
    public static KeyDerivationProfile ProductionV1 { get; } = new(
        ProfileVersion: 1,
        Argon2Version: 0x13,
        MemoryKiB: 65_536,
        Iterations: 3,
        Parallelism: 4,
        SaltLengthBytes: 16,
        MasterKeyLengthBytes: 32);

    public void Validate()
    {
        if (ProfileVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ProfileVersion));
        }

        if (Argon2Version != 0x13)
        {
            throw new NotSupportedException($"Неподдерживаемая версия Argon2: 0x{Argon2Version:X2}.");
        }

        if (Parallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism));
        }

        if (MemoryKiB < 8 * Parallelism)
        {
            throw new ArgumentOutOfRangeException(nameof(MemoryKiB));
        }

        if (Iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Iterations));
        }

        if (SaltLengthBytes < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(SaltLengthBytes));
        }

        if (MasterKeyLengthBytes < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(MasterKeyLengthBytes));
        }
    }
}
