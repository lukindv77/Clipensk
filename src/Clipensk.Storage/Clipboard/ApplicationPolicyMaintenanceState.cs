using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;

namespace Clipensk.Storage.Clipboard;

internal sealed record ApplicationPolicyMaintenanceState(
    string ApplicationId,
    string PolicyFingerprint,
    string CurrentPhase,
    string ArchiveExternalReferenceCleanup,
    string CatalogRebuild,
    string ExternalTrashCollection,
    string Completion,
    int StateVersion = LegacyVersion,
    string? CustomBinaryConfigurationFingerprint = null)
{
    public const int LegacyVersion = 1;
    public const int CustomBinaryConfigurationVersion = 2;
    public const string Pending = "pending";
    public const string Completed = "completed";

    public ApplicationPolicyMaintenanceState WithArchiveExternalReferenceCleanupCompleted() =>
        this with { ArchiveExternalReferenceCleanup = Completed };
}

internal static class ApplicationPolicyMaintenanceStateCodec
{
    public static ApplicationPolicyMaintenanceState CreateCurrentCompleted(
        ApplicationId applicationId,
        ClipboardCapturePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        ArgumentNullException.ThrowIfNull(policy);
        return new ApplicationPolicyMaintenanceState(
            applicationId.ToString(),
            ComputePolicyFingerprint(policy),
            ApplicationPolicyMaintenanceState.Completed,
            ApplicationPolicyMaintenanceState.Pending,
            ApplicationPolicyMaintenanceState.Pending,
            ApplicationPolicyMaintenanceState.Pending,
            ApplicationPolicyMaintenanceState.Pending);
    }

    public static ApplicationPolicyMaintenanceState CreateCurrentCompleted(
        ApplicationId applicationId,
        ClipboardCapturePolicy policy,
        string customBinaryConfigurationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(applicationId);
        ArgumentNullException.ThrowIfNull(policy);
        ValidateFingerprint(
            customBinaryConfigurationFingerprint,
            "Application policy-maintenance custom binary configuration fingerprint is invalid.");
        return new ApplicationPolicyMaintenanceState(
            applicationId.ToString(),
            ComputePolicyFingerprint(policy),
            ApplicationPolicyMaintenanceState.Completed,
            ApplicationPolicyMaintenanceState.Pending,
            ApplicationPolicyMaintenanceState.Pending,
            ApplicationPolicyMaintenanceState.Pending,
            ApplicationPolicyMaintenanceState.Pending,
            ApplicationPolicyMaintenanceState.CustomBinaryConfigurationVersion,
            customBinaryConfigurationFingerprint);
    }

    public static ApplicationPolicyMaintenanceState Parse(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            throw new InvalidDataException(
                "Application policy-maintenance state cannot be empty.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stateJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "Application policy-maintenance state must be a JSON object.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "Application policy-maintenance state contains duplicate properties.");
                }
            }

            if (!root.TryGetProperty("version", out JsonElement versionElement) ||
                !versionElement.TryGetInt32(out int version) ||
                version is not (
                    ApplicationPolicyMaintenanceState.LegacyVersion or
                    ApplicationPolicyMaintenanceState.CustomBinaryConfigurationVersion))
            {
                throw new InvalidDataException(
                    "Application policy-maintenance state version is unsupported.");
            }

            string[] expectedNames = version == ApplicationPolicyMaintenanceState.LegacyVersion
                ?
                [
                    "version",
                    "applicationId",
                    "policyFingerprint",
                    "currentPhase",
                    "archiveExternalReferenceCleanup",
                    "catalogRebuild",
                    "externalTrashCollection",
                    "completion",
                ]
                :
                [
                    "version",
                    "applicationId",
                    "policyFingerprint",
                    "customBinaryConfigurationFingerprint",
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
                    "Application policy-maintenance state has an unexpected shape.");
            }

            var state = new ApplicationPolicyMaintenanceState(
                ReadRequiredStateString(root, "applicationId"),
                ReadRequiredStateString(root, "policyFingerprint"),
                ReadRequiredStateString(root, "currentPhase"),
                ReadRequiredStateString(root, "archiveExternalReferenceCleanup"),
                ReadRequiredStateString(root, "catalogRebuild"),
                ReadRequiredStateString(root, "externalTrashCollection"),
                ReadRequiredStateString(root, "completion"),
                version,
                version == ApplicationPolicyMaintenanceState.CustomBinaryConfigurationVersion
                    ? ReadRequiredStateString(root, "customBinaryConfigurationFingerprint")
                    : null);
            Validate(state);
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Application policy-maintenance state contains invalid JSON.",
                exception);
        }
    }

    public static string Serialize(ApplicationPolicyMaintenanceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", state.StateVersion);
            writer.WriteString("applicationId", state.ApplicationId);
            writer.WriteString("policyFingerprint", state.PolicyFingerprint);
            if (state.StateVersion == ApplicationPolicyMaintenanceState.CustomBinaryConfigurationVersion)
            {
                writer.WriteString(
                    "customBinaryConfigurationFingerprint",
                    state.CustomBinaryConfigurationFingerprint);
            }
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

    public static string ComputeCustomBinaryConfigurationFingerprint(
        IReadOnlyDictionary<string, string> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach ((string formatName, string fileExtension) in
                     configuration.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("formatName", formatName);
                writer.WriteString("fileExtension", fileExtension);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    public static bool TargetsEqual(
        ApplicationPolicyMaintenanceState left,
        ApplicationPolicyMaintenanceState right) =>
        string.Equals(left.ApplicationId, right.ApplicationId, StringComparison.Ordinal) &&
        string.Equals(left.PolicyFingerprint, right.PolicyFingerprint, StringComparison.Ordinal) &&
        left.StateVersion == right.StateVersion &&
        string.Equals(
            left.CustomBinaryConfigurationFingerprint,
            right.CustomBinaryConfigurationFingerprint,
            StringComparison.Ordinal);

    private static void Validate(ApplicationPolicyMaintenanceState state)
    {
        if (!Guid.TryParseExact(state.ApplicationId, "D", out Guid applicationId) ||
            applicationId == Guid.Empty ||
            !string.Equals(
                state.ApplicationId,
                applicationId.ToString("D"),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Application policy-maintenance application id is invalid.");
        }

        ValidateFingerprint(
            state.PolicyFingerprint,
            "Application policy-maintenance policy fingerprint is invalid.");

        if (state.StateVersion == ApplicationPolicyMaintenanceState.LegacyVersion)
        {
            if (state.CustomBinaryConfigurationFingerprint is not null)
            {
                throw new InvalidDataException(
                    "Legacy application policy-maintenance state cannot contain a custom binary configuration fingerprint.");
            }
        }
        else if (state.StateVersion == ApplicationPolicyMaintenanceState.CustomBinaryConfigurationVersion)
        {
            ValidateFingerprint(
                state.CustomBinaryConfigurationFingerprint,
                "Application policy-maintenance custom binary configuration fingerprint is invalid.");
        }
        else
        {
            throw new InvalidDataException(
                "Application policy-maintenance state version is unsupported.");
        }

        if (!string.Equals(
                state.CurrentPhase,
                ApplicationPolicyMaintenanceState.Completed,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A durable application policy-maintenance marker must have a completed Current phase.");
        }

        ValidateContinuationState(
            state.ArchiveExternalReferenceCleanup,
            "archiveExternalReferenceCleanup");
        ValidateContinuationState(state.CatalogRebuild, "catalogRebuild");
        ValidateContinuationState(
            state.ExternalTrashCollection,
            "externalTrashCollection");
        ValidateContinuationState(state.Completion, "completion");

        if (state.ArchiveExternalReferenceCleanup == ApplicationPolicyMaintenanceState.Pending &&
            (state.CatalogRebuild == ApplicationPolicyMaintenanceState.Completed ||
             state.ExternalTrashCollection == ApplicationPolicyMaintenanceState.Completed ||
             state.Completion == ApplicationPolicyMaintenanceState.Completed))
        {
            throw new InvalidDataException(
                "Application policy-maintenance continuation phases are out of order.");
        }

        if (state.CatalogRebuild == ApplicationPolicyMaintenanceState.Pending &&
            (state.ExternalTrashCollection == ApplicationPolicyMaintenanceState.Completed ||
             state.Completion == ApplicationPolicyMaintenanceState.Completed))
        {
            throw new InvalidDataException(
                "Application policy-maintenance continuation phases are out of order.");
        }

        if (state.ExternalTrashCollection == ApplicationPolicyMaintenanceState.Pending &&
            state.Completion == ApplicationPolicyMaintenanceState.Completed)
        {
            throw new InvalidDataException(
                "Application policy-maintenance continuation phases are out of order.");
        }
    }

    private static void ValidateFingerprint(string? value, string errorMessage)
    {
        if (value is null ||
            value.Length != 64 ||
            !value.All(static character =>
                character is >= '0' and <= '9' or >= 'A' and <= 'F'))
        {
            throw new InvalidDataException(errorMessage);
        }
    }

    private static void ValidateContinuationState(string value, string propertyName)
    {
        if (value is not (
                ApplicationPolicyMaintenanceState.Pending or
                ApplicationPolicyMaintenanceState.Completed))
        {
            throw new InvalidDataException(
                $"Application policy-maintenance continuation state '{propertyName}' is invalid.");
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
                $"Application policy-maintenance state '{propertyName}' is invalid.");
        }

        return value.GetString()!;
    }
}
