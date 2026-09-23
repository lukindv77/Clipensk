using System.Globalization;
using System.Text.RegularExpressions;

namespace Clipensk.Storage.Databases;

/// <summary>
/// The name of a replaced Catalog kept in <c>Current/CatalogQuarantine</c>:
/// <c>storage-catalog-&lt;UTC time&gt;-&lt;id&gt;.db</c>. The time in the name, not the file's
/// timestamps (a replaced file keeps the original Catalog's), dates the copy for retention.
/// </summary>
public static partial class CatalogQuarantineFileName
{
    public const string DirectoryName = "CatalogQuarantine";

    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";

    public static string Create(DateTimeOffset quarantinedAtUtc, Guid id) =>
        "storage-catalog-" +
        quarantinedAtUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) +
        "-" + id.ToString("N") + ".db";

    public static bool TryParse(string fileName, out DateTimeOffset quarantinedAtUtc)
    {
        quarantinedAtUtc = default;
        Match match = NameRegex().Match(fileName);
        return match.Success &&
               DateTimeOffset.TryParseExact(
                   match.Groups["stamp"].Value,
                   TimestampFormat,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out quarantinedAtUtc);
    }

    [GeneratedRegex(@"^storage-catalog-(?<stamp>\d{8}T\d{13}Z)-[0-9a-f]{32}\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
