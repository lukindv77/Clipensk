using Clipensk.Core.History;
using Clipensk.Storage.History;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ClipboardRestorePlanTests
{
    [Fact]
    public void Create_KeepsPublishablePayloadsInOrder()
    {
        RestorableClipboardEntry entry = Entry(
            Inline(0, "Text", ClipboardHistoryPayloadKind.Text, "copied"),
            External(1, "Bitmap", ClipboardHistoryPayloadKind.PngImage, "/data/Files/2026-03-01/a.png"));

        ClipboardRestorePlan plan = ClipboardRestorePlan.Create(entry);

        Assert.Empty(plan.SkippedFormatNames);
        Assert.Equal(["Text", "Bitmap"], plan.Items.Select(item => item.FormatName));
        Assert.Equal("copied", plan.Items[0].InlineCanonicalText);
        Assert.Equal("/data/Files/2026-03-01/a.png", plan.Items[1].ExternalFilePath);
    }

    [Fact]
    public void Create_SkipsStorageItemsAndReportsThem()
    {
        RestorableClipboardEntry entry = Entry(
            Inline(0, "Text", ClipboardHistoryPayloadKind.Text, "copied"),
            Inline(1, "StorageItems", ClipboardHistoryPayloadKind.StorageItems, "{\"version\":1}"));

        ClipboardRestorePlan plan = ClipboardRestorePlan.Create(entry);

        Assert.Equal("StorageItems", Assert.Single(plan.SkippedFormatNames));
        Assert.Equal("Text", Assert.Single(plan.Items).FormatName);
    }

    [Fact]
    public void Create_FailsClosedWhenOnlyStorageItemsWereStored()
    {
        // Clipensk never stores the dropped files themselves, so a file-only entry has nothing
        // durable to republish. Replacing the user's clipboard with an empty package is worse.
        RestorableClipboardEntry entry = Entry(
            Inline(0, "StorageItems", ClipboardHistoryPayloadKind.StorageItems, "{\"version\":1}"));

        Assert.Throws<InvalidDataException>(() => ClipboardRestorePlan.Create(entry));
    }

    [Fact]
    public void Create_FailsClosedOnAnEntryWithNoPayloads()
    {
        Assert.Throws<InvalidDataException>(() => ClipboardRestorePlan.Create(Entry()));
    }

    private static RestorableClipboardEntry Entry(params RestorableClipboardPayload[] payloads) =>
        new(Guid.NewGuid(), payloads);

    private static RestorableClipboardPayload Inline(
        int order,
        string formatName,
        ClipboardHistoryPayloadKind kind,
        string text) =>
        new(order, formatName, kind, text, null, null, text.Length);

    private static RestorableClipboardPayload External(
        int order,
        string formatName,
        ClipboardHistoryPayloadKind kind,
        string path) =>
        new(
            order,
            formatName,
            kind,
            null,
            path,
            new ClipboardHistoryExternalReference(new string('a', 64), "2026-03-01/a.png", 3),
            3);
}
