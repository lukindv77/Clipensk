using System.Text;
using System.Text.Json;
using Clipensk.Core.Clipboard;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// Durable state of one <c>ApplicationHistoryPurge</c> operation — moving an application into a
/// user group — per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §5.1 (state version 2). The purge
/// rule is fixed here at start, so later phases never re-derive it from state that could have moved.
/// </summary>
internal sealed record ApplicationHistoryPurgeState(
    string ApplicationId,
    string GroupId,
    bool CreatedGroup,
    ClipboardCapturePolicyRule EffectiveCapture,
    IReadOnlyList<string> AllowedFormats,
    string GroupPolicyFingerprint,
    string Archive,
    string Catalog,
    string Trash)
{
    public const string OperationKind = "ApplicationHistoryPurge";
    public const int Version = 2;
    public const string Pending = "pending";
    public const string Completed = "completed";

    public ClipboardHistoryPurgeRule Rule => new(EffectiveCapture, AllowedFormats);

    /// <summary>The purge covers only the moved application's history.</summary>
    public IReadOnlyList<string> SourceApplicationIds => [ApplicationId];

    public bool IsFullyCompleted =>
        Archive == Completed && Catalog == Completed && Trash == Completed;
}

internal static class ApplicationHistoryPurgeStateCodec
{
    private static readonly string[] ExpectedNames =
    [
        "version",
        "applicationId",
        "groupId",
        "createdGroup",
        "effectivePolicy",
        "groupPolicyFingerprint",
        "archive",
        "catalog",
        "trash",
    ];

    public static ApplicationHistoryPurgeState CreateStarted(
        string applicationId,
        string groupId,
        bool createdGroup,
        ClipboardHistoryPurgeRule rule,
        string groupPolicyFingerprint)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var state = new ApplicationHistoryPurgeState(
            applicationId,
            groupId,
            createdGroup,
            rule.Capture,
            rule.AllowedFormats,
            groupPolicyFingerprint,
            ApplicationHistoryPurgeState.Pending,
            ApplicationHistoryPurgeState.Pending,
            ApplicationHistoryPurgeState.Pending);
        Validate(state);
        return state;
    }

    public static string Serialize(ApplicationHistoryPurgeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", ApplicationHistoryPurgeState.Version);
            writer.WriteString("applicationId", state.ApplicationId);
            writer.WriteString("groupId", state.GroupId);
            writer.WriteBoolean("createdGroup", state.CreatedGroup);
            writer.WriteStartObject("effectivePolicy");
            writer.WriteString("capture", state.EffectiveCapture.ToString());
            writer.WriteStartArray("allowedFormats");
            foreach (string formatName in state.AllowedFormats)
            {
                writer.WriteStringValue(formatName);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("groupPolicyFingerprint", state.GroupPolicyFingerprint);
            writer.WriteString("archive", state.Archive);
            writer.WriteString("catalog", state.Catalog);
            writer.WriteString("trash", state.Trash);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ApplicationHistoryPurgeState Parse(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            throw new InvalidDataException("Application history purge state cannot be empty.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stateJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Application history purge state must be a JSON object.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Application history purge state contains duplicate properties.");
                }
            }
            if (!root.TryGetProperty("version", out JsonElement versionElement) ||
                !versionElement.TryGetInt32(out int version) ||
                version != ApplicationHistoryPurgeState.Version)
            {
                throw new InvalidDataException("Application history purge state version is unsupported.");
            }
            if (names.Count != ExpectedNames.Length || !ExpectedNames.All(names.Contains))
            {
                throw new InvalidDataException("Application history purge state has an unexpected shape.");
            }

            JsonElement effectivePolicy = root.GetProperty("effectivePolicy");
            if (effectivePolicy.ValueKind != JsonValueKind.Object ||
                effectivePolicy.EnumerateObject().Count() != 2)
            {
                throw new InvalidDataException("Application history purge effective policy has an unexpected shape.");
            }

            JsonElement createdGroup = root.GetProperty("createdGroup");
            if (createdGroup.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException("Application history purge state 'createdGroup' is invalid.");
            }

            var state = new ApplicationHistoryPurgeState(
                ReadString(root, "applicationId"),
                ReadString(root, "groupId"),
                createdGroup.GetBoolean(),
                ParseEnum<ClipboardCapturePolicyRule>(ReadString(effectivePolicy, "capture")),
                ReadStringArray(effectivePolicy, "allowedFormats"),
                ReadString(root, "groupPolicyFingerprint"),
                ReadString(root, "archive"),
                ReadString(root, "catalog"),
                ReadString(root, "trash"));
            Validate(state);
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Application history purge state contains invalid JSON.", exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidDataException("Application history purge state has an unexpected shape.", exception);
        }
    }

    private static void Validate(ApplicationHistoryPurgeState state)
    {
        ValidateCanonicalGuid(state.ApplicationId);
        ValidateCanonicalGuid(state.GroupId);
        if (state.EffectiveCapture is not (ClipboardCapturePolicyRule.Allow or ClipboardCapturePolicyRule.Deny))
        {
            throw new InvalidDataException("Application history purge effective capture rule must be explicit.");
        }
        for (int index = 0; index < state.AllowedFormats.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(state.AllowedFormats[index]) ||
                (index > 0 &&
                 string.CompareOrdinal(state.AllowedFormats[index - 1], state.AllowedFormats[index]) >= 0))
            {
                throw new InvalidDataException("Application history purge allowed formats must be unique and ordered.");
            }
        }

        if (state.GroupPolicyFingerprint is null ||
            state.GroupPolicyFingerprint.Length != 64 ||
            !state.GroupPolicyFingerprint.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F'))
        {
            throw new InvalidDataException("Application history purge group policy fingerprint is invalid.");
        }

        ValidatePhase(state.Archive);
        ValidatePhase(state.Catalog);
        ValidatePhase(state.Trash);
        if ((state.Archive == ApplicationHistoryPurgeState.Pending && state.Catalog == ApplicationHistoryPurgeState.Completed) ||
            (state.Catalog == ApplicationHistoryPurgeState.Pending && state.Trash == ApplicationHistoryPurgeState.Completed))
        {
            throw new InvalidDataException("Application history purge phases are out of order.");
        }
    }

    private static void ValidatePhase(string value)
    {
        if (value is not (ApplicationHistoryPurgeState.Pending or ApplicationHistoryPurgeState.Completed))
        {
            throw new InvalidDataException("Application history purge phase state is invalid.");
        }
    }

    private static void ValidateCanonicalGuid(string? value)
    {
        if (value is null ||
            !Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Application history purge state contains a non-canonical identifier.");
        }
    }

    private static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse(value, ignoreCase: false, out TEnum parsed) ||
            !Enum.IsDefined(parsed) ||
            !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Application history purge state contains an unsupported value.");
        }
        return parsed;
    }

    private static string ReadString(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Application history purge state '{name}' is invalid.");
        }
        return value.GetString()!;
    }

    private static string[] ReadStringArray(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Application history purge state '{name}' must be an array.");
        }

        var result = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException($"Application history purge state '{name}' contains an invalid item.");
            }
            result.Add(item.GetString()!);
        }
        return result.ToArray();
    }
}
