using System.Numerics;
using System.Text;
using Ltb.App;

namespace Ltb.Integration.Tests;

public sealed class BridgeProfileTests
{
    [Fact]
    public void ParsesSpecificationExampleAndAcceptsUnrelatedSchemaOneProperties()
    {
        const string json = """
            {
              "schema_version": 1,
              "profile_name": "Synthetic mount A",
              "hand": "left",
              "controller_runtime": "ALVR",
              "controller_model": "Synthetic Touch",
              "controller_serial": "CTRL-TEST0001",
              "tracker_serial": "LHR-TEST0001",
              "calibration_policy": "auto",
              "selected_mode": "full_6dof",
              "tracker_to_controller": {
                "translation_m": [0.014, -0.052, 0.031],
                "rotation_xyzw": [0.012, -0.704, 0.019, 0.710],
                "future_transform_metadata": true
              },
              "estimated_lag_ms": 11.5,
              "quality": {
                "rotation_rms_deg": 1.2,
                "position_rms_mm": 8.4,
                "translation_condition": 14.7,
                "inlier_ratio": 0.94
              },
              "created_utc": "2026-07-17T00:00:00Z",
              "future_root_metadata": { "accepted": true }
            }
            """;

        var profile = BridgeProfileLoader.Parse(json);

        Assert.Equal(1, profile.SchemaVersion);
        Assert.Equal("Synthetic mount A", profile.ProfileName);
        Assert.Equal(BridgeHand.Left, profile.Hand);
        Assert.Equal("CTRL-TEST0001", profile.ControllerSerial);
        Assert.Equal("LHR-TEST0001", profile.TrackerSerial);
        Assert.Equal(BridgeCalibrationMode.Full6Dof, profile.SelectedMode);
        Assert.Equal(new Vector3(0.014f, -0.052f, 0.031f), profile.TrackerToController.TranslationMeters);

        var expectedRotation = Quaternion.Normalize(new Quaternion(0.012f, -0.704f, 0.019f, 0.710f));
        Assert.Equal(expectedRotation, profile.TrackerToController.Rotation);
        Assert.True(profile.TrackerToController.IsValid);
    }

    [Fact]
    public void LoadsRotationOnlyProfileWithoutChangingItsFile()
    {
        var json = ProfileJson(
            handJson: "\"right\"",
            controllerSerialJson: null,
            selectedModeJson: "\"rotation_only\"",
            translationJson: "[0, 0, 0]",
            rotationJson: "[0, 0, 0, 1.0002]");
        var profilePath = Path.Combine(
            Path.GetTempPath(),
            $"ltb-bridge-profile-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(profilePath, json);
            var originalBytes = File.ReadAllBytes(profilePath);

            var profile = BridgeProfileLoader.Load(profilePath);

            Assert.Equal(BridgeHand.Right, profile.Hand);
            Assert.Equal(BridgeCalibrationMode.RotationOnly, profile.SelectedMode);
            Assert.Null(profile.ControllerSerial);
            Assert.Equal(Vector3.Zero, profile.TrackerToController.TranslationMeters);
            Assert.Equal(Quaternion.Identity, profile.TrackerToController.Rotation);
            Assert.Equal(originalBytes, File.ReadAllBytes(profilePath));
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("1.0")]
    public void RejectsUnsupportedOrNonIntegerSchemaVersions(string schemaVersionJson)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(schemaVersionJson: schemaVersionJson)));

        Assert.Contains("schema_version", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"auto\"")]
    [InlineData("\"FULL_6DOF\"")]
    [InlineData("null")]
    public void RejectsUnsupportedModes(string selectedModeJson)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(selectedModeJson: selectedModeJson)));

        Assert.Contains("selected_mode", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"center\"")]
    [InlineData("\"Left\"")]
    [InlineData("null")]
    public void RejectsUnsupportedHands(string handJson)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(handJson: handJson)));

        Assert.Contains("hand", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[0, 0]", "[0, 0, 0, 1]", "tracker_to_controller.translation_m")]
    [InlineData("[0, 0, 0, 0]", "[0, 0, 0, 1]", "tracker_to_controller.translation_m")]
    [InlineData("[0, 0, 0]", "[0, 0, 1]", "tracker_to_controller.rotation_xyzw")]
    [InlineData("[0, 0, 0]", "[0, false, 0, 1]", "tracker_to_controller.rotation_xyzw")]
    public void RejectsMalformedTransformArrays(
        string translationJson,
        string rotationJson,
        string expectedField)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(
                translationJson: translationJson,
                rotationJson: rotationJson)));

        Assert.Contains(expectedField, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1e400, 0, 0]", "[0, 0, 0, 1]", "translation_m[0]")]
    [InlineData("[0, 0, 0]", "[0, -1e400, 0, 1]", "rotation_xyzw[1]")]
    public void RejectsNonFiniteOrOutOfRangeTransformComponents(
        string translationJson,
        string rotationJson,
        string expectedField)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(
                translationJson: translationJson,
                rotationJson: rotationJson)));

        Assert.Contains(expectedField, exception.Message, StringComparison.Ordinal);
        Assert.Contains("finite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsQuaternionWithoutUsableMagnitude()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(rotationJson: "[0, 0, 0, 0]")));

        Assert.Contains("unit length", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[0, 0, 0, 1.001]")]
    [InlineData("[0, 0, 0, 2]")]
    public void RejectsMateriallyNonUnitQuaternionInput(string rotationJson)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(rotationJson: rotationJson)));

        Assert.Contains("unit length within 0.00025", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonZeroRotationOnlyTranslation()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(
                selectedModeJson: "\"rotation_only\"",
                translationJson: "[0, 0.001, 0]")));

        Assert.Contains("exactly [0, 0, 0]", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "profile_name")]
    [InlineData("\"   \"", "profile_name")]
    public void RejectsMissingOrBlankRequiredStrings(string? profileNameJson, string expectedField)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(profileNameJson: profileNameJson)));

        Assert.Contains(expectedField, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsBlankOptionalControllerSerialWhenPresent()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(controllerSerialJson: "\" \"")));

        Assert.Contains("controller_serial", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesTranslationXyzwOrderAndQuaternionSign()
    {
        var positive = BridgeProfileLoader.Parse(ProfileJson(
            translationJson: "[0.125, -0.25, 0.5]",
            rotationJson: "[0.5, -0.5, 0.5, -0.5]"));
        var negative = BridgeProfileLoader.Parse(ProfileJson(
            translationJson: "[0.125, -0.25, 0.5]",
            rotationJson: "[-0.5, 0.5, -0.5, 0.5]"));

        Assert.Equal(new Vector3(0.125f, -0.25f, 0.5f), positive.TrackerToController.TranslationMeters);
        Assert.Equal(new Quaternion(0.5f, -0.5f, 0.5f, -0.5f), positive.TrackerToController.Rotation);
        Assert.Equal(new Quaternion(-0.5f, 0.5f, -0.5f, 0.5f), negative.TrackerToController.Rotation);
    }

    [Fact]
    public void AcceptsUtf8BomAndPreservesExactValidSerialIdentity()
    {
        const string trackerSerial = "LHR-Test_ß-001";
        const string controllerSerial = "CTRL-Test_界-001";
        var json = ProfileJson(
            controllerSerialJson: $"\"{controllerSerial}\"",
            trackerSerialJson: $"\"{trackerSerial}\"");
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(json))
            .ToArray();

        var profile = LoadProfile(bytes);

        Assert.Equal(trackerSerial, profile.TrackerSerial);
        Assert.Equal(controllerSerial, profile.ControllerSerial);
    }

    [Fact]
    public void RejectsInvalidUtf8Bytes()
    {
        var bytes = Encoding.ASCII.GetBytes("{\"schema_version\":")
            .Append((byte)0xFF)
            .ToArray();

        var exception = LoadFailure(bytes);

        Assert.Contains("valid UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUtf16AndUtf32BomEncodedProfiles()
    {
        var json = ProfileJson();
        Encoding[] encodings =
        [
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            new UTF32Encoding(bigEndian: false, byteOrderMark: true),
            new UTF32Encoding(bigEndian: true, byteOrderMark: true),
        ];

        foreach (var encoding in encodings)
        {
            var bytes = encoding.GetPreamble()
                .Concat(encoding.GetBytes(json))
                .ToArray();

            var exception = LoadFailure(bytes);

            Assert.Contains("must use UTF-8", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ParseLimitCountsUtf8BytesRatherThanUtf16Characters()
    {
        var multibyteText = new string('界', (BridgeProfileLoader.MaximumProfileBytes / 3) + 1);
        Assert.True(multibyteText.Length < BridgeProfileLoader.MaximumProfileBytes);
        Assert.True(Encoding.UTF8.GetByteCount(multibyteText) > BridgeProfileLoader.MaximumProfileBytes);

        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(multibyteText));

        Assert.Contains("1 MiB size limit", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\" LHR-TEST0001\"", "tracker_serial")]
    [InlineData("\"LHR-TEST0001 \"", "tracker_serial")]
    [InlineData("\"LHR-TEST\\u0000-001\"", "tracker_serial")]
    [InlineData("\"CTRL-TEST0001\\u0001\"", "controller_serial")]
    public void RejectsWhitespaceOrControlCharactersAtSerialBoundaries(
        string serialJson,
        string field)
    {
        var json = field == "tracker_serial"
            ? ProfileJson(trackerSerialJson: serialJson)
            : ProfileJson(controllerSerialJson: serialJson);

        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(json));

        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsOverlongTrackerAndControllerSerials(bool controller)
    {
        var serialJson = $"\"{new string('S', BridgeProfileLoader.MaximumSerialUtf8Bytes + 1)}\"";
        var json = controller
            ? ProfileJson(controllerSerialJson: serialJson)
            : ProfileJson(trackerSerialJson: serialJson);

        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(json));

        Assert.Contains("256 UTF-8 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateKnownRootAndNestedProperties()
    {
        var duplicateRoot = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(
                extraRootProperty: "\"tracker_serial\":\"LHR-TEST0002\"")));
        var duplicateNested = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(
                extraTransformProperty: "\"translation_m\":[0,0,0]")));

        Assert.Contains("tracker_serial", duplicateRoot.Message, StringComparison.Ordinal);
        Assert.Contains("must not appear more than once", duplicateRoot.Message, StringComparison.Ordinal);
        Assert.Contains("tracker_to_controller.translation_m", duplicateNested.Message, StringComparison.Ordinal);
        Assert.Contains("must not appear more than once", duplicateNested.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("123")]
    public void RejectsMissingOrWrongTypeTrackerSerial(string? trackerSerialJson)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(trackerSerialJson: trackerSerialJson)));

        Assert.Contains("tracker_serial", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "[0,0,0,1]", "translation_m")]
    [InlineData("{}", "[0,0,0,1]", "translation_m")]
    [InlineData("[0,0,0]", null, "rotation_xyzw")]
    [InlineData("[0,0,0]", "{}", "rotation_xyzw")]
    public void RejectsMissingOrWrongTypeTransformMembers(
        string? translationJson,
        string? rotationJson,
        string expectedField)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(
                translationJson: translationJson,
                rotationJson: rotationJson)));

        Assert.Contains(expectedField, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "42")]
    public void RejectsMissingOrWrongTypeTransformObject(
        bool includeTransform,
        string? transformOverrideJson)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            BridgeProfileLoader.Parse(ProfileJson(
                includeTransform: includeTransform,
                transformOverrideJson: transformOverrideJson)));

        Assert.Contains("tracker_to_controller", exception.Message, StringComparison.Ordinal);
    }

    private static string ProfileJson(
        string schemaVersionJson = "1",
        string? profileNameJson = "\"Synthetic profile\"",
        string handJson = "\"left\"",
        string? controllerSerialJson = "\"CTRL-TEST0001\"",
        string? trackerSerialJson = "\"LHR-TEST0001\"",
        string selectedModeJson = "\"full_6dof\"",
        string? translationJson = "[0, 0, 0]",
        string? rotationJson = "[0, 0, 0, 1]",
        bool includeTransform = true,
        string? transformOverrideJson = null,
        string? extraRootProperty = null,
        string? extraTransformProperty = null)
    {
        var properties = new List<string>
        {
            $"\"schema_version\":{schemaVersionJson}",
            $"\"hand\":{handJson}",
            $"\"selected_mode\":{selectedModeJson}",
        };
        if (includeTransform)
        {
            var transformProperties = new List<string>();
            if (translationJson is not null)
            {
                transformProperties.Add($"\"translation_m\":{translationJson}");
            }

            if (rotationJson is not null)
            {
                transformProperties.Add($"\"rotation_xyzw\":{rotationJson}");
            }

            if (extraTransformProperty is not null)
            {
                transformProperties.Add(extraTransformProperty);
            }

            var transformJson = transformOverrideJson ?? $"{{{string.Join(',', transformProperties)}}}";
            properties.Add($"\"tracker_to_controller\":{transformJson}");
        }

        if (profileNameJson is not null)
        {
            properties.Add($"\"profile_name\":{profileNameJson}");
        }

        if (controllerSerialJson is not null)
        {
            properties.Add($"\"controller_serial\":{controllerSerialJson}");
        }

        if (trackerSerialJson is not null)
        {
            properties.Add($"\"tracker_serial\":{trackerSerialJson}");
        }

        if (extraRootProperty is not null)
        {
            properties.Add(extraRootProperty);
        }

        return $"{{{string.Join(',', properties)}}}";
    }

    private static BridgeProfile LoadProfile(byte[] bytes)
    {
        var profilePath = TemporaryProfilePath();
        try
        {
            File.WriteAllBytes(profilePath, bytes);
            return BridgeProfileLoader.Load(profilePath);
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    private static InvalidDataException LoadFailure(byte[] bytes)
    {
        var profilePath = TemporaryProfilePath();
        try
        {
            File.WriteAllBytes(profilePath, bytes);
            return Assert.Throws<InvalidDataException>(() => BridgeProfileLoader.Load(profilePath));
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    private static string TemporaryProfilePath() =>
        Path.Combine(Path.GetTempPath(), $"ltb-bridge-profile-{Guid.NewGuid():N}.json");
}
