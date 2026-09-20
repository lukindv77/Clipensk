using System.Reflection;

namespace Clipensk.App;

public sealed partial class JournalWindow
{
    private const string RepositoryUrl = "https://github.com/lukindv77/Clipensk";

    /// <summary>
    /// Static content only, per explicit product decision: project description, the GitHub link,
    /// and the current build's version — nothing else. Since no versioning scheme is defined
    /// anywhere in this project (no <c>&lt;Version&gt;</c>/<c>&lt;AssemblyVersion&gt;</c>), "build
    /// version" here means the exact git commit the running binary was built from, not an invented
    /// semantic release number; <c>Clipensk.App.csproj</c> bakes it in from <c>$(GITHUB_SHA)</c>,
    /// so it is only ever present in CI-built binaries — the only ones this app is ever distributed
    /// as.
    /// </summary>
    private void InitializeAboutPage()
    {
        AboutTitle.Text = _localization.GetString("Page.About.Title");
        AboutDescription.Text = _localization.GetString("Page.About.Description");
        AboutRepositoryLink.Content = RepositoryUrl;
        AboutRepositoryLink.NavigateUri = new Uri(RepositoryUrl);
        AboutBuildInfo.Text = string.Format(
            _localization.GetString("Page.About.Build"),
            TryGetBuildCommit() ?? _localization.GetString("Page.About.BuildUnknown"));
    }

    private static string? TryGetBuildCommit()
    {
        string? commit = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildCommit")?.Value;

        if (string.IsNullOrWhiteSpace(commit))
        {
            return null;
        }

        return commit.Length > 12 ? commit[..12] : commit;
    }
}
