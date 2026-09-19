using System.Text;
using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardStorageItemsCanonicalizerTests
{
    [Fact]
    public void Create_ProducesDeterministicVersionedUtf8Json()
    {
        ClipboardStorageItemMetadata[] items =
        [
            new ClipboardStorageItemMetadata(
                "C:\\Temp\\Ж.txt",
                "Ж.txt",
                ".txt",
                IsDirectory: false,
                Order: 0,
                ClipboardPreferredFileOperation.Copy),
            new ClipboardStorageItemMetadata(
                "C:\\Temp\\Folder",
                "Folder",
                string.Empty,
                IsDirectory: true,
                Order: 1,
                ClipboardPreferredFileOperation.Move),
        ];

        ClipboardStorageItemsCanonicalRepresentation result =
            ClipboardStorageItemsCanonicalizer.Create(items);

        const string Expected =
            "{\"version\":1,\"items\":[" +
            "{\"order\":0,\"fullPath\":\"C:\\\\Temp\\\\Ж.txt\",\"name\":\"Ж.txt\",\"extension\":\".txt\",\"isDirectory\":false,\"preferredOperation\":\"copy\"}," +
            "{\"order\":1,\"fullPath\":\"C:\\\\Temp\\\\Folder\",\"name\":\"Folder\",\"extension\":\"\",\"isDirectory\":true,\"preferredOperation\":\"move\"}" +
            "]}";

        Assert.Equal(Expected, result.Text);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(Expected), result.ByteCount);
        Assert.True(result.ByteCount > result.Text.Length);
    }

    [Fact]
    public void CapturedContent_TwoArgumentConstructorBuildsCanonicalRepresentation()
    {
        ClipboardStorageItemMetadata[] items =
        [
            new ClipboardStorageItemMetadata(
                "C:\\Temp\\a.txt",
                "a.txt",
                ".txt",
                IsDirectory: false,
                Order: 0,
                ClipboardPreferredFileOperation.Link),
        ];
        ClipboardStorageItemsCanonicalRepresentation expected =
            ClipboardStorageItemsCanonicalizer.Create(items);
        var route = new ClipboardContentReaderRoute(
            new ClipboardSelectedFormat("StorageItems", null),
            ClipboardContentReaderKind.StorageItems);

        var captured = new ClipboardCapturedStorageItemsContent(route, items);

        Assert.Equal(expected.Text, captured.CanonicalRepresentation);
        Assert.Equal(expected.ByteCount, captured.CanonicalByteCount);
        Assert.Equal(items, captured.Items);
    }

    [Theory]
    [InlineData(ClipboardPreferredFileOperation.Unknown, "unknown")]
    [InlineData(ClipboardPreferredFileOperation.Copy, "copy")]
    [InlineData(ClipboardPreferredFileOperation.Move, "move")]
    [InlineData(ClipboardPreferredFileOperation.Link, "link")]
    public void Create_UsesStablePreferredOperationNames(
        ClipboardPreferredFileOperation operation,
        string expectedName)
    {
        ClipboardStorageItemMetadata[] items =
        [
            new ClipboardStorageItemMetadata(
                "C:\\item",
                "item",
                string.Empty,
                IsDirectory: true,
                Order: 0,
                operation),
        ];

        ClipboardStorageItemsCanonicalRepresentation result =
            ClipboardStorageItemsCanonicalizer.Create(items);

        Assert.Contains($"\"preferredOperation\":\"{expectedName}\"", result.Text);
    }

    [Fact]
    public void Create_RejectsNonContiguousOrder()
    {
        ClipboardStorageItemMetadata[] items =
        [
            new ClipboardStorageItemMetadata(
                "C:\\item",
                "item",
                string.Empty,
                IsDirectory: true,
                Order: 1,
                ClipboardPreferredFileOperation.Unknown),
        ];

        Assert.Throws<InvalidDataException>(() =>
            ClipboardStorageItemsCanonicalizer.Create(items));
    }

    [Fact]
    public void ReadFullPaths_RoundTripsWhatTheWriterProduced()
    {
        // Writer and reader share one schema; this is the guard against them drifting apart.
        string canonical = ClipboardStorageItemsCanonicalizer.Create(
        [
            new ClipboardStorageItemMetadata(
                "C:\\reports\\q3.xlsx",
                "q3.xlsx",
                ".xlsx",
                IsDirectory: false,
                Order: 0,
                ClipboardPreferredFileOperation.Copy),
            new ClipboardStorageItemMetadata(
                "C:\\reports\\archive",
                "archive",
                string.Empty,
                IsDirectory: true,
                Order: 1,
                ClipboardPreferredFileOperation.Move),
        ]).Text;

        Assert.Equal(
            new[] { "C:\\reports\\q3.xlsx", "C:\\reports\\archive" },
            ClipboardStorageItemsCanonicalizer.ReadFullPaths(canonical));
    }

    [Fact]
    public void ReadFullPaths_RejectsAnUnsupportedVersion()
    {
        Assert.Throws<InvalidDataException>(() => ClipboardStorageItemsCanonicalizer.ReadFullPaths(
            "{\"version\":2,\"items\":[{\"order\":0,\"fullPath\":\"C:\\\\a.txt\"}]}"));
    }

    [Fact]
    public void ReadFullPaths_RejectsNonContiguousOrder()
    {
        Assert.Throws<InvalidDataException>(() => ClipboardStorageItemsCanonicalizer.ReadFullPaths(
            "{\"version\":1,\"items\":[{\"order\":1,\"fullPath\":\"C:\\\\a.txt\"}]}"));
    }

    [Fact]
    public void ReadFullPaths_RejectsAnItemWithoutAPath()
    {
        Assert.Throws<InvalidDataException>(() => ClipboardStorageItemsCanonicalizer.ReadFullPaths(
            "{\"version\":1,\"items\":[{\"order\":0,\"fullPath\":\"  \"}]}"));
    }

    [Fact]
    public void ReadFullPaths_RejectsAnEmptyItemArray()
    {
        Assert.Throws<InvalidDataException>(() => ClipboardStorageItemsCanonicalizer.ReadFullPaths(
            "{\"version\":1,\"items\":[]}"));
    }

    [Fact]
    public void ReadFullPaths_RejectsMalformedJson()
    {
        Assert.Throws<InvalidDataException>(() =>
            ClipboardStorageItemsCanonicalizer.ReadFullPaths("{\"version\":1,"));
    }
}
