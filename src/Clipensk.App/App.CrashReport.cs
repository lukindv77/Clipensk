using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Clipensk.Core.Localization;
using Clipensk.Infrastructure.Localization;
using Clipensk.Infrastructure.Settings;
using Clipensk.Windows.Interop;

namespace Clipensk.App;

/// <summary>
/// A failure nothing else catches is reported instead of ending Clipensk silently: its details go
/// to <c>%LOCALAPPDATA%\Clipensk\error.log</c> — the exception only, never clipboard content — and
/// the user is told where. The process still ends: the state after an unknown failure is not
/// trusted.
/// </summary>
public partial class App
{
    internal const string ErrorLogFileName = "error.log";

    private const int MaxLoggedCaptureFailureKinds = 20;

    private static readonly HashSet<string> LoggedCaptureFailureKinds = new(StringComparer.Ordinal);

    private static ILocalizationService? _crashLocalization;

    private void RegisterCrashReporting()
    {
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        string? logPath = TryWriteErrorLog("Application.UnhandledException", e.Exception, e.Message);
        ShowCrashMessage("Crash.Unhandled", e.Exception, logPath);
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e) =>
        TryWriteErrorLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        TryWriteErrorLog("TaskScheduler.UnobservedTaskException", e.Exception);

    /// <summary>
    /// A clipboard capture failed and its record was dropped; resident capture goes on. The first
    /// failure of each kind (exception type and HRESULT) in this run is logged — never the content —
    /// so a capture that keeps failing is visible without the log growing with every copy.
    /// </summary>
    private static void ReportCaptureFailure(Exception exception)
    {
        string kind = $"{exception.GetType().FullName}:{exception.HResult:X8}";
        lock (LoggedCaptureFailureKinds)
        {
            if (LoggedCaptureFailureKinds.Count >= MaxLoggedCaptureFailureKinds ||
                !LoggedCaptureFailureKinds.Add(kind))
            {
                return;
            }
        }

        TryWriteErrorLog("Capture", exception, "A clipboard capture failed; its record was not saved.");
    }

    /// <summary>Clipensk could not start: the reason is logged and shown, and the caller exits.</summary>
    private static void ReportStartupFailure(Exception exception)
    {
        string? logPath = TryWriteErrorLog("Startup", exception);
        ShowCrashMessage("Crash.Startup", exception, logPath);
    }

    private static string? TryWriteErrorLog(string source, Exception? exception, string? message = null)
    {
        try
        {
            string directory = Path.GetDirectoryName(SettingsPathProvider.GetInstanceLockPath())!;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, ErrorLogFileName);

            string? commit = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "BuildCommit")?.Value;
            string entry = string.Join(
                Environment.NewLine,
                $"==== {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)} {source}",
                $"Build: {commit ?? "unknown"}; Windows {Environment.OSVersion.Version}; {RuntimeInformation.FrameworkDescription}",
                message ?? string.Empty,
                exception?.ToString() ?? "(no exception object)",
                string.Empty);
            File.AppendAllText(path, entry);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void ShowCrashMessage(string key, Exception? exception, string? logPath)
    {
        try
        {
            ILocalizationService localization = _crashLocalization ?? new BuiltInRussianLocalizationService();
            string reason = exception is null
                ? string.Empty
                : $"{exception.GetType().Name}: {exception.Message}";
            string text = localization.GetString(key)
                .Replace("{0}", reason, StringComparison.Ordinal)
                .Replace("{1}", logPath ?? localization.GetString("Crash.LogUnavailable"), StringComparison.Ordinal);
            WindowsStartupMessage.ShowError(text, localization.GetString("App.Title"));
        }
        catch
        {
            // Nothing more can be done; the log, if written, still holds the details.
        }
    }
}
