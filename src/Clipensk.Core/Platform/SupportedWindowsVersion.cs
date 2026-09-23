namespace Clipensk.Core.Platform;

/// <summary>
/// Clipensk supports only Windows 11 (<c>docs/OPEN_QUESTIONS.md</c> §2). An older Windows is not
/// refused: per the user's decision of 2026-09-23 Clipensk warns that it is not supported and runs on.
/// </summary>
public static class SupportedWindowsVersion
{
    /// <summary>The first Windows 11 build (21H2).</summary>
    public const int MinimumBuild = 22000;

    /// <summary>Whether <paramref name="version"/>, as <c>Environment.OSVersion.Version</c> reports Windows, is Windows 11 or later.</summary>
    public static bool IsSupported(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Major > 10 || (version.Major == 10 && version.Build >= MinimumBuild);
    }
}
