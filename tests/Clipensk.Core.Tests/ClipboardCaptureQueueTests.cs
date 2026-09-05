using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardCaptureQueueTests
{
    [Fact]
    public async Task EnqueueThenDequeue_ReturnsRequest()
    {
        var queue = new ClipboardCaptureQueue();
        var request = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 15, 30, TimeSpan.FromHours(3)),
                "Test/Zone"));

        Assert.True(queue.TryEnqueue(request));

        ClipboardCaptureRequest actual = await queue.DequeueAsync();
        Assert.Equal(request, actual);
    }

    [Fact]
    public async Task MultiplePendingUpdates_CoalesceToLatestRequest()
    {
        var queue = new ClipboardCaptureQueue();
        var first = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 15, 29, TimeSpan.FromHours(3)),
                "Test/Zone"));
        var latest = new ClipboardCaptureRequest(
            new EventTimeContext(
                new DateTimeOffset(2026, 9, 5, 10, 15, 30, TimeSpan.FromHours(3)),
                "Test/Zone"));

        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(latest));

        ClipboardCaptureRequest actual = await queue.DequeueAsync();
        Assert.Equal(latest, actual);
    }

    [Fact]
    public void CaptureRequest_PreservesEventTimeContext()
    {
        var eventTime = new EventTimeContext(
            new DateTimeOffset(2026, 9, 5, 23, 30, 0, TimeSpan.FromHours(-5)),
            "Central Standard Time");
        var request = new ClipboardCaptureRequest(eventTime);

        Assert.Equal(new DateTime(2026, 9, 6, 4, 30, 0, DateTimeKind.Utc), request.EventTime.UtcTimestamp);
        Assert.Equal(TimeSpan.FromHours(-5), request.EventTime.Offset);
        Assert.Equal(new DateOnly(2026, 9, 5), request.EventTime.CalendarDate);
        Assert.Equal("Central Standard Time", request.EventTime.WindowsTimeZoneId);
    }
}
