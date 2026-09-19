using System.Security.Cryptography;
using Clipensk.Core.History;
using Clipensk.Storage.History;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedClipboardHistoryRestoreServiceTests
{
    private static readonly DateOnly StoredDate = new(2026, 3, 1);

    [Fact]
    public async Task PrepareAsync_ReturnsInlinePayloadsInPayloadOrder()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardHistoryEntry entry = Entry(
            InlinePayload(1, "Link", ClipboardHistoryPayloadKind.Link, "https://example.invalid/"),
            InlinePayload(0, "Text", ClipboardHistoryPayloadKind.Text, "copied text"));

        RestorableClipboardEntry prepared = await Service(environment).PrepareAsync(entry);

        Assert.Equal(entry.EventId, prepared.EventId);
        Assert.Equal([0, 1], prepared.Payloads.Select(item => item.PayloadOrder));
        Assert.Equal("copied text", prepared.Payloads[0].InlineCanonicalText);
        Assert.Null(prepared.Payloads[0].ExternalFilePath);
        Assert.Equal("https://example.invalid/", prepared.Payloads[1].InlineCanonicalText);
    }

    [Fact]
    public async Task PrepareAsync_ResolvesAndVerifiesAnExternalPayload()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        byte[] bytes = [1, 2, 3, 4, 5];
        ClipboardHistoryExternalReference reference = WriteExternalPayload(environment, bytes, ".png");
        ClipboardHistoryEntry entry = Entry(
            ExternalPayload(0, "PngImage", ClipboardHistoryPayloadKind.PngImage, reference));

        RestorableClipboardEntry prepared = await Service(environment).PrepareAsync(entry);

        RestorableClipboardPayload payload = Assert.Single(prepared.Payloads);
        Assert.Null(payload.InlineCanonicalText);
        Assert.Equal(reference, payload.ExternalReference);
        Assert.NotNull(payload.ExternalFilePath);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(environment.Root, "Files", reference.RelativePath)),
            payload.ExternalFilePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(payload.ExternalFilePath!));
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedWhenTheExternalFileWasCollectedIntoTrash()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardHistoryExternalReference reference =
            WriteExternalPayload(environment, [9, 9, 9], ".png");
        File.Delete(Path.Combine(environment.Root, "Files", reference.RelativePath));
        ClipboardHistoryEntry entry = Entry(
            ExternalPayload(0, "PngImage", ClipboardHistoryPayloadKind.PngImage, reference));

        await Assert.ThrowsAsync<FileNotFoundException>(() => Service(environment).PrepareAsync(entry));
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedWhenTheExternalContentNoLongerMatchesItsHash()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        byte[] bytes = [1, 2, 3, 4, 5];
        ClipboardHistoryExternalReference reference = WriteExternalPayload(environment, bytes, ".png");
        // Same length, different content: only the hash check can catch this substitution.
        await File.WriteAllBytesAsync(
            Path.Combine(environment.Root, "Files", reference.RelativePath),
            [5, 4, 3, 2, 1]);
        ClipboardHistoryEntry entry = Entry(
            ExternalPayload(0, "PngImage", ClipboardHistoryPayloadKind.PngImage, reference));

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Service(environment).PrepareAsync(entry));
        Assert.Contains("SHA-256", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedWhenTheExternalSizeDoesNotMatch()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardHistoryExternalReference reference =
            WriteExternalPayload(environment, [1, 2, 3], ".png");
        ClipboardHistoryEntry entry = Entry(
            ExternalPayload(
                0,
                "PngImage",
                ClipboardHistoryPayloadKind.PngImage,
                reference with { SizeBytes = reference.SizeBytes + 1 }));

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(environment).PrepareAsync(entry));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("2026-03-01/../../outside.png")]
    public async Task PrepareAsync_FailsClosedWhenTheReferenceEscapesTheFilesRoot(string relativePath)
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(environment.Root, "outside.png"), [7]);
        var reference = new ClipboardHistoryExternalReference(
            Convert.ToHexString(SHA256.HashData(new byte[] { 7 })).ToLowerInvariant(),
            relativePath,
            1);
        ClipboardHistoryEntry entry = Entry(
            ExternalPayload(0, "PngImage", ClipboardHistoryPayloadKind.PngImage, reference));

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(environment).PrepareAsync(entry));
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedOnAnAbsoluteExternalReference()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        string outside = Path.Combine(environment.Root, "outside.png");
        await File.WriteAllBytesAsync(outside, [7]);
        var reference = new ClipboardHistoryExternalReference(
            Convert.ToHexString(SHA256.HashData(new byte[] { 7 })).ToLowerInvariant(),
            outside,
            1);
        ClipboardHistoryEntry entry = Entry(
            ExternalPayload(0, "PngImage", ClipboardHistoryPayloadKind.PngImage, reference));

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(environment).PrepareAsync(entry));
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedWhenAnExternalPayloadAlsoCarriesInlineText()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardHistoryExternalReference reference =
            WriteExternalPayload(environment, [1], ".png");
        var entry = Entry(new ClipboardHistoryPayload(
            0,
            "PngImage",
            ClipboardHistoryPayloadKind.PngImage,
            1,
            "unexpected",
            null,
            reference));

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(environment).PrepareAsync(entry));
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedWhenAnInlinePayloadCarriesAnExternalReference()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardHistoryExternalReference reference =
            WriteExternalPayload(environment, [1], ".png");
        var entry = Entry(new ClipboardHistoryPayload(
            0,
            "Text",
            ClipboardHistoryPayloadKind.Text,
            4,
            "text",
            "text",
            reference));

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(environment).PrepareAsync(entry));
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedWhenAnInlinePayloadHasNoCanonicalText()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var entry = Entry(new ClipboardHistoryPayload(
            0,
            "Text",
            ClipboardHistoryPayloadKind.Text,
            0,
            null,
            null,
            null));

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(environment).PrepareAsync(entry));
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedOnDuplicatePayloadOrders()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardHistoryEntry entry = Entry(
            InlinePayload(0, "Text", ClipboardHistoryPayloadKind.Text, "one"),
            InlinePayload(0, "Link", ClipboardHistoryPayloadKind.Link, "two"));

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(environment).PrepareAsync(entry));
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedOnAnEntryWithoutPayloads()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ClipboardHistoryEntry entry = Entry();

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(environment).PrepareAsync(entry));
    }

    private static ProtectedClipboardHistoryRestoreService Service(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session);

    private static ClipboardHistoryEntry Entry(params ClipboardHistoryPayload[] payloads) =>
        new(
            Guid.NewGuid(),
            new EventTimeContext(
                new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero),
                "UTC"),
            null,
            null,
            payloads);

    private static ClipboardHistoryPayload InlinePayload(
        int order,
        string formatName,
        ClipboardHistoryPayloadKind kind,
        string canonicalText) =>
        new(order, formatName, kind, canonicalText.Length, canonicalText, canonicalText, null);

    private static ClipboardHistoryPayload ExternalPayload(
        int order,
        string formatName,
        ClipboardHistoryPayloadKind kind,
        ClipboardHistoryExternalReference reference) =>
        new(order, formatName, kind, reference.SizeBytes, null, null, reference);

    private static ClipboardHistoryExternalReference WriteExternalPayload(
        GlobalPolicyTestEnvironment environment,
        byte[] bytes,
        string extension)
    {
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string relativePath = Path.Combine(
            StoredDate.ToString("yyyy-MM-dd"),
            sha256 + extension);
        string fullPath = Path.Combine(environment.Root, "Files", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
        return new ClipboardHistoryExternalReference(sha256, relativePath, bytes.Length);
    }
}
