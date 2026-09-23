using System.Text;

namespace Clipensk.Core.Applications;

public enum ApplicationGroupNameError
{
    Empty,
    TooLong,
    ControlCharacter,
}

/// <summary>
/// A validated application group name, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2.1: trimmed,
/// non-empty, at most <see cref="MaxLength"/> characters and free of control characters. Names are
/// unique by <see cref="Key"/>, which ignores case in every alphabet.
/// </summary>
public sealed class ApplicationGroupName : IEquatable<ApplicationGroupName>
{
    public const int MaxLength = 100;

    private ApplicationGroupName(string value)
    {
        Value = value;
        Key = value.ToUpperInvariant();
    }

    public string Value { get; }

    /// <summary>The uniqueness key: two names with the same key cannot name two groups.</summary>
    public string Key { get; }

    public static bool TryCreate(
        string? text,
        out ApplicationGroupName? name,
        out ApplicationGroupNameError error)
    {
        name = null;
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = ApplicationGroupNameError.Empty;
            return false;
        }
        if (trimmed.Length > MaxLength)
        {
            error = ApplicationGroupNameError.TooLong;
            return false;
        }
        if (trimmed.Any(char.IsControl))
        {
            error = ApplicationGroupNameError.ControlCharacter;
            return false;
        }

        error = default;
        name = new ApplicationGroupName(trimmed);
        return true;
    }

    public static ApplicationGroupName Create(string text)
    {
        if (!TryCreate(text, out ApplicationGroupName? name, out ApplicationGroupNameError error))
        {
            throw new ArgumentException($"Invalid application group name: {error}.", nameof(text));
        }
        return name!;
    }

    /// <summary>
    /// A valid name derived from an application's display name, for the one case where Clipensk
    /// names a group itself — migrating a legacy per-application policy. Control characters are
    /// dropped and the text is shortened to fit; the first free of <c>name</c>, <c>name (2)</c>,
    /// <c>name (3)</c>, … is returned.
    /// </summary>
    public static ApplicationGroupName CreateUniqueFromApplicationName(
        string applicationName,
        Func<string, bool> isKeyTaken)
    {
        ArgumentNullException.ThrowIfNull(applicationName);
        ArgumentNullException.ThrowIfNull(isKeyTaken);
        var cleaned = new StringBuilder(applicationName.Length);
        foreach (char character in applicationName)
        {
            if (!char.IsControl(character))
            {
                cleaned.Append(character);
            }
        }

        string baseName = cleaned.ToString().Trim();
        if (baseName.Length == 0)
        {
            throw new ArgumentException("An application name must contain printable text.", nameof(applicationName));
        }

        for (int attempt = 1; ; attempt++)
        {
            string suffix = attempt == 1 ? string.Empty : $" ({attempt})";
            string stem = baseName.Length + suffix.Length > MaxLength
                ? baseName[..(MaxLength - suffix.Length)].TrimEnd()
                : baseName;
            ApplicationGroupName candidate = Create(stem + suffix);
            if (!isKeyTaken(candidate.Key))
            {
                return candidate;
            }
        }
    }

    public bool Equals(ApplicationGroupName? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ApplicationGroupName);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
