using Clipensk.Core.Clipboard;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardAcceptedCaptureWorkerTests
{
    [Fact]
    public async Task RunAsync_CancellationStopsBlockedDelivery()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new DelegateDelivery(async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        });
        var worker = new ClipboardAcceptedCaptureWorker(delivery);
        using var cancellation = new CancellationTokenSource();

        Task run = worker.RunAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, delivery.CallCount);
    }

    [Fact]
    public async Task RunAsync_FailedCaptureDoesNotStopLaterCapture()
    {
        using var cancellation = new CancellationTokenSource();
        var delivery = new DelegateDelivery(cancellationToken =>
        {
            if (deliveryCallCount == 0)
            {
                deliveryCallCount++;
                return Task.FromException<bool>(new InvalidDataException("Rejected capture."));
            }

            deliveryCallCount++;
            cancellation.Cancel();
            return Task.FromResult(true);
        });
        var worker = new ClipboardAcceptedCaptureWorker(delivery);

        await worker.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, deliveryCallCount);

        int deliveryCallCount = 0;
    }

    [Fact]
    public async Task RunAsync_ProcessesOnlyOneCaptureAtATime()
    {
        using var cancellation = new CancellationTokenSource();
        int active = 0;
        int maxActive = 0;
        int completed = 0;
        var delivery = new DelegateDelivery(async cancellationToken =>
        {
            int current = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, current);
            try
            {
                await Task.Yield();
                if (Interlocked.Increment(ref completed) == 3)
                {
                    cancellation.Cancel();
                }

                return true;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var worker = new ClipboardAcceptedCaptureWorker(delivery);

        await worker.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, completed);
        Assert.Equal(1, maxActive);
    }

    private sealed class DelegateDelivery : IClipboardAcceptedCaptureDelivery
    {
        private readonly Func<CancellationToken, Task<bool>> _handler;
        private int _callCount;

        public DelegateDelivery(Func<CancellationToken, Task<bool>> handler)
        {
            _handler = handler;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return new ValueTask<bool>(_handler(cancellationToken));
        }
    }
}
