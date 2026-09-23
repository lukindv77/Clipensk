namespace Clipensk.Core.Applications;

/// <summary>
/// The name Clipensk shows for an application: its executable file name, else its AUMID, else its
/// <see cref="ApplicationId"/>. Never empty.
/// </summary>
public static class ApplicationDisplayName
{
    public static string From(ApplicationIdentitySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        string? executable = summary.ExecutablePaths.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(executable))
        {
            string name = FileNameOf(executable);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        string? aumid = summary.ApplicationUserModelIds.FirstOrDefault();
        return string.IsNullOrWhiteSpace(aumid) ? summary.ApplicationId.ToString() : aumid;
    }

    // Windows paths use both separators; Path.GetFileName on Linux (where tests run) knows only '/'.
    private static string FileNameOf(string path)
    {
        int separator = path.LastIndexOfAny(['\\', '/']);
        return separator < 0 ? path : path[(separator + 1)..];
    }
}
