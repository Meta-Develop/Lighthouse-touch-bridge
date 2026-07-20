using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ltb.Configuration;

/// <summary>Deterministic UTF-8 JSON serialization for schema-versioned profiles and stores.</summary>
public static class CalibrationProfileJson
{
    private static readonly string[] CreatedUtcFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static string SerializeProfile(CalibrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Serialize(ProfileDto.FromProfile(profile));
    }

    public static CalibrationProfile DeserializeProfile(string json)
    {
        var dto = Deserialize<ProfileDto>(json, "Calibration profile");
        return ConvertProfile(dto);
    }

    /// <summary>
    /// Serializes a store as <c>{ "profiles": [...] }</c>. Every element in
    /// <c>profiles</c> retains the exact per-profile shape from specification section 19.
    /// </summary>
    public static string SerializeStore(CalibrationProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Serialize(new StoreDto
        {
            Profiles = store.Profiles
                .Select(profile => (ProfileDto?)ProfileDto.FromProfile(profile))
                .ToList(),
        });
    }

    public static CalibrationProfileStore DeserializeStore(string json)
    {
        var dto = Deserialize<StoreDto>(json, "Calibration profile store");
        if (dto.Profiles is null)
        {
            throw Invalid("Calibration profile store must contain a non-null 'profiles' array.");
        }

        var profiles = new List<CalibrationProfile>(dto.Profiles.Count);
        for (var index = 0; index < dto.Profiles.Count; index++)
        {
            var entry = dto.Profiles[index];
            if (entry is null)
            {
                throw Invalid($"'profiles[{index}]' must be a calibration profile object, not null.");
            }

            try
            {
                profiles.Add(ConvertProfile(entry));
            }
            catch (CalibrationProfileFormatException exception)
            {
                throw Format(
                    exception.Reason,
                    $"'profiles[{index}]' is invalid: {exception.Message}",
                    exception);
            }
        }

        try
        {
            return new CalibrationProfileStore(profiles);
        }
        catch (CalibrationProfileFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw Invalid("Calibration profile store contains invalid profile data.", exception);
        }
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializerOptions) + "\n";

    private static T Deserialize<T>(string json, string subject)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw Format(
                    CalibrationProfileFormatReason.MalformedJson,
                    $"{subject} JSON must be an object, not null.");
        }
        catch (JsonException exception)
        {
            throw Format(
                CalibrationProfileFormatReason.MalformedJson,
                $"{subject} is not valid schema-versioned JSON.",
                exception);
        }
    }

    private static CalibrationProfile ConvertProfile(ProfileDto dto)
    {
        try
        {
            if (dto.SchemaVersion is not CalibrationProfileSchema.LegacyVersion and
                not CalibrationProfileSchema.CurrentVersion)
            {
                throw Format(
                    CalibrationProfileFormatReason.UnsupportedSchemaVersion,
                    $"Unsupported 'schema_version' {dto.SchemaVersion}; expected " +
                    $"{CalibrationProfileSchema.LegacyVersion} or {CalibrationProfileSchema.CurrentVersion}.");
            }

            ValidateIdentityShape(dto);

            var translation = RequireArray(dto.TrackerToController?.TranslationMeters, 3, "tracker_to_controller.translation_m");
            var rotation = RequireArray(dto.TrackerToController?.RotationXyzw, 4, "tracker_to_controller.rotation_xyzw");
            var quality = dto.Quality ?? throw Invalid("Profile must contain a 'quality' object.");

            if (!DateTimeOffset.TryParseExact(
                    dto.CreatedUtc,
                    CreatedUtcFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var createdUtc))
            {
                throw Invalid(
                    "'created_utc' must be an ISO 8601 UTC timestamp ending in 'Z', with optional fractional seconds.");
            }

            var transform = new TrackerToControllerTransform(
                new Vector3((float)translation[0], (float)translation[1], (float)translation[2]),
                new Quaternion((float)rotation[0], (float)rotation[1], (float)rotation[2], (float)rotation[3]));
            var profileQuality = new CalibrationProfileQuality(
                quality.RotationRmsDegrees,
                quality.PositionRmsMillimeters,
                quality.TranslationCondition,
                quality.InlierRatio);

            return dto.SchemaVersion == CalibrationProfileSchema.LegacyVersion
                ? new CalibrationProfile(
                    dto.SchemaVersion,
                    dto.ProfileName,
                    ParseHand(dto.Hand),
                    dto.ControllerRuntime,
                    dto.ControllerModel,
                    dto.ControllerSerial,
                    dto.TrackerSerial,
                    ParsePolicy(dto.CalibrationPolicy),
                    ParseMode(dto.SelectedMode),
                    dto.SelectionReason,
                    transform,
                    dto.EstimatedLagMilliseconds,
                    profileQuality,
                    createdUtc)
                : new CalibrationProfile(
                    dto.SchemaVersion,
                    dto.ProfileName,
                    ParseHand(dto.Hand),
                    dto.ControllerRuntime,
                    dto.ControllerModel,
                    dto.ControllerIdentity,
                    dto.TrackerSerial,
                    dto.DriverProfile!,
                    ParsePolicy(dto.CalibrationPolicy),
                    ParseMode(dto.SelectedMode),
                    dto.SelectionReason,
                    transform,
                    dto.EstimatedLagMilliseconds,
                    profileQuality,
                    createdUtc);
        }
        catch (CalibrationProfileFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw Invalid($"Calibration profile contains invalid schema-version-{dto.SchemaVersion} data.", exception);
        }
    }

    private static void ValidateIdentityShape(ProfileDto dto)
    {
        if (dto.SchemaVersion == CalibrationProfileSchema.LegacyVersion)
        {
            if (dto.HasControllerIdentity || dto.HasDriverProfile)
            {
                throw Invalid(
                    "Schema-version-1 profiles must not contain 'controller_identity' or 'driver_profile'.");
            }

            return;
        }

        if (dto.HasControllerSerial)
        {
            throw Invalid("Schema-version-2 profiles must not contain legacy 'controller_serial'.");
        }

        if (!dto.HasDriverProfile || dto.DriverProfile is null)
        {
            throw Invalid("Schema-version-2 profiles require a non-null 'driver_profile'.");
        }

        // controller_identity is optional for schema 2 because the public
        // LibOVR ABI does not expose a stable per-controller identity. When it
        // is present, CalibrationProfile validates and matches it exactly.
    }

    private static double[] RequireArray(double[]? values, int length, string field)
    {
        if (values is null || values.Length != length)
        {
            throw Invalid($"'{field}' must contain exactly {length} numeric components.");
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (!double.IsFinite(values[index]) || values[index] < -float.MaxValue || values[index] > float.MaxValue)
            {
                throw Invalid($"'{field}[{index}]' must be finite and representable as a 32-bit float.");
            }
        }

        return values;
    }

    private static ControllerHand ParseHand(string value) => value switch
    {
        "left" => ControllerHand.Left,
        "right" => ControllerHand.Right,
        _ => throw Invalid("'hand' must be 'left' or 'right'."),
    };

    private static ProfileCalibrationPolicy ParsePolicy(string value) => value switch
    {
        "rotation_only" => ProfileCalibrationPolicy.RotationOnly,
        "full_6dof" => ProfileCalibrationPolicy.FullSixDof,
        "auto" => ProfileCalibrationPolicy.Auto,
        _ => throw Invalid("'calibration_policy' must be 'rotation_only', 'full_6dof', or 'auto'."),
    };

    private static ProfileCalibrationMode ParseMode(string value) => value switch
    {
        "rotation_only" => ProfileCalibrationMode.RotationOnly,
        "full_6dof" => ProfileCalibrationMode.FullSixDof,
        _ => throw Invalid("'selected_mode' must be 'rotation_only' or 'full_6dof'."),
    };

    private static string FormatHand(ControllerHand value) => value switch
    {
        ControllerHand.Left => "left",
        ControllerHand.Right => "right",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string FormatPolicy(ProfileCalibrationPolicy value) => value switch
    {
        ProfileCalibrationPolicy.RotationOnly => "rotation_only",
        ProfileCalibrationPolicy.FullSixDof => "full_6dof",
        ProfileCalibrationPolicy.Auto => "auto",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string FormatMode(ProfileCalibrationMode value) => value switch
    {
        ProfileCalibrationMode.RotationOnly => "rotation_only",
        ProfileCalibrationMode.FullSixDof => "full_6dof",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static CalibrationProfileFormatException Invalid(
        string message,
        Exception? innerException = null) =>
        Format(CalibrationProfileFormatReason.InvalidProfileData, message, innerException);

    private static CalibrationProfileFormatException Format(
        CalibrationProfileFormatReason reason,
        string message,
        Exception? innerException = null) =>
        new(reason, message, innerException);

    private sealed class StoreDto
    {
        [JsonPropertyName("profiles")]
        [JsonPropertyOrder(0)]
        public required List<ProfileDto?> Profiles { get; init; }
    }

    private sealed class ProfileDto
    {
        private string? controllerSerial;
        private string? controllerIdentity;
        private string? driverProfile;
        private bool hasControllerSerial;
        private bool hasControllerIdentity;
        private bool hasDriverProfile;

        [JsonPropertyName("schema_version")]
        [JsonPropertyOrder(0)]
        public required int SchemaVersion { get; init; }

        [JsonPropertyName("profile_name")]
        [JsonPropertyOrder(1)]
        public required string ProfileName { get; init; }

        [JsonPropertyName("hand")]
        [JsonPropertyOrder(2)]
        public required string Hand { get; init; }

        [JsonPropertyName("controller_runtime")]
        [JsonPropertyOrder(3)]
        public required string ControllerRuntime { get; init; }

        [JsonPropertyName("controller_model")]
        [JsonPropertyOrder(4)]
        public required string ControllerModel { get; init; }

        [JsonPropertyName("controller_serial")]
        [JsonPropertyOrder(5)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ControllerSerial
        {
            get => controllerSerial;
            init
            {
                controllerSerial = value;
                hasControllerSerial = true;
            }
        }

        [JsonIgnore]
        public bool HasControllerSerial => hasControllerSerial;

        [JsonPropertyName("controller_identity")]
        [JsonPropertyOrder(5)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ControllerIdentity
        {
            get => controllerIdentity;
            init
            {
                controllerIdentity = value;
                hasControllerIdentity = true;
            }
        }

        [JsonIgnore]
        public bool HasControllerIdentity => hasControllerIdentity;

        [JsonPropertyName("tracker_serial")]
        [JsonPropertyOrder(6)]
        public required string TrackerSerial { get; init; }

        [JsonPropertyName("driver_profile")]
        [JsonPropertyOrder(7)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DriverProfile
        {
            get => driverProfile;
            init
            {
                driverProfile = value;
                hasDriverProfile = true;
            }
        }

        [JsonIgnore]
        public bool HasDriverProfile => hasDriverProfile;

        [JsonPropertyName("calibration_policy")]
        [JsonPropertyOrder(8)]
        public required string CalibrationPolicy { get; init; }

        [JsonPropertyName("selected_mode")]
        [JsonPropertyOrder(9)]
        public required string SelectedMode { get; init; }

        [JsonPropertyName("selection_reason")]
        [JsonPropertyOrder(10)]
        public required string SelectionReason { get; init; }

        [JsonPropertyName("tracker_to_controller")]
        [JsonPropertyOrder(11)]
        public required TransformDto TrackerToController { get; init; }

        [JsonPropertyName("estimated_lag_ms")]
        [JsonPropertyOrder(12)]
        public required double EstimatedLagMilliseconds { get; init; }

        [JsonPropertyName("quality")]
        [JsonPropertyOrder(13)]
        public required QualityDto Quality { get; init; }

        [JsonPropertyName("created_utc")]
        [JsonPropertyOrder(14)]
        public required string CreatedUtc { get; init; }

        public static ProfileDto FromProfile(CalibrationProfile profile)
        {
            var translation = profile.TrackerToController.TranslationMeters;
            var rotation = profile.TrackerToController.RotationXyzw;

            return new ProfileDto
            {
                SchemaVersion = profile.SchemaVersion,
                ProfileName = profile.ProfileName,
                Hand = FormatHand(profile.Hand),
                ControllerRuntime = profile.ControllerRuntime,
                ControllerModel = profile.ControllerModel,
                ControllerSerial = profile.IsLegacy ? profile.ControllerIdentity : null,
                ControllerIdentity = profile.IsLegacy ? null : profile.ControllerIdentity,
                TrackerSerial = profile.TrackerSerial,
                DriverProfile = profile.DriverProfile,
                CalibrationPolicy = FormatPolicy(profile.CalibrationPolicy),
                SelectedMode = FormatMode(profile.SelectedMode),
                SelectionReason = profile.SelectionReason,
                TrackerToController = new TransformDto
                {
                    TranslationMeters = [translation.X, translation.Y, translation.Z],
                    RotationXyzw = [rotation.X, rotation.Y, rotation.Z, rotation.W],
                },
                EstimatedLagMilliseconds = profile.EstimatedLagMilliseconds,
                Quality = new QualityDto
                {
                    RotationRmsDegrees = profile.Quality.RotationRmsDegrees,
                    PositionRmsMillimeters = profile.Quality.PositionRmsMillimeters,
                    TranslationCondition = profile.Quality.TranslationCondition,
                    InlierRatio = profile.Quality.InlierRatio,
                },
                CreatedUtc = profile.CreatedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            };
        }
    }

    private sealed class TransformDto
    {
        [JsonPropertyName("translation_m")]
        [JsonPropertyOrder(0)]
        public required double[] TranslationMeters { get; init; }

        [JsonPropertyName("rotation_xyzw")]
        [JsonPropertyOrder(1)]
        public required double[] RotationXyzw { get; init; }
    }

    private sealed class QualityDto
    {
        [JsonPropertyName("rotation_rms_deg")]
        [JsonPropertyOrder(0)]
        public required double RotationRmsDegrees { get; init; }

        [JsonPropertyName("position_rms_mm")]
        [JsonPropertyOrder(1)]
        public required double? PositionRmsMillimeters { get; init; }

        [JsonPropertyName("translation_condition")]
        [JsonPropertyOrder(2)]
        public required double? TranslationCondition { get; init; }

        [JsonPropertyName("inlier_ratio")]
        [JsonPropertyOrder(3)]
        public required double InlierRatio { get; init; }
    }
}
