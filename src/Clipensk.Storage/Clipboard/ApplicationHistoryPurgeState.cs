using System.Text;
using System.Text.Json;
using Clipensk.Core.Clipboard;

namespace Clipensk.Storage.Clipboard;

internal enum ApplicationHistoryPurgeReason
{
    FirstAssignment,
    Merge,
}

/// <summary>
/// Durable state of one <c>ApplicationHistoryPurge</c> operation, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §5.1. The purge rule and the source scope are fixed
/// here at start, so later phases never re-derive them from state that could have moved.
/// </summary>
internal sealed record ApplicationHistoryPurgeState(
    ApplicationHistoryPurgeReason Reason,
    string RootApplicationId,
    string? ChildApplicationId,
    IReadOnlyList<string> SourceApplicationIds,
    ClipboardCapturePolicyRule EffectiveCapture,
    IReadOnlyList<string> AllowedFormats,
    string? RootPolicyFingerprint,
    string Archive,
    string Catalog,
    string Trash)
{
    public const string OperationKind = "ApplicationHistoryPurge";
    public const int Version = 1;
    public const string Pending = "pending";
    public const string Completed = "completed";

    public ClipboardHistoryPurgeRule Rule => new(EffectiveCapture, AllowedFormats);

    public bool IsFullyCompleted =>
        Archive == Completed && Catalog == Completed && Trash == Completed;
}

internal static class ApplicationHistoryPurgeStateCodec
{
    private static readonly string[] ExpectedNames =
    [
        "version",
        "reason",
        "rootApplicationId",
        "childApplicationId",
        "sourceApplicationIds",
        "effectivePolicy",
        "rootPolicyFingerprint",
        "archive",
        "catalog",
        "trash",
    ];

    public static ApplicationHistoryPurgeState CreateStarted(
        ApplicationHistoryPurgeReason reason,
        string rootApplicationId,
        string? childApplicationId,
        IEnumerable<string> sourceApplicationIds,
        ClipboardHistoryPurgeRule rule,
        string? rootPolicyFingerprint)
    {
        ArgumentNullException.ThrowIfNull(sourceApplicationIds);
        ArgumentNullException.ThrowIfNull(rule);
        var state = new ApplicationHistoryPurgeState(
            reason,
            rootApplicationId,
            childApplicationId,
            sourceApplicationIds.OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            rule.Capture,
            rule.AllowedFormats,
            rootPolicyFingerprint,
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
            writer.WriteString("reason", state.Reason.ToString());
            writer.WriteString("rootApplicationId", state.RootApplicationId);
            if (state.ChildApplicationId is null)
            {
                writer.WriteNull("childApplicationId");
            }
            else
            {
                writer.WriteString("childApplicationId", state.ChildApplicationId);
            }

            writer.WriteStartArray("sourceApplicationIds");
            foreach (string sourceApplicationId in state.SourceApplicationIds)
            {
                writer.WriteStringValue(sourceApplicationId);
            }
            writer.WriteEndArray();

            writer.WriteStartObject("effectivePolicy");
            writer.WriteString("capture", state.EffectiveCapture.ToString());
            writer.WriteStartArray("allowedFormats");
            foreach (string formatName in state.AllowedFormats)
            {
                writer.WriteStringValue(formatName);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            if (state.RootPolicyFingerprint is null)
            {
                writer.WriteNull("rootPolicyFingerprint");
            }
            else
            {
                writer.WriteString("rootPolicyFingerprint", state.RootPolicyFingerprint);
            }

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
            if (names.Count != ExpectedNames.Length || !ExpectedNames.All(names.Contains))
            {
                throw new InvalidDataException("Application history purge state has an unexpected shape.");
            }

            if (!root.GetProperty("version").TryGetInt32(out int version) ||
                version != ApplicationHistoryPurgeState.Version)
            {
                throw new InvalidDataException("Application history purge state version is unsupported.");
            }

            JsonElement effectivePolicy = root.GetProperty("effectivePolicy");
            if (effectivePolicy.ValueKind != JsonValueKind.Object ||
                effectivePolicy.EnumerateObject().Count() != 2)
            {
                throw new InvalidDataException("Application history purge effective policy has an unexpected shape.");
            }

            var state = new ApplicationHistoryPurgeState(
                ParseEnum<ApplicationHistoryPurgeReason>(ReadString(root, "reason")),
                ReadString(root, "rootApplicationId"),
                ReadNullableString(root, "childApplicationId"),
                ReadStringArray(root, "sourceApplicationIds"),
                ParseEnum<ClipboardCapturePolicyRule>(ReadString(effectivePolicy, "capture")),
                ReadStringArray(effectivePolicy, "allowedFormats"),
                ReadNullableString(root, "rootPolicyFingerprint"),
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
        if (!Enum.IsDefined(state.Reason))
        {
            throw new InvalidDataException("Application history purge reason is unsupported.");
        }

        ValidateCanonicalGuid(state.RootApplicationId);
        if (state.Reason == ApplicationHistoryPurgeReason.Merge)
        {
            ValidateCanonicalGuid(state.ChildApplicationId);
            if (string.Equals(state.ChildApplicationId, state.RootApplicationId, StringComparison.Ordinal) ||
                !state.SourceApplicationIds.Contains(state.ChildApplicationId!, StringComparer.Ordinal))
            {
                throw new InvalidDataException("A merge purge must include the child in its scope and differ from the root.");
            }
        }
        else if (state.ChildApplicationId is not null ||
                 !state.SourceApplicationIds.Contains(state.RootApplicationId, StringComparer.Ordinal))
        {
            throw new InvalidDataException("A first-assignment purge covers the root's group and has no child.");
        }

        if (state.SourceApplicationIds.Count == 0)
        {
            throw new InvalidDataException("Application history purge scope cannot be empty.");
        }
        for (int index = 0; index < state.SourceApplicationIds.Count; index++)
        {
            ValidateCanonicalGuid(state.SourceApplicationIds[index]);
            if (index > 0 &&
                string.CompareOrdinal(state.SourceApplicationIds[index - 1], state.SourceApplicationIds[index]) >= 0)
            {
                throw new InvalidDataException("Application history purge scope must be unique and ordered.");
            }
        }

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

        if (state.RootPolicyFingerprint is not null &&
            (state.RootPolicyFingerprint.Length != 64 ||
             !state.RootPolicyFingerprint.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F')))
        {
            throw new InvalidDataException("Application history purge root policy fingerprint is invalid.");
        }
        if (state.Reason == ApplicationHistoryPurgeReason.FirstAssignment && state.RootPolicyFingerprint is null)
        {
            throw new InvalidDataException("A first-assignment purge always records the assigned root policy.");
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
            throw new InvalidDataException("Application history purge state contains a non-canonical ApplicationId.");
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

    private static string? ReadNullableString(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : ReadString(element, name);
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
