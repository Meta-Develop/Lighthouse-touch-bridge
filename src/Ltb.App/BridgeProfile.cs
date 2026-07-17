using System.Numerics;
using System.Text;
using System.Text.Json;
using Ltb.Core;

namespace Ltb.App;

internal enum BridgeHand
{
    Left,
    Right,
}

internal enum BridgeCalibrationMode
{
    RotationOnly,
    Full6Dof,
}

/// <summary>
/// The schema-version-1 fields needed to apply one calibrated hand bridge.
/// <see cref="TrackerToController"/> is <c>T_T_C</c>: it maps controller-frame
/// coordinates into the physical tracker frame.
/// </summary>
internal sealed record BridgeProfile(
    int SchemaVersion,
    string ProfileName,
    BridgeHand Hand,
    string? ControllerSerial,
    string TrackerSerial,
    BridgeCalibrationMode SelectedMode,
    RigidTransform TrackerToController);

/// <summary>
/// Strict, read-only loader for the Milestone 2 subset of profile schema 1.
/// Unknown schema-1 properties are ignored so later profile producers can add
/// quality and provenance metadata without breaking the live bridge. Duplicate
/// known properties are rejected; repeated unknown property names are ignored.
/// Profile files must be UTF-8, with or without a UTF-8 BOM.
/// </summary>
internal static class BridgeProfileLoader
{
    internal const int SupportedSchemaVersion = 1;
    internal const int MaximumProfileBytes = 1_048_576;
    // SteamVR identities are short; this bound prevents accidental profile payloads
    // from being retained as device keys while preserving exact text within it.
    internal const int MaximumSerialUtf8Bytes = 256;
    // The rounded specification quaternion has norm about 1.0001105. Accept that
    // serialization error, but reject materially scaled quaternion inputs.
    internal const float MaximumQuaternionNormError = 0.00025f;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static BridgeProfile Load(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);

        using var source = new FileStream(
            profilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var length = source.Length;
        if (length > MaximumProfileBytes)
        {
            throw Invalid("Calibration profile exceeds the 1 MiB size limit.");
        }

        var bytes = new byte[(int)length];
        try
        {
            source.ReadExactly(bytes);
        }
        catch (EndOfStreamException exception)
        {
            throw Invalid("Calibration profile changed while it was being read.", exception);
        }

        if (source.ReadByte() != -1)
        {
            throw Invalid("Calibration profile changed or exceeds the 1 MiB size limit.");
        }

        return ParseUtf8(bytes);
    }

    public static BridgeProfile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        int utf8ByteCount;
        try
        {
            utf8ByteCount = StrictUtf8.GetByteCount(json);
        }
        catch (EncoderFallbackException exception)
        {
            throw Invalid("Calibration profile text must be representable as valid UTF-8.", exception);
        }

        if (utf8ByteCount > MaximumProfileBytes)
        {
            throw Invalid("Calibration profile exceeds the 1 MiB size limit.");
        }

        if (json.Length > 0 && json[0] == '\uFEFF')
        {
            json = json[1..];
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            return ParseRoot(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw Invalid(
                $"Calibration profile is not valid JSON near line {exception.LineNumber ?? 0}, byte {exception.BytePositionInLine ?? 0}.",
                exception);
        }
    }

    private static BridgeProfile ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Calibration profile root must be a JSON object.");
        }

        var schemaElement = RequiredProperty(root, "schema_version", "schema_version");
        if (schemaElement.ValueKind != JsonValueKind.Number ||
            !schemaElement.TryGetInt32(out var schemaVersion))
        {
            throw Invalid("'schema_version' must be an integer.");
        }

        if (schemaVersion != SupportedSchemaVersion)
        {
            throw Invalid($"Unsupported 'schema_version'; expected {SupportedSchemaVersion}.");
        }

        var profileName = RequiredString(root, "profile_name", "profile_name");
        var hand = RequiredString(root, "hand", "hand") switch
        {
            "left" => BridgeHand.Left,
            "right" => BridgeHand.Right,
            _ => throw Invalid("'hand' must be 'left' or 'right'."),
        };
        var controllerSerial = OptionalSerial(root, "controller_serial", "controller_serial");
        var trackerSerial = RequiredSerial(root, "tracker_serial", "tracker_serial");
        var selectedMode = RequiredString(root, "selected_mode", "selected_mode") switch
        {
            "rotation_only" => BridgeCalibrationMode.RotationOnly,
            "full_6dof" => BridgeCalibrationMode.Full6Dof,
            _ => throw Invalid("'selected_mode' must be 'rotation_only' or 'full_6dof'."),
        };

        var transformElement = RequiredProperty(
            root,
            "tracker_to_controller",
            "tracker_to_controller");
        if (transformElement.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("'tracker_to_controller' must be a JSON object.");
        }

        var translation = FloatArray(
            RequiredProperty(
                transformElement,
                "translation_m",
                "tracker_to_controller.translation_m"),
            "tracker_to_controller.translation_m",
            expectedLength: 3);
        var rotation = FloatArray(
            RequiredProperty(
                transformElement,
                "rotation_xyzw",
                "tracker_to_controller.rotation_xyzw"),
            "tracker_to_controller.rotation_xyzw",
            expectedLength: 4);

        var inputRotation = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
        var inputRotationNorm = inputRotation.Length();
        if (!float.IsFinite(inputRotationNorm) ||
            MathF.Abs(inputRotationNorm - 1f) > MaximumQuaternionNormError)
        {
            throw Invalid(
                "'tracker_to_controller.rotation_xyzw' must have unit length within 0.00025.");
        }

        RigidTransform trackerToController;
        try
        {
            trackerToController = new RigidTransform(
                inputRotation,
                new Vector3(translation[0], translation[1], translation[2]));
        }
        catch (ArgumentException exception)
        {
            throw Invalid(
                "'tracker_to_controller.rotation_xyzw' must be a usable non-zero quaternion.",
                exception);
        }

        if (selectedMode == BridgeCalibrationMode.RotationOnly &&
            trackerToController.TranslationMeters != Vector3.Zero)
        {
            throw Invalid(
                "'tracker_to_controller.translation_m' must be exactly [0, 0, 0] for 'rotation_only'.");
        }

        return new BridgeProfile(
            schemaVersion,
            profileName,
            hand,
            controllerSerial,
            trackerSerial,
            selectedMode,
            trackerToController);
    }

    private static string RequiredString(JsonElement parent, string propertyName, string fieldPath)
    {
        var value = RequiredProperty(parent, propertyName, fieldPath);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid($"'{fieldPath}' must be a non-empty string.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Invalid($"'{fieldPath}' must be a non-empty string.");
        }

        return text;
    }

    private static string RequiredSerial(
        JsonElement parent,
        string propertyName,
        string fieldPath) =>
        Serial(RequiredProperty(parent, propertyName, fieldPath), fieldPath, optional: false);

    private static string? OptionalSerial(
        JsonElement parent,
        string propertyName,
        string fieldPath)
    {
        if (!TryUniqueProperty(parent, propertyName, fieldPath, out var value))
        {
            return null;
        }

        return Serial(value, fieldPath, optional: true);
    }

    private static string Serial(JsonElement value, string fieldPath, bool optional)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
        {
            var requirement = optional
                ? "must be a non-empty string when present"
                : "must be a non-empty string";
            throw Invalid($"'{fieldPath}' {requirement}.");
        }

        var serial = value.GetString()!;
        if (char.IsWhiteSpace(serial[0]) || char.IsWhiteSpace(serial[^1]))
        {
            throw Invalid($"'{fieldPath}' must not contain leading or trailing whitespace.");
        }

        if (serial.Any(char.IsControl))
        {
            throw Invalid($"'{fieldPath}' must not contain control characters.");
        }

        int utf8ByteCount;
        try
        {
            utf8ByteCount = StrictUtf8.GetByteCount(serial);
        }
        catch (EncoderFallbackException exception)
        {
            throw Invalid($"'{fieldPath}' must contain valid Unicode text.", exception);
        }

        if (utf8ByteCount > MaximumSerialUtf8Bytes)
        {
            throw Invalid(
                $"'{fieldPath}' must not exceed {MaximumSerialUtf8Bytes} UTF-8 bytes.");
        }

        return serial;
    }

    /// <summary>
    /// Decodes profile bytes as UTF-8. An optional UTF-8 BOM is accepted;
    /// UTF-16 and UTF-32 BOMs are rejected rather than auto-detected.
    /// </summary>
    private static BridgeProfile ParseUtf8(ReadOnlySpan<byte> bytes)
    {
        if (HasUtf16OrUtf32Bom(bytes))
        {
            throw Invalid("Calibration profile must use UTF-8, not UTF-16 or UTF-32.");
        }

        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            bytes = bytes[3..];
        }

        try
        {
            return Parse(StrictUtf8.GetString(bytes));
        }
        catch (DecoderFallbackException exception)
        {
            throw Invalid("Calibration profile must contain valid UTF-8 text.", exception);
        }
    }

    private static bool HasUtf16OrUtf32Bom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }) ||
        bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }) ||
        bytes.StartsWith(new byte[] { 0xFF, 0xFE }) ||
        bytes.StartsWith(new byte[] { 0xFE, 0xFF });

    private static float[] FloatArray(JsonElement value, string fieldPath, int expectedLength)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != expectedLength)
        {
            throw Invalid($"'{fieldPath}' must be an array of exactly {expectedLength} numbers.");
        }

        var result = new float[expectedLength];
        var index = 0;
        foreach (var component in value.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number ||
                !component.TryGetSingle(out var number) ||
                !float.IsFinite(number))
            {
                throw Invalid(
                    $"'{fieldPath}[{index}]' must be a finite single-precision number.");
            }

            result[index++] = number;
        }

        return result;
    }

    private static JsonElement RequiredProperty(
        JsonElement parent,
        string propertyName,
        string fieldPath)
    {
        if (!TryUniqueProperty(parent, propertyName, fieldPath, out var value))
        {
            throw Invalid($"Required profile field '{fieldPath}' is missing.");
        }

        return value;
    }

    private static bool TryUniqueProperty(
        JsonElement parent,
        string propertyName,
        string fieldPath,
        out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(propertyName))
            {
                continue;
            }

            if (found)
            {
                throw Invalid($"Profile field '{fieldPath}' must not appear more than once.");
            }

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static InvalidDataException Invalid(string message, Exception? innerException = null) =>
        new(message, innerException);
}
