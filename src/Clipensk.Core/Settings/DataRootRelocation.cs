namespace Clipensk.Core.Settings;

/// <summary>
/// Durable phase of a data root relocation, per <c>docs/DATA_ROOT_RELOCATION_PROTOCOL.md</c> §5.
/// </summary>
public enum DataRootRelocationPhase
{
    /// <summary>The data root is still the source; the target may be empty, partial or unverified.</summary>
    Copying = 0,

    /// <summary>The data root is already the verified target; the source is being removed.</summary>
    Switched = 1,
}

/// <summary>
/// The pending relocation marker. It lives beside the data root path, outside both data roots, and
/// is always written together with that path in one atomic write.
/// </summary>
public sealed record DataRootRelocationMarker(
    Guid OperationId,
    string SourcePath,
    string TargetPath,
    bool TargetExisted,
    DataRootRelocationPhase Phase,
    DateTimeOffset StartedAtUtc)
{
    /// <summary>
    /// Fails closed on a marker recovery could misread: an empty identifier, an unknown phase,
    /// relative or equal paths, or one path inside the other.
    /// </summary>
    public void Validate()
    {
        if (OperationId == Guid.Empty)
        {
            throw new InvalidDataException("A data root relocation marker needs an operation identifier.");
        }
        if (!Enum.IsDefined(Phase))
        {
            throw new InvalidDataException($"Unknown data root relocation phase '{(int)Phase}'.");
        }
        if (StartedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException("A data root relocation start time must be UTC.");
        }

        string source = RequireFullPath(SourcePath, nameof(SourcePath));
        string target = RequireFullPath(TargetPath, nameof(TargetPath));
        if (DataRootPaths.AreSame(source, target) ||
            DataRootPaths.IsNested(source, target) ||
            DataRootPaths.IsNested(target, source))
        {
            throw new InvalidDataException("Relocation source and target must be distinct, non-nested directories.");
        }
    }

    private static string RequireFullPath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            !string.Equals(DataRootPaths.Normalize(path), path, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Relocation {name} must be a normalized absolute path.");
        }

        return path;
    }
}

/// <summary>The configured data root and, while one is unfinished, the relocation marker.</summary>
public sealed record DataRootLocationState(string? DataRootPath, DataRootRelocationMarker? PendingRelocation);

/// <summary>
/// Reads and atomically writes the data root path together with the relocation marker: a reader
/// never sees one changed without the other.
/// </summary>
public interface IDataRootLocationStore
{
    Task<DataRootLocationState> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(DataRootLocationState state, CancellationToken cancellationToken = default);
}

/// <summary>
/// Data root path rules shared by relocation and its marker: full, without a trailing separator,
/// and compared without regard to case, as Windows file paths are.
/// </summary>
public static class DataRootPaths
{
    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Trims a trailing separator but never the one of a root such as "C:\".
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool AreSame(string left, string right) =>
        Comparer.Equals(Normalize(left), Normalize(right));

    /// <summary>Whether <paramref name="inner"/> lies strictly inside <paramref name="outer"/>.</summary>
    public static bool IsNested(string outer, string inner)
    {
        string outerPath = Normalize(outer);
        string innerPath = Normalize(inner);
        string prefix = Path.EndsInDirectorySeparator(outerPath)
            ? outerPath
            : outerPath + Path.DirectorySeparatorChar;
        return innerPath.Length > prefix.Length &&
            innerPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
