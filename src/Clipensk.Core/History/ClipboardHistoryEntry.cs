using Clipensk.Core.Clipboard;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Core.History;

/// <summary>A detached projection of one persisted Current history event.</summary>
public sealed record ClipboardHistoryEntry(
    Guid EventId,
    EventTimeContext EventTime,
    DurableApplicationId? SourceApplicationId,
    ClipboardSourceApplication? SourceApplication,
    IReadOnlyList<ClipboardHistoryPayload> Payloads);

public sealed record ClipboardHistoryPayload(
    int PayloadOrder,
    string FormatName,
    ClipboardHistoryPayloadKind Kind,
    long CanonicalByteCount,
    string? InlineCanonicalText,
    string? SearchText,
    ClipboardHistoryExternalReference? ExternalReference);

public enum ClipboardHistoryPayloadKind
{
    Text,
    Link,
    PngImage,
    CustomBinary,
    StorageItems,
}

/// <summary>Persisted address metadata only; contains no external file bytes.</summary>
public sealed record ClipboardHistoryExternalReference(
    string Sha256,
    string RelativePath,
    long SizeBytes);
