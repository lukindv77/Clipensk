using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Infrastructure.Clipboard;
using Clipensk.Infrastructure.Localization;
using Clipensk.Windows;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WindowsClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Clipensk.Clipboard.Probe;

/// <summary>
/// Puts content on the Windows clipboard and reads it back the way Clipensk's resident capture does:
/// through <see cref="ResidentWindowsHost"/>'s capture pipeline, on a thread-pool thread, after a
/// capture request was queued. Covers plain text, a web fragment (text and HTML), an image and a file
/// list. The raw WinRT clipboard is also read directly on the STA main thread and on a thread-pool
/// thread, to tell an apartment problem from a pipeline one. Exit code 0 only when every pipeline read
/// returns what was put on the clipboard.
/// </summary>
internal static class Program
{
    private const string ProbeText = "Clipensk clipboard probe";
    private const string HtmlMarker = "Clipensk HTML probe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [STAThread]
    private static int Main()
    {
        Console.WriteLine($"Main thread apartment: {Thread.CurrentThread.GetApartmentState()}");

        PutOnClipboard(package => package.SetText(ProbeText));
        Report("WinRT clipboard, STA main thread (diagnostic)", ReadTextDirectlyAsync(), static text => text == ProbeText);
        // Expected to fail with 0x8001010E: the reason Clipensk reads the clipboard on its own STA thread.
        Report("WinRT clipboard, thread-pool thread (diagnostic, expected to fail)", Task.Run(ReadTextDirectlyAsync), static text => text == ProbeText);

        using var host = new ResidentWindowsHost(
            new HtmlAgilityPackClipboardHtmlSearchTextConverter(),
            new ManagedClipboardRtfSearchTextConverter(),
            new BuiltInRussianLocalizationService());
        ClipboardCaptureReadExecutionPipeline pipeline =
            host.CreateCaptureReadExecutionPipeline(new AllowProbeFormatsPolicyProvider());

        bool passed = true;

        PutOnClipboard(package => package.SetText(ProbeText));
        passed &= Report(
            "Pipeline: plain text",
            ReadThroughPipeline(host, pipeline),
            static execution => execution.CapturedContent.OfType<ClipboardCapturedTextContent>()
                .Any(static text => text.Value == ProbeText));

        PutOnClipboard(package =>
        {
            package.SetText(ProbeText);
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat($"<p><b>{HtmlMarker}</b></p>"));
        });
        passed &= Report(
            "Pipeline: web fragment (text and HTML)",
            ReadThroughPipeline(host, pipeline),
            static execution =>
                execution.CapturedContent.OfType<ClipboardCapturedTextContent>().Any(static text => text.Value == ProbeText) &&
                execution.CapturedContent.OfType<ClipboardCapturedTextContent>().Any(static text => text.Value.Contains(HtmlMarker, StringComparison.Ordinal)));

        InMemoryRandomAccessStream image = CreatePng();
        PutOnClipboard(package => package.SetBitmap(RandomAccessStreamReference.CreateFromStream(image)));
        passed &= Report(
            "Pipeline: image",
            ReadThroughPipeline(host, pipeline),
            static execution => execution.CapturedContent.OfType<ClipboardCapturedPngImageContent>()
                .Any(static png => png.PngBytes.Span.StartsWith(PngSignature)));

        string directory = Path.Combine(Path.GetTempPath(), "clipensk-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "first.txt");
        string second = Path.Combine(directory, "second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        IStorageItem[] files =
        [
            Wait(StorageFile.GetFileFromPathAsync(first).AsTask()),
            Wait(StorageFile.GetFileFromPathAsync(second).AsTask()),
        ];
        PutOnClipboard(package => package.SetStorageItems(files));
        // Compared by folder and file name: the temp path may come back in its long or 8.3 form.
        string directoryName = Path.GetFileName(directory);
        passed &= Report(
            "Pipeline: file list",
            ReadThroughPipeline(host, pipeline),
            execution => execution.CapturedContent.OfType<ClipboardCapturedStorageItemsContent>()
                .Any(content =>
                    content.Items.Count == 2 &&
                    IsProbeFile(content.Items[0], directoryName, "first.txt") &&
                    IsProbeFile(content.Items[1], directoryName, "second.txt")));

        Console.WriteLine(passed ? "Clipboard probe PASS." : "Clipboard probe FAIL.");
        return passed ? 0 : 1;
    }

    /// <summary>Puts a package on the clipboard from the STA main thread and renders it.</summary>
    private static void PutOnClipboard(Action<DataPackage> fill)
    {
        var package = new DataPackage();
        fill(package);
        WindowsClipboard.SetContent(package);
        WindowsClipboard.Flush();
    }

    private static async Task<string> ReadTextDirectlyAsync()
    {
        DataPackageView content = WindowsClipboard.GetContent();
        return await content.GetTextAsync();
    }

    /// <summary>Queues a capture request and runs the pipeline on the thread pool, as the resident worker does.</summary>
    private static Task<ClipboardContentReadExecution> ReadThroughPipeline(
        ResidentWindowsHost host,
        ClipboardCaptureReadExecutionPipeline pipeline)
    {
        if (!host.CaptureQueue.TryEnqueue(new ClipboardCaptureRequest(EventTimeContext.CaptureNow())))
        {
            return Task.FromException<ClipboardContentReadExecution>(
                new InvalidOperationException("The capture request was not queued."));
        }

        return Task.Run(async () => await pipeline.ProcessNextAsync().ConfigureAwait(false));
    }

    private static bool IsProbeFile(ClipboardStorageItemMetadata item, string directoryName, string fileName) =>
        !item.IsDirectory &&
        string.Equals(item.Name, fileName, StringComparison.OrdinalIgnoreCase) &&
        item.FullPath.EndsWith(Path.Combine(directoryName, fileName), StringComparison.OrdinalIgnoreCase);

    private static InMemoryRandomAccessStream CreatePng()
    {
        var stream = new InMemoryRandomAccessStream();
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 4, 4, BitmapAlphaMode.Premultiplied);
        BitmapEncoder encoder = Wait(BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask());
        encoder.SetSoftwareBitmap(bitmap);
        Wait(encoder.FlushAsync().AsTask());
        stream.Seek(0);
        return stream;
    }

    private static T Wait<T>(Task<T> task) =>
        task.Wait(Timeout) ? task.Result : throw new TimeoutException("Probe setup did not finish.");

    private static void Wait(Task task)
    {
        if (!task.Wait(Timeout))
        {
            throw new TimeoutException("Probe setup did not finish.");
        }
    }

    private static bool Report<T>(string name, Task<T> read, Func<T, bool> check)
    {
        try
        {
            if (!read.Wait(Timeout))
            {
                Console.WriteLine($"FAIL  {name}: no result in {Timeout.TotalSeconds} s.");
                return false;
            }

            bool ok = check(read.Result);
            Console.WriteLine(ok ? $"PASS  {name}." : $"FAIL  {name}: {Describe(read.Result)}");
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

    private static string Describe(object? result) => result switch
    {
        ClipboardContentReadExecution execution =>
            $"captured [{string.Join(", ", execution.CapturedContent.Select(static content => content.SelectedFormat.FormatName))}], " +
            $"selected [{string.Join(", ", execution.Plan.Selection.Formats.Select(static format => format.FormatName))}], " +
            $"available [{string.Join(", ", execution.Plan.Selection.Snapshot.AvailableFormats)}], " +
            $"files [{string.Join(", ", execution.CapturedContent.OfType<ClipboardCapturedStorageItemsContent>().SelectMany(static content => content.Items).Select(static item => item.FullPath))}]",
        _ => $"read '{result}'",
    };

    private sealed class AllowProbeFormatsPolicyProvider : IClipboardCapturePolicyProvider
    {
        private const long Limit = 16 * 1024 * 1024;

        public ValueTask<ClipboardCapturePolicySet> GetPoliciesAsync(
            ClipboardCaptureContext captureContext,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ClipboardCapturePolicySet(new ClipboardCapturePolicy(
                ClipboardCapturePolicyRule.Allow,
                new Dictionary<string, ClipboardFormatCapturePolicy>(StringComparer.Ordinal)
                {
                    [StandardDataFormats.Text] = new(ClipboardCapturePolicyRule.Allow, Limit),
                    [StandardDataFormats.Html] = new(ClipboardCapturePolicyRule.Allow, Limit),
                    [StandardDataFormats.Bitmap] = new(ClipboardCapturePolicyRule.Allow, Limit),
                    [StandardDataFormats.StorageItems] = new(ClipboardCapturePolicyRule.Allow, Limit),
                })));
    }
}
