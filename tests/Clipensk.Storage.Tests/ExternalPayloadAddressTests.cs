using System.Security.Cryptography;
using System.Text;
using Clipensk.Storage.ExternalFiles;

namespace Clipensk.Storage.Tests;

public sealed class ExternalPayloadAddressTests
{
    [Fact]
    public void NormalizedPng_UsesSha256AndPngExtension()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("normalized-png-placeholder");
        string expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        ExternalPayloadAddress address = ExternalPayloadAddressFactory.ForNormalizedPng(
            new DateOnly(2026, 9, 4),
            bytes);

        Assert.Equal(expectedHash, address.Sha256);
        Assert.Equal(Path.Combine("2026-09-04", expectedHash + ".png"), address.RelativePath);
        Assert.Equal(bytes.Length, address.SizeBytes);
    }

    [Fact]
    public void SameBytes_ProduceSameContentId()
    {
        byte[] bytes = [1, 2, 3, 4, 5];

        ExternalPayloadAddress first = ExternalPayloadAddressFactory.ForNormalizedPng(
            new DateOnly(2026, 9, 4),
            bytes);
        ExternalPayloadAddress second = ExternalPayloadAddressFactory.ForNormalizedPng(
            new DateOnly(2026, 9, 5),
            bytes);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.NotEqual(first.RelativePath, second.RelativePath);
    }
}
