using System.Text.RegularExpressions;

namespace Clipensk.Core.Storage;

public readonly partial record struct ArchiveFileName(int BaseNumber, int SplitSequence)
{
    public const int NoSplit = 0;

    public string FileName => SplitSequence == NoSplit
        ? $"archive_{BaseNumber:000000}.db"
        : $"archive_{BaseNumber:000000}_{SplitSequence:0000}.db";

    public override string ToString() => FileName;

    public ArchiveFileName NextSplit(int nextSplitSequence)
    {
        if (nextSplitSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextSplitSequence));
        }

        return new ArchiveFileName(BaseNumber, nextSplitSequence);
    }

    public static bool TryParse(string? fileName, out ArchiveFileName result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        Match match = ArchiveFileNameRegex().Match(Path.GetFileName(fileName));
        if (!match.Success
            || !int.TryParse(match.Groups["base"].Value, out int baseNumber)
            || baseNumber <= 0)
        {
            return false;
        }

        int splitSequence = NoSplit;
        Group splitGroup = match.Groups["split"];
        if (splitGroup.Success
            && (!int.TryParse(splitGroup.Value, out splitSequence) || splitSequence <= 0))
        {
            return false;
        }

        result = new ArchiveFileName(baseNumber, splitSequence);
        return true;
    }

    [GeneratedRegex(@"^archive_(?<base>\d{6})(?:_(?<split>\d{4}))?\.db$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveFileNameRegex();
}
