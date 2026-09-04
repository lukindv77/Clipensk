using Clipensk.Core.Security;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class SecurityRulesTests
{
    [Fact]
    public void ProductionKdfProfile_IsPinnedToArgon2idV1Parameters()
    {
        KeyDerivationProfile profile = KeyDerivationProfile.ProductionV1;

        Assert.Equal(1, profile.ProfileVersion);
        Assert.Equal(0x13, profile.Argon2Version);
        Assert.Equal(65_536, profile.MemoryKiB);
        Assert.Equal(3, profile.Iterations);
        Assert.Equal(4, profile.Parallelism);
        Assert.Equal(16, profile.SaltLengthBytes);
        Assert.Equal(32, profile.MasterKeyLengthBytes);
    }

    [Fact]
    public void MasterKeyLease_ZeroesOwnedBufferOnDispose()
    {
        var lease = new MasterKeyLease([1, 2, 3, 4]);
        ReadOnlyMemory<byte> observedMemory = lease.DangerousGetMemory();

        lease.Dispose();

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, observedMemory.ToArray());
        Assert.Throws<ObjectDisposedException>(() => lease.DangerousGetMemory());
    }
}
