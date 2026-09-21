using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Storage.History;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ClipboardRestorePlanTests
{
    private const string PlainText = "Text";

    [Fact]
    public void Create_KeepsPublishablePayloadsInOrder()
    {
        RestorableClipboardEntry entry = Entry(
            Inline(0, PlainText, ClipboardHistoryPayloadKind.Text, "copied"),
            External(1, "Bitmap", ClipboardHistoryPayloadKind.PngImage, "/data/Files/2026-03-01/a.png"));

        ClipboardRestorePlan plan = ClipboardRestorePlanFactory.Create(entry, PlainText);

        Assert.Empty(plan.SkippedFormatNames);
        Assert.Empty(plan.TextConvertedFormatNames);
        Assert.Equal([PlainText, "Bitmap"], plan.Items.Select(item => item.FormatName));
        Assert.Equal("copied", plan.Items[0].InlineCanonicalText);
        Assert.Equal("/data/Files/2026-03-01/a.png", plan.Items[1].ExternalFilePath);
    }

    [Fact]
    public void Create_RepublishesAFileDropAsOneFullPathPerLineInStoredOrder()
    {
        RestorableClipboardEntry entry = Entry(Inline(
            0,
            "StorageItems",
            ClipboardHistoryPayloadKind.StorageItems,
            Canonical(@"C:\reports\q3.xlsx", @"C:\reports\archive")));

        ClipboardRestorePlan plan = ClipboardRestorePlanFactory.Create(entry, PlainText);

        ClipboardRestoreItem item = Assert.Single(plan.Items);
        Assert.Equal(PlainText, item.FormatName);
        Assert.Equal(ClipboardHistoryPayloadKind.Text, item.Kind);
        Assert.Equal("C:\\reports\\q3.xlsx\r\nC:\\reports\\archive", item.InlineCanonicalText);
        Assert.Equal("StorageItems", Assert.Single(plan.TextConvertedFormatNames));
        Assert.Empty(plan.SkippedFormatNames);
    }

    [Fact]
    public void Create_NeverOverwritesCapturedPlainTextWithAFileList()
    {
        // A clipboard holds one plain-text value, and the captured one is the user's own.
        RestorableClipboardEntry entry = Entry(
            Inline(0, PlainText, ClipboardHistoryPayloadKind.Text, "copied"),
            Inline(1, "StorageItems", ClipboardHistoryPayloadKind.StorageItems, Canonical(@"C:\a.txt")));

        ClipboardRestorePlan plan = ClipboardRestorePlanFactory.Create(entry, PlainText);

        Assert.Equal("copied", Assert.Single(plan.Items).InlineCanonicalText);
        Assert.Equal("StorageItems", Assert.Single(plan.SkippedFormatNames));
        Assert.Empty(plan.TextConvertedFormatNames);
    }

    [Fact]
    public void Create_ConvertsAFileDropAlongsideNonTextPayloads()
    {
        RestorableClipboardEntry entry = Entry(
            External(0, "Bitmap", ClipboardHistoryPayloadKind.PngImage, "/data/Files/2026-03-01/a.png"),
            Inline(1, "StorageItems", ClipboardHistoryPayloadKind.StorageItems, Canonical(@"C:\a.txt")));

        ClipboardRestorePlan plan = ClipboardRestorePlanFactory.Create(entry, PlainText);

        Assert.Equal(["Bitmap", PlainText], plan.Items.Select(item => item.FormatName));
        Assert.Equal("StorageItems", Assert.Single(plan.TextConvertedFormatNames));
    }

    [Fact]
    public void Create_FailsClosedOnAnUnreadableFileDrop()
    {
        RestorableClipboardEntry entry = Entry(Inline(
            0,
            "StorageItems",
            ClipboardHistoryPayloadKind.StorageItems,
            "{\"version\":99,\"items\":[]}"));

        Assert.Throws<InvalidDataException>(() => ClipboardRestorePlanFactory.Create(entry, PlainText));
    }

    [Fact]
    public void Create_FailsClosedOnAnEntryWithNoPayloads()
    {
        Assert.Throws<InvalidDataException>(() => ClipboardRestorePlanFactory.Create(Entry(), PlainText));
    }

    [Fact]
    public void Create_RequiresAPlainTextFormatName()
    {
        RestorableClipboardEntry entry = Entry(
            Inline(0, PlainText, ClipboardHistoryPayloadKind.Text, "copied"));

        Assert.Throws<ArgumentException>(() => ClipboardRestorePlanFactory.Create(entry, " "));
    }

    [Fact]
    public void CreatePlainTextOnly_PublishesTheFirstPayloadWithSearchTextInStoredOrder()
    {
        RestorableClipboardEntry entry = Entry(
            External(0, "Bitmap", ClipboardHistoryPayloadKind.PngImage, "/data/Files/2026-03-01/a.png"),
            Inline(1, "HTML Format", ClipboardHistoryPayloadKind.Text, "<b>hi</b>", searchText: "hi"),
            Inline(2, PlainText, ClipboardHistoryPayloadKind.Text, "hi", searchText: "hi"));

        ClipboardRestorePlan plan = ClipboardRestorePlanFactory.CreatePlainTextOnly(entry, PlainText);

        ClipboardRestoreItem item = Assert.Single(plan.Items);
        Assert.Equal(PlainText, item.FormatName);
        Assert.Equal(ClipboardHistoryPayloadKind.Text, item.Kind);
        Assert.Equal("hi", item.InlineCanonicalText);
        Assert.Null(item.ExternalFilePath);
        Assert.Empty(plan.TextConvertedFormatNames);
        Assert.Equal(["Bitmap", PlainText], plan.SkippedFormatNames);
    }

    [Fact]
    public void CreatePlainTextOnly_FallsBackToTheRawUrlForALinkPayloadWithoutSearchText()
    {
        RestorableClipboardEntry entry = Entry(
            Inline(0, "UniformResourceLocator", ClipboardHistoryPayloadKind.Link, "https://example.invalid/"));

        ClipboardRestorePlan plan = ClipboardRestorePlanFactory.CreatePlainTextOnly(entry, PlainText);

        Assert.Equal("https://example.invalid/", Assert.Single(plan.Items).InlineCanonicalText);
    }

    [Fact]
    public void CreatePlainTextOnly_DiscardsEveryOtherFormatEvenWhenTextIsCaptured()
    {
        RestorableClipboardEntry entry = Entry(
            Inline(0, PlainText, ClipboardHistoryPayloadKind.Text, "hi", searchText: "hi"),
            External(1, "Bitmap", ClipboardHistoryPayloadKind.PngImage, "/data/Files/2026-03-01/a.png"));

        ClipboardRestorePlan plan = ClipboardRestorePlanFactory.CreatePlainTextOnly(entry, PlainText);

        Assert.Single(plan.Items);
        Assert.Equal("Bitmap", Assert.Single(plan.SkippedFormatNames));
    }

    [Fact]
    public void CreatePlainTextOnly_NeverConvertsAFileDropOfItsOwnAccord()
    {
        RestorableClipboardEntry entry = Entry(Inline(
            0,
            "StorageItems",
            ClipboardHistoryPayloadKind.StorageItems,
            Canonical(@"C:\reports\q3.xlsx")));

        Assert.Throws<InvalidDataException>(
            () => ClipboardRestorePlanFactory.CreatePlainTextOnly(entry, PlainText));
    }

    [Fact]
    public void CreatePlainTextOnly_FailsClosedWhenNothingHasAPlainTextRepresentation()
    {
        RestorableClipboardEntry entry = Entry(
            External(0, "Bitmap", ClipboardHistoryPayloadKind.PngImage, "/data/Files/2026-03-01/a.png"));

        Assert.Throws<InvalidDataException>(
            () => ClipboardRestorePlanFactory.CreatePlainTextOnly(entry, PlainText));
    }

    [Fact]
    public void CreatePlainTextOnly_RequiresAPlainTextFormatName()
    {
        RestorableClipboardEntry entry = Entry(
            Inline(0, PlainText, ClipboardHistoryPayloadKind.Text, "hi", searchText: "hi"));

        Assert.Throws<ArgumentException>(() => ClipboardRestorePlanFactory.CreatePlainTextOnly(entry, " "));
    }

    private static string Canonical(params string[] fullPaths) =>
        ClipboardStorageItemsCanonicalizer.Create(
            [.. fullPaths.Select((path, index) => new ClipboardStorageItemMetadata(
                path,
                Path.GetFileName(path),
                Path.GetExtension(path),
                IsDirectory: false,
                index,
                ClipboardPreferredFileOperation.Copy))]).Text;

    private static RestorableClipboardEntry Entry(params RestorableClipboardPayload[] payloads) =>
        new(Guid.NewGuid(), payloads);

    private static RestorableClipboardPayload Inline(
        int order,
        string formatName,
        ClipboardHistoryPayloadKind kind,
        string text,
        string? searchText = null) =>
        new(order, formatName, kind, text, null, null, text.Length, searchText);

    private static RestorableClipboardPayload External(
        int order,
        string formatName,
        ClipboardHistoryPayloadKind kind,
        string path,
        string? searchText = null) =>
        new(
            order,
            formatName,
            kind,
            null,
            path,
            new ClipboardHistoryExternalReference(new string('a', 64), "2026-03-01/a.png", 3),
            3,
            searchText);
}
