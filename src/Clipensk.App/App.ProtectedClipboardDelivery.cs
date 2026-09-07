using Clipensk.Core.Application;
using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.History;
using Clipensk.Windows;

namespace Clipensk.App;

public partial class App
{
    private ProtectedClipboardDeliveryServices? _clipboardDeliveryServices;
    private long _clipboardCompositionGeneration;

    private void RequestClipboardDeliveryComposition(
        JournalWindow window,
        ResidentWindowsHost host)
    {
        ProtectedApplicationLifecycle? lifecycle = _lifecycle;
        if (lifecycle is null ||
            !lifecycle.CanAccessProtectedData ||
            !window.TryGetActiveProtectedStorageSession(out ProtectedStorageSessionLease? session) ||
            session is null)
        {
            return;
        }

        long generation = Interlocked.Increment(ref _clipboardCompositionGeneration);
        _ = ComposeProtectedClipboardDeliveryAsync(
            window,
            host,
            lifecycle,
            session,
            generation);
    }

    private async Task ComposeProtectedClipboardDeliveryAsync(
        JournalWindow window,
        ResidentWindowsHost host,
        ProtectedApplicationLifecycle lifecycle,
        ProtectedStorageSessionLease session,
        long generation)
    {
        ProtectedClipboardDeliveryServices? services;
        try
        {
            services = await Task.Run(
                async () =>
                {
                    var configurationRepository =
                        new SqliteCustomBinaryFormatConfigurationRepository(session);
                    var extensionProvider =
                        new RepositoryClipboardCustomBinaryFileExtensionProvider(
                            configurationRepository);

                    return await ProtectedClipboardDeliveryServices.TryCreateAsync(
                        session,
                        host,
                        extensionProvider,
                        cancellationToken: session.CancellationToken);
                },
                session.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // Composition failure must not publish a partial graph or enable capture.
            return;
        }

        if (generation != Volatile.Read(ref _clipboardCompositionGeneration) ||
            !IsCurrentClipboardRuntimeSession(host, window, lifecycle, session))
        {
            return;
        }

        _clipboardDeliveryServices = services;
        if (services is null)
        {
            InvalidateClipboardWorker();
            TrySetClipboardMonitoring(host, start: false);
            return;
        }

        RequestClipboardWorkerStart(
            window,
            host,
            lifecycle,
            session,
            services);
        TryStartClipboardMonitoringForSession(
            host,
            window,
            lifecycle,
            session);
    }

    private void InvalidateClipboardDeliveryComposition()
    {
        Interlocked.Increment(ref _clipboardCompositionGeneration);
        _clipboardDeliveryServices = null;
    }

    private void OnGlobalCapturePolicyInitialized(object? sender, EventArgs e)
    {
        JournalWindow? window = _window;
        ResidentWindowsHost? host = _residentWindowsHost;
        if (window is null ||
            host is null ||
            !ReferenceEquals(sender, window))
        {
            return;
        }

        try
        {
            RequestClipboardDeliveryComposition(window, host);
        }
        catch
        {
            // The policy is already durably committed. Runtime composition is retried
            // independently and must not retroactively turn that commit into a failure.
        }
    }
}
