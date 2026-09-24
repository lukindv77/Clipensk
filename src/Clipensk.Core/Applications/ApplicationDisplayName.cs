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

    /// <summary>
    /// Names for applications shown together in one list. An application whose name another one in
    /// the list shares also shows the path (or AUMID) it was recognised by, so two same-named
    /// executables — two installs, or one path observed in two spellings — can be told apart
    /// (smoke finding З6, 2026-09-24). Identities are only told apart here, never merged
    /// (<c>docs/APPLICATION_IDENTITY.md</c> §4–5).
    /// </summary>
    public static IReadOnlyDictionary<ApplicationId, string> ForList(
        IEnumerable<ApplicationIdentitySummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        ApplicationIdentitySummary[] list = summaries.ToArray();
        HashSet<string> shared = list
            .GroupBy(From, StringComparer.OrdinalIgnoreCase)
            .Where(static names => names.Count() > 1)
            .Select(static names => names.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<ApplicationId, string>(list.Length);
        foreach (ApplicationIdentitySummary summary in list)
        {
            string name = From(summary);
            result[summary.ApplicationId] = shared.Contains(name)
                ? $"{name} ({DistinguishingDetail(summary, name)})"
                : name;
        }

        return result;
    }

    private static string DistinguishingDetail(ApplicationIdentitySummary summary, string name)
    {
        string? path = summary.ExecutablePaths.FirstOrDefault(static path => !string.IsNullOrWhiteSpace(path));
        if (path is not null)
        {
            return path;
        }

        string? aumid = summary.ApplicationUserModelIds.FirstOrDefault(static aumid => !string.IsNullOrWhiteSpace(aumid));
        return aumid is not null && !string.Equals(aumid, name, StringComparison.Ordinal)
            ? aumid
            : summary.ApplicationId.ToString();
    }

    // Windows paths use both separators; Path.GetFileName on Linux (where tests run) knows only '/'.
    private static string FileNameOf(string path)
    {
        int separator = path.LastIndexOfAny(['\\', '/']);
        return separator < 0 ? path : path[(separator + 1)..];
    }
}
