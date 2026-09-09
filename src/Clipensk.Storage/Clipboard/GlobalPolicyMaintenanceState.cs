using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clipensk.Core.Clipboard;

namespace Clipensk.Storage.Clipboard;

internal sealed record GlobalPolicyMaintenanceState(
    string PolicyFingerprint,
    string CurrentPhase,
    string ArchiveExternalReferenceCleanup,
    string CatalogRebuild,
    string ExternalTrashCollection,
    string Completion)
{
    public const int Version = 1;
    public const string Pending = "pending";
    public const string Completed = "completed";

    public GlobalPolicyMaintenanceState WithArchiveExternalReferenceCleanupCompleted() =>
        this with { ArchiveExternalReferenceCleanup = Completed };
}

internal static class GlobalPolicyMaintenanceStateCodec
{
    public static GlobalPolicyMaintenanceState Parse(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            throw new InvalidDataException(
                "Global policy-maintenance state cannot be empty.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stateJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Global policy-maintenance state must be a JSON object.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "Global policy-maintenance state contains duplicate properties.");
                }
            }

            string[] expectedNames =
            [
                "version",
                "policyFingerprint",
                "currentPhase",
                "archiveExternalReferenceCleanup",
                "catalogRebuild",
                "externalTrashCollection",
                "completion",
            ];
            if (names.Count != expectedNames.Length ||
                expectedNames.Any(name => !names.Contains(name)))
            {
                throw new InvalidDataException(
                    "Global policy-maintenance state has an unexpected shape.");
            }

            if (!root.GetProperty("version").TryGetInt32(out int version) ||
                version != GlobalPolicyMaintenanceState.Version)
            {
                throw new InvalidDataException(
                    "Global policy-maintenance state version is unsupported.");
            }

            var state = new GlobalPolicyMaintenanceState(
                ReadRequiredStateString(root, "policyFingerprint"),
                ReadRequiredStateString(root, "currentPhase"),
                ReadRequiredStateString(root, "archiveExternalReferenceCleanup"),
                ReadRequiredStateString(root, "catalogRebuild"),
                ReadRequiredStateString(root, "externalTrashCollection"),
                ReadRequiredStateString(root, "completion"));
            Validate(state);
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Global policy-maintenance state contains invalid JSON.",
                exception);
        }
    }

    public static string Serialize(GlobalPolicyMaintenanceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", GlobalPolicyMaintenanceState.Version);
            writer.WriteString("policyFingerprint", state.PolicyFingerprint);
            writer.WriteString("currentPhase", state.CurrentPhase);
            writer.WriteString(
                "archiveExternalReferenceCleanup",
                state.ArchiveExternalReferenceCleanup);
            writer.WriteString("catalogRebuild", state.CatalogRebuild);
            writer.WriteString(
                "externalTrashCollection",
                state.ExternalTrashCollection);
            writer.WriteString("completion", state.Completion);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string ComputePolicyFingerprint(ClipboardCapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("capture", policy.Capture.ToString());
            writer.WritePropertyName("formats");
            writer.WriteStartArray();
            foreach ((string formatName, ClipboardFormatCapturePolicy format) in
                     policy.Formats.OrderBy(
                         static pair => pair.Key,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", formatName);
                writer.WriteString("capture", format.Capture.ToString());
                if (format.MaxBytes.HasValue)
                {
                    writer.WriteNumber("maxBytes", format.MaxBytes.Value);
                }
                else
                {
                    writer.WriteNull("maxBytes");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void Validate(GlobalPolicyMaintenanceState state)
    {
        if (state.PolicyFingerprint.Length != 64 ||
            !state.PolicyFingerprint.All(static character =>
                character is >= '0' and <= '9' or >= 'A' and <= 'F'))
        {
            throw new InvalidDataException(
                "Global policy-maintenance policy fingerprint is invalid.");
        }

        if (!string.Equals(
                state.CurrentPhase,
                GlobalPolicyMaintenanceState.Completed,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A durable global policy-maintenance marker must have a completed Current phase.");
        }

        ValidateContinuationState(
            state.ArchiveExternalReferenceCleanup,
            "archiveExternalReferenceCleanup");
        ValidateContinuationState(state.CatalogRebuild, "catalogRebuild");
        ValidateContinuationState(
            state.ExternalTrashCollection,
            "externalTrashCollection");
        ValidateContinuationState(state.Completion, "completion");

        if (state.ArchiveExternalReferenceCleanup == GlobalPolicyMaintenanceState.Pending &&
            (state.CatalogRebuild == GlobalPolicyMaintenanceState.Completed ||
             state.ExternalTrashCollection == GlobalPolicyMaintenanceState.Completed ||
             state.Completion == GlobalPolicyMaintenanceState.Completed))
        {
            throw new InvalidDataException(
                "Global policy-maintenance continuation phases are out of order.");
        }

        if (state.CatalogRebuild == GlobalPolicyMaintenanceState.Pending &&
            (state.ExternalTrashCollection == GlobalPolicyMaintenanceState.Completed ||
             state.Completion == GlobalPolicyMaintenanceState.Completed))
        {
            throw new InvalidDataException(
                "Global policy-maintenance continuation phases are out of order.");
        }

        if (state.ExternalTrashCollection == GlobalPolicyMaintenanceState.Pending &&
            state.Completion == GlobalPolicyMaintenanceState.Completed)
        {
            throw new InvalidDataException(
                "Global policy-maintenance continuation phases are out of order.");
        }
    }

    private static void ValidateContinuationState(string value, string propertyName)
    {
        if (value is not (
                GlobalPolicyMaintenanceState.Pending or
                GlobalPolicyMaintenanceState.Completed))
        {
            throw new InvalidDataException(
                $"Global policy-maintenance continuation state '{propertyName}' is invalid.");
        }
    }

    private static string ReadRequiredStateString(
        JsonElement root,
        string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Global policy-maintenance state '{propertyName}' is invalid.");
        }

        return value.GetString()!;
    }
}
