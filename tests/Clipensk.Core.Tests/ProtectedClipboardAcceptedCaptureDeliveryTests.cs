using Clipensk.Core.Application;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ProtectedClipboardAcceptedCaptureDeliveryTests
{
    [Fact]
    public async Task ProcessNextAsync_UsesSessionCancellationTokenWhenCallerTokenCannotCancel()
    {
        var (lifecycle, session) = CreateUnlockedSession();
        using (session)
        {
            var inner = new RecordingDelivery(result: true);
            var delivery = new ProtectedClipboardAcceptedCaptureDelivery(inner, session);

            bool result = await delivery.ProcessNextAsync();

            Assert.True(result);
            Assert.Equal(session.CancellationToken, inner.ObservedCancellationToken);
            Assert.True(lifecycle.CanAccessProtectedData);
        }
    }

    [Fact]
    public async Task ProcessNextAsync_CancelsInFlightDeliveryWhenProtectedAccessIsRevoked()
    {
        var (lifecycle, session) = CreateUnlockedSession();
        using (session)
        {
            var inner = new BlockingDelivery();
            var delivery = new ProtectedClipboardAcceptedCaptureDelivery(inner, session);

            Task<bool> pending = delivery.ProcessNextAsync().AsTask();
            await inner.Entered;

            Assert.True(lifecycle.TryBeginLock());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(inner.ObservedCancellationToken.IsCancellationRequested);
        }
    }

    [Fact]
    public async Task ProcessNextAsync_LinksCallerCancellationWithSessionCancellation()
    {
        var (_, session) = CreateUnlockedSession();
        using (session)
        using (var callerCancellation = new CancellationTokenSource())
        {
            var inner = new BlockingDelivery();
            var delivery = new ProtectedClipboardAcceptedCaptureDelivery(inner, session);

            Task<bool> pending = delivery.ProcessNextAsync(callerCancellation.Token).AsTask();
            await inner.Entered;

            callerCancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(inner.ObservedCancellationToken.IsCancellationRequested);
            Assert.False(session.CancellationToken.IsCancellationRequested);
        }
    }

    private static (ProtectedApplicationLifecycle Lifecycle, ProtectedStorageSessionLease Session)
        CreateUnlockedSession()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
        Assert.True(lifecycle.TryBeginUnlock());
        lifecycle.CompleteUnlock();

        var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\Clipensk-Test",
            Guid.NewGuid(),
            new MasterKeyLease([1, 2, 3, 4]));

        return (lifecycle, session);
    }

    private sealed class RecordingDelivery : IClipboardAcceptedCaptureDelivery
    {
        private readonly bool _result;

        public RecordingDelivery(bool result)
        {
            _result = result;
        }

        public CancellationToken ObservedCancellationToken { get; private set; }

        public ValueTask<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedCancellationToken = cancellationToken;
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class BlockingDelivery : IClipboardAcceptedCaptureDelivery
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public CancellationToken ObservedCancellationToken { get; private set; }

        public async ValueTask<bool> ProcessNextAsync(
            CancellationToken cancellationToken = default)
        {
            ObservedCancellationToken = cancellationToken;
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return true;
        }
    }
}
