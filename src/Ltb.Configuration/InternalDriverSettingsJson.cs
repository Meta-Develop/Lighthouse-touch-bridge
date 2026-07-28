using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ltb.Configuration;

/// <summary>Deterministic JSON serialization for internal-driver settings.</summary>
public static class InternalDriverSettingsJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static string Serialize(InternalDriverSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var discovery = settings.OpenVrPathsDiscovery;
        return JsonSerializer.Serialize(
            new SettingsDto
            {
                SchemaVersion = settings.SchemaVersion,
                OpenVrPathsDiscovery = new DiscoveryDto
                {
                    Mode = discovery.Mode switch
                    {
                        OpenVrPathsDiscoveryMode.Automatic => "automatic",
                        OpenVrPathsDiscoveryMode.ExplicitFile => "explicit_file",
                        _ => throw new ArgumentOutOfRangeException(nameof(settings)),
                    },
                    FilePath = discovery.FilePath,
                },
                StagedDriverRoot = settings.StagedDriverRoot,
                CalibrationProfileStorePath = settings.CalibrationProfileStorePath,
                TrackerPathObservationStorePath = settings.TrackerPathObservationStorePath,
                ManualTrackerBinding = settings.ManualTrackerBinding is { } binding
                    ? new TrackerBindingDto
                    {
                        LeftTrackerSerial = binding.LeftTrackerSerial,
                        RightTrackerSerial = binding.RightTrackerSerial,
                    }
                    : null,
                UnregisterOnExit = settings.UnregisterOnExit,
            },
            SerializerOptions) + "\n";
    }

    public static InternalDriverSettings Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        EnsureNoDuplicateMembers(json);

        SettingsDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<SettingsDto>(json, SerializerOptions)
                ?? throw Format(
                    InternalDriverSettingsFormatReason.MalformedJson,
                    "Internal-driver settings JSON must be an object, not null.");
        }
        catch (JsonException exception)
        {
            throw Format(
                InternalDriverSettingsFormatReason.MalformedJson,
                "Internal-driver settings are not valid schema-versioned JSON.",
                exception);
        }

        if (dto.SchemaVersion != InternalDriverSettingsSchema.CurrentVersion)
        {
            throw Format(
                InternalDriverSettingsFormatReason.UnsupportedSchemaVersion,
                $"Unsupported 'schema_version' {dto.SchemaVersion}; expected {InternalDriverSettingsSchema.CurrentVersion}. " +
                "Settings migration must be requested explicitly.");
        }

        try
        {
            var discoveryDto = dto.OpenVrPathsDiscovery
                ?? throw Invalid("Settings must contain an 'openvrpaths_discovery' object.");
            var discovery = discoveryDto.Mode switch
            {
                "automatic" when !discoveryDto.HasFilePath => OpenVrPathsDiscovery.Automatic,
                "automatic" => throw Invalid(
                    "Automatic openvrpaths discovery must not contain 'file_path'."),
                "explicit_file" when discoveryDto.FilePath is not null =>
                    OpenVrPathsDiscovery.FromFile(discoveryDto.FilePath),
                "explicit_file" => throw Invalid(
                    "Explicit openvrpaths discovery requires a non-null 'file_path'."),
                _ => throw Invalid(
                    "'openvrpaths_discovery.mode' must be 'automatic' or 'explicit_file'."),
            };

            var manualTrackerBinding = dto.ManualTrackerBinding is { } bindingDto
                ? new InternalDriverTrackerBinding(
                    bindingDto.LeftTrackerSerial,
                    bindingDto.RightTrackerSerial)
                : null;
            var trackerPathObservationStorePath =
                dto.HasTrackerPathObservationStorePath
                    ? dto.TrackerPathObservationStorePath
                        ?? throw Invalid(
                            "'tracker_path_observation_store_path' must not be null.")
                    : null;
            return new InternalDriverSettings(
                dto.SchemaVersion,
                discovery,
                dto.StagedDriverRoot,
                dto.CalibrationProfileStorePath,
                manualTrackerBinding,
                dto.HasUnregisterOnExit
                    ? dto.UnregisterOnExit
                    : true,
                trackerPathObservationStorePath);
        }
        catch (InternalDriverSettingsFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw Invalid("Internal-driver settings contain invalid data.", exception);
        }
    }

    private static InternalDriverSettingsFormatException Invalid(
        string message,
        Exception? innerException = null) =>
        Format(InternalDriverSettingsFormatReason.InvalidSettingsData, message, innerException);

    private static InternalDriverSettingsFormatException Format(
        InternalDriverSettingsFormatReason reason,
        string message,
        Exception? innerException = null) =>
        new(reason, message, innerException);

    private static void EnsureNoDuplicateMembers(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            ValidateElement(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw Format(
                InternalDriverSettingsFormatReason.MalformedJson,
                "Internal-driver settings are not valid strict JSON.",
                exception);
        }

        static void ValidateElement(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw Format(
                            InternalDriverSettingsFormatReason.MalformedJson,
                            "Internal-driver settings contain a duplicate JSON member.");
                    }

                    ValidateElement(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    ValidateElement(item);
                }
            }
        }
    }

    private sealed class SettingsDto
    {
        private bool unregisterOnExit;
        private bool hasUnregisterOnExit;
        private string? trackerPathObservationStorePath;
        private bool hasTrackerPathObservationStorePath;

        [JsonPropertyName("schema_version")]
        [JsonPropertyOrder(0)]
        public required int SchemaVersion { get; init; }

        [JsonPropertyName("openvrpaths_discovery")]
        [JsonPropertyOrder(1)]
        public required DiscoveryDto OpenVrPathsDiscovery { get; init; }

        [JsonPropertyName("staged_driver_root")]
        [JsonPropertyOrder(2)]
        public required string StagedDriverRoot { get; init; }

        [JsonPropertyName("calibration_profile_store_path")]
        [JsonPropertyOrder(3)]
        public required string CalibrationProfileStorePath { get; init; }

        [JsonPropertyName("manual_tracker_binding")]
        [JsonPropertyOrder(5)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TrackerBindingDto? ManualTrackerBinding { get; init; }

        [JsonPropertyName("tracker_path_observation_store_path")]
        [JsonPropertyOrder(4)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TrackerPathObservationStorePath
        {
            get => trackerPathObservationStorePath;
            init
            {
                trackerPathObservationStorePath = value;
                hasTrackerPathObservationStorePath = true;
            }
        }

        [JsonIgnore]
        public bool HasTrackerPathObservationStorePath =>
            hasTrackerPathObservationStorePath;

        [JsonPropertyName("unregister_on_exit")]
        [JsonPropertyOrder(6)]
        public bool UnregisterOnExit
        {
            get => unregisterOnExit;
            init
            {
                unregisterOnExit = value;
                hasUnregisterOnExit = true;
            }
        }

        [JsonIgnore]
        public bool HasUnregisterOnExit => hasUnregisterOnExit;
    }

    private sealed class DiscoveryDto
    {
        private string? filePath;
        private bool hasFilePath;

        [JsonPropertyName("mode")]
        [JsonPropertyOrder(0)]
        public required string Mode { get; init; }

        [JsonPropertyName("file_path")]
        [JsonPropertyOrder(1)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FilePath
        {
            get => filePath;
            init
            {
                filePath = value;
                hasFilePath = true;
            }
        }

        [JsonIgnore]
        public bool HasFilePath => hasFilePath;
    }

    private sealed class TrackerBindingDto
    {
        [JsonPropertyName("left_tracker_serial")]
        [JsonPropertyOrder(0)]
        public required string LeftTrackerSerial { get; init; }

        [JsonPropertyName("right_tracker_serial")]
        [JsonPropertyOrder(1)]
        public required string RightTrackerSerial { get; init; }
    }
}
