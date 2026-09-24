using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Infrastructure.Clipboard;
using Clipensk.Infrastructure.Localization;
using Clipensk.Windows;
using Windows.ApplicationModel.DataTransfer;
using WindowsClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipensk.Clipboard.Probe;

/// <summary>
/// Reads the Windows clipboard the way Clipensk's resident capture does — through
/// <see cref="ResidentWindowsHost"/>'s capture pipeline, on a thread-pool thread after a capture
/// request was queued — and reports whether the text put on the clipboard comes back. The raw WinRT
/// clipboard is also read directly on the STA main thread and on a thread-pool thread, to tell an
/// apartment problem from a pipeline one. Exit code 0 only when the pipeline read succeeds.
/// </summary>
internal static class Program
{
    private const string ProbeText = "Clipensk clipboard probe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [STAThread]
    private static int Main()
    {
        var package = new DataPackage();
        package.SetText(ProbeText);
        WindowsClipboard.SetContent(package);
        WindowsClipboard.Flush();
        Console.WriteLine($"Main thread apartment: {Thread.CurrentThread.GetApartmentState()}");

        Report("WinRT clipboard, STA main thread", ReadDirectlyAsync());
        Report("WinRT clipboard, thread-pool thread", Task.Run(ReadDirectlyAsync));
        bool pipeline = Report("Clipensk capture pipeline, thread-pool thread (as the resident worker)", ReadThroughPipelineAsync());
        return pipeline ? 0 : 1;
    }

    private static async Task<string> ReadDirectlyAsync()
    {
        DataPackageView content = WindowsClipboard.GetContent();
        return await content.GetTextAsync();
    }

    private static async Task<string> ReadThroughPipelineAsync()
    {
        using var host = new ResidentWindowsHost(
            new HtmlAgilityPackClipboardHtmlSearchTextConverter(),
            new ManagedClipboardRtfSearchTextConverter(),
            new BuiltInRussianLocalizationService());
        ClipboardCaptureReadExecutionPipeline pipeline =
            host.CreateCaptureReadExecutionPipeline(new AllowTextPolicyProvider());
        if (!host.CaptureQueue.TryEnqueue(new ClipboardCaptureRequest(EventTimeContext.CaptureNow())))
        {
            throw new InvalidOperationException("The capture request was not queued.");
        }

        // The resident worker runs the pipeline on the thread pool (App.ClipboardWorker.cs).
        ClipboardContentReadExecution execution = await Task.Run(
            async () => await pipeline.ProcessNextAsync().ConfigureAwait(false));
        ClipboardCapturedTextContent? text = execution.CapturedContent
            .OfType<ClipboardCapturedTextContent>()
            .FirstOrDefault();
        return text?.Value ?? throw new InvalidOperationException(
            $"No text captured; formats: {string.Join(", ", execution.Plan.Selection.Formats.Select(static f => f.FormatName))}.");
    }

    private static bool Report(string name, Task<string> read)
    {
        try
        {
            if (!read.Wait(Timeout))
            {
                Console.WriteLine($"FAIL  {name}: no result in {Timeout.TotalSeconds} s.");
                return false;
            }

            bool ok = read.Result == ProbeText;
            Console.WriteLine(ok
                ? $"PASS  {name}: read the probe text."
                : $"FAIL  {name}: read '{read.Result}'.");
            return ok;
        }
        catch (AggregateException exception)
        {
            Exception inner = exception.GetBaseException();
            Console.WriteLine($"FAIL  {name}: {inner.GetType().FullName} (0x{inner.HResult:X8}): {inner.Message}");
            Console.WriteLine(inner.StackTrace);
            return false;
        }
    }

    private sealed class AllowTextPolicyProvider : IClipboardCapturePolicyProvider
    {
        public ValueTask<ClipboardCapturePolicySet> GetPoliciesAsync(
            ClipboardCaptureContext captureContext,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ClipboardCapturePolicySet(new ClipboardCapturePolicy(
                ClipboardCapturePolicyRule.Allow,
                new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
                {
                    [StandardDataFormats.Text] = new(ClipboardCapturePolicyRule.Allow, 1024 * 1024),
                })));
    }
}
