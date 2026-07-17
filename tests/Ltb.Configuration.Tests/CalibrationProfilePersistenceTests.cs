using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ltb.Configuration.Tests;

public sealed class CalibrationProfilePersistenceTests
{
    public static IEnumerable<object[]> RequiredProfileFields()
    {
        yield return ["schema_version"];
        yield return ["profile_name"];
        yield return ["hand"];
        yield return ["controller_runtime"];
        yield return ["controller_model"];
        yield return ["tracker_serial"];
        yield return ["calibration_policy"];
        yield return ["selected_mode"];
        yield return ["selection_reason"];
        yield return ["tracker_to_controller"];
        yield return ["estimated_lag_ms"];
        yield return ["quality"];
        yield return ["created_utc"];
        yield return ["tracker_to_controller.translation_m"];
        yield return ["tracker_to_controller.rotation_xyzw"];
        yield return ["quality.rotation_rms_deg"];
        yield return ["quality.position_rms_mm"];
        yield return ["quality.translation_condition"];
        yield return ["quality.inlier_ratio"];
    }

    [Fact]
    public void ProfileRoundTripUsesExactSchemaVersionOneNamesAndValues()
    {
        var profile = Profile(
            ControllerHand.Left,
            "LHR-TEST0001",
            ProfileCalibrationMode.FullSixDof,
            new Vector3(0.014f, -0.052f, 0.031f));

        var json = CalibrationProfileJson.SerializeProfile(profile);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            [
                "schema_version",
                "profile_name",
                "hand",
                "controller_runtime",
                "controller_model",
                "controller_serial",
                "tracker_serial",
                "calibration_policy",
                "selected_mode",
                "selection_reason",
                "tracker_to_controller",
                "estimated_lag_ms",
                "quality",
                "created_utc",
            ],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("left", root.GetProperty("hand").GetString());
        Assert.Equal("ALVR", root.GetProperty("controller_runtime").GetString());
        Assert.Equal("Quest 2 Touch", root.GetProperty("controller_model").GetString());
        Assert.Equal("CTRL-TEST0001", root.GetProperty("controller_serial").GetString());
        Assert.Equal("LHR-TEST0001", root.GetProperty("tracker_serial").GetString());
        Assert.Equal("auto", root.GetProperty("calibration_policy").GetString());
        Assert.Equal("full_6dof", root.GetProperty("selected_mode").GetString());
        Assert.Equal(
            "translation observable; held-out position RMS improved",
            root.GetProperty("selection_reason").GetString());
        Assert.EndsWith("Z", root.GetProperty("created_utc").GetString(), StringComparison.Ordinal);

        var transform = root.GetProperty("tracker_to_controller");
        Assert.Equal(
            ["translation_m", "rotation_xyzw"],
            transform.EnumerateObject().Select(property => property.Name));
        Assert.Equal(3, transform.GetProperty("translation_m").GetArrayLength());
        Assert.Equal(4, transform.GetProperty("rotation_xyzw").GetArrayLength());

        var quality = root.GetProperty("quality");
        Assert.Equal(
            ["rotation_rms_deg", "position_rms_mm", "translation_condition", "inlier_ratio"],
            quality.EnumerateObject().Select(property => property.Name));

        var loaded = CalibrationProfileJson.DeserializeProfile(json);
        AssertProfileEqual(profile, loaded);
    }

    [Fact]
    public void OptionalControllerSerialIsOmittedAndNullableQualityFieldsRemainExplicit()
    {
        var profile = Profile(
            ControllerHand.Right,
            "LHR-TEST0002",
            ProfileCalibrationMode.RotationOnly,
            Vector3.Zero,
            controllerSerial: null,
            positionRmsMillimeters: null,
            translationCondition: null);

        var json = CalibrationProfileJson.SerializeProfile(profile);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("controller_serial", out _));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("quality").GetProperty("position_rms_mm").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("quality").GetProperty("translation_condition").ValueKind);

        var loaded = CalibrationProfileJson.DeserializeProfile(json);
        Assert.Null(loaded.ControllerSerial);
        Assert.Null(loaded.Quality.PositionRmsMillimeters);
        Assert.Null(loaded.Quality.TranslationCondition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedSchemaVersionsAreRejected(int schemaVersion)
    {
        var json = CalibrationProfileJson.SerializeProfile(Profile());
        json = json.Replace(
            "\"schema_version\": 1",
            $"\"schema_version\": {schemaVersion}",
            StringComparison.Ordinal);

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(json));

        Assert.Equal(CalibrationProfileFormatReason.UnsupportedSchemaVersion, exception.Reason);
        Assert.Contains("schema_version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedStoreProfileSchemaRetainsTypedReasonAndEntryContext()
    {
        var root = JsonNode.Parse(
            CalibrationProfileJson.SerializeStore(new CalibrationProfileStore([Profile()])))!
            .AsObject();
        root["profiles"]!.AsArray()[0]!.AsObject()["schema_version"] = 2;

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeStore(root.ToJsonString()));

        Assert.Equal(CalibrationProfileFormatReason.UnsupportedSchemaVersion, exception.Reason);
        Assert.Contains("profiles[0]", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJsonSyntaxHasMalformedJsonReason()
    {
        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile("{"));

        Assert.Equal(CalibrationProfileFormatReason.MalformedJson, exception.Reason);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Theory]
    [MemberData(nameof(RequiredProfileFields))]
    public void OmittedRequiredProfileFieldsAreRejected(string fieldPath)
    {
        var root = SerializedProfileObject();
        RemoveProperty(root, fieldPath);

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(root.ToJsonString()));

        Assert.Equal(CalibrationProfileFormatReason.MalformedJson, exception.Reason);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public void OmittedStoreProfilesArrayIsRejected()
    {
        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeStore("{}"));

        Assert.Equal(CalibrationProfileFormatReason.MalformedJson, exception.Reason);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public void WrongCasedPropertyNameIsRejectedAsUnmapped()
    {
        var root = SerializedProfileObject();
        var value = root["estimated_lag_ms"]!.DeepClone();
        root.Remove("estimated_lag_ms");
        root["Estimated_Lag_Ms"] = value;

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(root.ToJsonString()));

        Assert.Equal(CalibrationProfileFormatReason.MalformedJson, exception.Reason);
        var jsonException = Assert.IsType<JsonException>(exception.InnerException);
        Assert.Contains("Estimated_Lag_Ms", jsonException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tracker_to_controller")]
    [InlineData("quality")]
    public void UnknownPropertiesAreRejectedAtEveryProfileObjectLevel(string parentPath)
    {
        var root = SerializedProfileObject();
        ObjectAt(root, parentPath)["unknown_schema_member"] = true;

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(root.ToJsonString()));

        Assert.Equal(CalibrationProfileFormatReason.MalformedJson, exception.Reason);
        var jsonException = Assert.IsType<JsonException>(exception.InnerException);
        Assert.Contains("unknown_schema_member", jsonException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownStoreEnvelopePropertyIsRejected()
    {
        var root = JsonNode.Parse(
            CalibrationProfileJson.SerializeStore(new CalibrationProfileStore([Profile()])))!
            .AsObject();
        root["unknown_store_member"] = true;

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeStore(root.ToJsonString()));

        Assert.Equal(CalibrationProfileFormatReason.MalformedJson, exception.Reason);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("schema_version", "\"1\"")]
    [InlineData("hand", "1")]
    [InlineData("tracker_to_controller.translation_m", "\"not-an-array\"")]
    [InlineData("estimated_lag_ms", "\"11.5\"")]
    [InlineData("quality.rotation_rms_deg", "\"1.2\"")]
    [InlineData("quality.position_rms_mm", "{}")]
    [InlineData("quality.translation_condition", "[]")]
    [InlineData("quality.inlier_ratio", "true")]
    [InlineData("created_utc", "123")]
    public void WrongJsonMemberTypesAreRejected(string fieldPath, string replacementJson)
    {
        var root = SerializedProfileObject();
        SetProperty(root, fieldPath, JsonNode.Parse(replacementJson));

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(root.ToJsonString()));

        Assert.Equal(CalibrationProfileFormatReason.MalformedJson, exception.Reason);
        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Fact]
    public void ProfileMatchingUsesExactTrackerSerialPlusHandOnly()
    {
        var left = Profile(ControllerHand.Left, "LHR-TEST0001");
        var right = Profile(ControllerHand.Right, "LHR-TEST0002");
        var store = new CalibrationProfileStore([right, left]);

        Assert.Same(left, store.FindMatchingProfile("LHR-TEST0001", ControllerHand.Left));
        Assert.Same(right, store.FindMatchingProfile("LHR-TEST0002", ControllerHand.Right));
        Assert.Null(store.FindMatchingProfile("LHR-TEST0001", ControllerHand.Right));
        Assert.Null(store.FindMatchingProfile("LHR-TEST0002", ControllerHand.Left));
        Assert.Null(store.FindMatchingProfile("lhr-test0001", ControllerHand.Left));
        Assert.Null(store.FindMatchingProfile("LHR-TEST9999", ControllerHand.Left));
    }

    [Fact]
    public void StoreRejectsAmbiguousDuplicateSerialAndHandKeys()
    {
        var first = Profile(ControllerHand.Left, "LHR-TEST0001");
        var second = Profile(ControllerHand.Left, "LHR-TEST0001", profileName: "Replacement");

        var exception = Assert.Throws<ArgumentException>(() =>
            new CalibrationProfileStore([first, second]));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreJsonRejectsNullEntriesWithIndexedContext()
    {
        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeStore("{\"profiles\":[null]}"));

        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, exception.Reason);
        Assert.Contains("profiles[0]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not null", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProfileCalibrationPolicy.RotationOnly, ProfileCalibrationMode.FullSixDof)]
    [InlineData(ProfileCalibrationPolicy.FullSixDof, ProfileCalibrationMode.RotationOnly)]
    public void ExplicitPoliciesRejectContradictorySelectedModes(
        ProfileCalibrationPolicy policy,
        ProfileCalibrationMode selectedMode)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Profile(mode: selectedMode, policy: policy));

        Assert.Contains("policy", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FullSixDofRequiresPositionAndConditionQualityEvidence(
        bool omitPositionRms,
        bool omitTranslationCondition)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Profile(
                mode: ProfileCalibrationMode.FullSixDof,
                positionRmsMillimeters: omitPositionRms ? null : 8.4d,
                translationCondition: omitTranslationCondition ? null : 14.7d));

        Assert.Contains("quality evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoPolicyPreservesRotationOnlyFallbackAndSelectionReason()
    {
        var profile = Profile(
            mode: ProfileCalibrationMode.RotationOnly,
            policy: ProfileCalibrationPolicy.Auto,
            positionRmsMillimeters: null,
            translationCondition: null);

        var reloaded = CalibrationProfileJson.DeserializeProfile(
            CalibrationProfileJson.SerializeProfile(profile));

        Assert.Equal(ProfileCalibrationPolicy.Auto, reloaded.CalibrationPolicy);
        Assert.Equal(ProfileCalibrationMode.RotationOnly, reloaded.SelectedMode);
        Assert.Equal("translation unobservable; rotation-only fallback", reloaded.SelectionReason);
    }

    [Theory]
    [InlineData(ProfileCalibrationPolicy.RotationOnly, ProfileCalibrationMode.RotationOnly)]
    [InlineData(ProfileCalibrationPolicy.FullSixDof, ProfileCalibrationMode.FullSixDof)]
    public void ExplicitPoliciesAllowTheirMatchingSelectedModes(
        ProfileCalibrationPolicy policy,
        ProfileCalibrationMode selectedMode)
    {
        var profile = Profile(mode: selectedMode, policy: policy);

        Assert.Equal(policy, profile.CalibrationPolicy);
        Assert.Equal(selectedMode, profile.SelectedMode);
    }

    [Fact]
    public void RotationOnlyRejectsNonZeroTranslationAndPersistsExactZero()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Profile(
                ControllerHand.Right,
                "LHR-TEST0002",
                ProfileCalibrationMode.RotationOnly,
                new Vector3(0.001f, 0f, 0f)));
        Assert.Contains("zero", exception.Message, StringComparison.OrdinalIgnoreCase);

        var valid = Profile(
            ControllerHand.Right,
            "LHR-TEST0002",
            ProfileCalibrationMode.RotationOnly,
            Vector3.Zero);
        var reloaded = CalibrationProfileJson.DeserializeProfile(
            CalibrationProfileJson.SerializeProfile(valid));

        Assert.Equal(Vector3.Zero, reloaded.TrackerToController.TranslationMeters);
        using var document = JsonDocument.Parse(CalibrationProfileJson.SerializeProfile(valid));
        Assert.All(
            document.RootElement
                .GetProperty("tracker_to_controller")
                .GetProperty("translation_m")
                .EnumerateArray(),
            component => Assert.Equal(0d, component.GetDouble()));
    }

    [Fact]
    public void DeserializationRejectsMateriallyNonUnitQuaternion()
    {
        var json = CalibrationProfileJson.SerializeProfile(Profile());
        using var document = JsonDocument.Parse(json);
        var malformed = json.Replace(
            document.RootElement
                .GetProperty("tracker_to_controller")
                .GetProperty("rotation_xyzw")
                .GetRawText(),
            "[0, 0, 0, 2]",
            StringComparison.Ordinal);

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(malformed));

        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, exception.Reason);
        Assert.NotNull(exception.InnerException);
        Assert.Contains("unit length", exception.InnerException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ProfileConstructorRejectsNonFiniteEstimatedLag(double estimatedLagMilliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Profile(estimatedLagMilliseconds: estimatedLagMilliseconds));
    }

    [Theory]
    [InlineData("rotation", double.NaN)]
    [InlineData("rotation", double.PositiveInfinity)]
    [InlineData("position", double.NegativeInfinity)]
    [InlineData("condition", double.PositiveInfinity)]
    [InlineData("inlier", double.NaN)]
    public void QualityConstructorRejectsNonFiniteMetrics(string metric, double invalidValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CalibrationProfileQuality(
                metric == "rotation" ? invalidValue : 1.2d,
                metric == "position" ? invalidValue : 8.4d,
                metric == "condition" ? invalidValue : 14.7d,
                metric == "inlier" ? invalidValue : 0.94d));
    }

    [Fact]
    public void TransformConstructorRejectsNonFiniteComponents()
    {
        Assert.Throws<ArgumentException>(() =>
            new TrackerToControllerTransform(
                new Vector3(float.NaN, 0f, 0f),
                Quaternion.Identity));
        Assert.Throws<ArgumentException>(() =>
            new TrackerToControllerTransform(
                Vector3.Zero,
                new Quaternion(0f, 0f, 0f, float.PositiveInfinity)));
    }

    [Fact]
    public void ConstructorNormalizesCreationTimeToUtcAndJsonRejectsNonUtcTimestamp()
    {
        var localTimestamp = new DateTimeOffset(2026, 7, 17, 9, 0, 0, TimeSpan.FromHours(9));
        var profile = Profile(createdUtc: localTimestamp);

        Assert.Equal(TimeSpan.Zero, profile.CreatedUtc.Offset);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero),
            profile.CreatedUtc);

        var root = SerializedProfileObject();
        root["created_utc"] = "2026-07-17T09:00:00+09:00";
        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(root.ToJsonString()));
        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, exception.Reason);
        Assert.Contains("created_utc", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-07-17T00:00:00Z")]
    [InlineData("2026-07-17T00:00:00.1Z")]
    [InlineData("2026-07-17T00:00:00.1234567Z")]
    public void CreatedUtcAcceptsExplicitIsoUtcForms(string timestamp)
    {
        var root = SerializedProfileObject();
        root["created_utc"] = timestamp;

        var profile = CalibrationProfileJson.DeserializeProfile(root.ToJsonString());

        Assert.Equal(TimeSpan.Zero, profile.CreatedUtc.Offset);
    }

    [Theory]
    [InlineData("2026-07-17T00:00:00")]
    [InlineData("2026-07-17T00:00:00+00:00")]
    [InlineData("2026-07-17 00:00:00Z")]
    [InlineData("07/17/2026 00:00:00Z")]
    [InlineData("2026-07-17T00:00:00.12345678Z")]
    public void CreatedUtcRejectsNonCanonicalOrZoneMissingForms(string timestamp)
    {
        var root = SerializedProfileObject();
        root["created_utc"] = timestamp;

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(root.ToJsonString()));

        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, exception.Reason);
        Assert.Contains("created_utc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoProfileStoreSavesReloadsAndMatchesRegardlessOfInputOrder()
    {
        var left = Profile(
            ControllerHand.Left,
            "LHR-TEST0001",
            ProfileCalibrationMode.FullSixDof,
            new Vector3(0.014f, -0.052f, 0.031f));
        var right = Profile(
            ControllerHand.Right,
            "LHR-TEST0002",
            ProfileCalibrationMode.RotationOnly,
            Vector3.Zero,
            positionRmsMillimeters: null,
            translationCondition: null);
        var store = new CalibrationProfileStore([right, left]);
        var reversed = new CalibrationProfileStore([left, right]);

        Assert.Equal(
            CalibrationProfileJson.SerializeStore(store),
            CalibrationProfileJson.SerializeStore(reversed));

        var path = TemporaryPath();
        try
        {
            CalibrationProfileFile.SaveStore(path, store);
            var loaded = CalibrationProfileFile.LoadStore(path);

            Assert.Equal(2, loaded.Profiles.Count);
            AssertProfileEqual(left, Assert.IsType<CalibrationProfile>(
                loaded.FindMatchingProfile("LHR-TEST0001", ControllerHand.Left)));
            AssertProfileEqual(right, Assert.IsType<CalibrationProfile>(
                loaded.FindMatchingProfile("LHR-TEST0002", ControllerHand.Right)));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(["profiles"], document.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(2, document.RootElement.GetProperty("profiles").GetArrayLength());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StoreUpsertAndAtomicSaveReplaceAnExistingProfile()
    {
        var original = Profile(ControllerHand.Left, "LHR-TEST0001", profileName: "Original");
        var replacement = Profile(ControllerHand.Left, "LHR-TEST0001", profileName: "Replacement");
        var updated = new CalibrationProfileStore([original]).Upsert(replacement);
        var path = TemporaryPath();

        try
        {
            CalibrationProfileFile.SaveStore(path, new CalibrationProfileStore([original]));
            CalibrationProfileFile.SaveStore(path, updated);

            var matched = CalibrationProfileFile.LoadStore(path)
                .FindMatchingProfile("LHR-TEST0001", ControllerHand.Left);
            Assert.NotNull(matched);
            Assert.Equal("Replacement", matched.ProfileName);

            var fileName = Path.GetFileName(path);
            var leftovers = Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                $".{fileName}.*.tmp",
                SearchOption.TopDirectoryOnly);
            Assert.Empty(leftovers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CalibrationProfile Profile(
        ControllerHand hand = ControllerHand.Left,
        string trackerSerial = "LHR-TEST0001",
        ProfileCalibrationMode mode = ProfileCalibrationMode.FullSixDof,
        Vector3? translationMeters = null,
        string? controllerSerial = "CTRL-TEST0001",
        double? positionRmsMillimeters = 8.4d,
        double? translationCondition = 14.7d,
        string profileName = "Quest2 Touch + Vive Tracker test mount",
        ProfileCalibrationPolicy policy = ProfileCalibrationPolicy.Auto,
        double estimatedLagMilliseconds = 11.5d,
        DateTimeOffset? createdUtc = null) =>
        new(
            CalibrationProfileSchema.CurrentVersion,
            profileName,
            hand,
            "ALVR",
            "Quest 2 Touch",
            controllerSerial,
            trackerSerial,
            policy,
            mode,
            mode == ProfileCalibrationMode.FullSixDof
                ? "translation observable; held-out position RMS improved"
                : "translation unobservable; rotation-only fallback",
            new TrackerToControllerTransform(
                translationMeters ?? (mode == ProfileCalibrationMode.RotationOnly
                    ? Vector3.Zero
                    : new Vector3(0.014f, -0.052f, 0.031f)),
                Quaternion.Normalize(new Quaternion(0.012f, -0.704f, 0.019f, 0.710f))),
            estimatedLagMilliseconds,
            new CalibrationProfileQuality(
                1.2d,
                positionRmsMillimeters,
                translationCondition,
                0.94d),
            createdUtc ?? new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero));

    private static JsonObject SerializedProfileObject() =>
        JsonNode.Parse(CalibrationProfileJson.SerializeProfile(Profile()))!.AsObject();

    private static JsonObject ObjectAt(JsonObject root, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return root;
        }

        JsonNode current = root;
        foreach (var segment in path.Split('.'))
        {
            current = current[segment]
                ?? throw new InvalidOperationException($"Test JSON path '{path}' was not found.");
        }

        return current.AsObject();
    }

    private static void RemoveProperty(JsonObject root, string fieldPath)
    {
        var separator = fieldPath.LastIndexOf('.');
        var parentPath = separator < 0 ? string.Empty : fieldPath[..separator];
        var propertyName = separator < 0 ? fieldPath : fieldPath[(separator + 1)..];
        Assert.True(ObjectAt(root, parentPath).Remove(propertyName));
    }

    private static void SetProperty(JsonObject root, string fieldPath, JsonNode? value)
    {
        var separator = fieldPath.LastIndexOf('.');
        var parentPath = separator < 0 ? string.Empty : fieldPath[..separator];
        var propertyName = separator < 0 ? fieldPath : fieldPath[(separator + 1)..];
        ObjectAt(root, parentPath)[propertyName] = value;
    }

    private static void AssertProfileEqual(CalibrationProfile expected, CalibrationProfile actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.ProfileName, actual.ProfileName);
        Assert.Equal(expected.Hand, actual.Hand);
        Assert.Equal(expected.ControllerRuntime, actual.ControllerRuntime);
        Assert.Equal(expected.ControllerModel, actual.ControllerModel);
        Assert.Equal(expected.ControllerSerial, actual.ControllerSerial);
        Assert.Equal(expected.TrackerSerial, actual.TrackerSerial);
        Assert.Equal(expected.CalibrationPolicy, actual.CalibrationPolicy);
        Assert.Equal(expected.SelectedMode, actual.SelectedMode);
        Assert.Equal(expected.SelectionReason, actual.SelectionReason);
        Assert.Equal(expected.TrackerToController, actual.TrackerToController);
        Assert.Equal(expected.EstimatedLagMilliseconds, actual.EstimatedLagMilliseconds);
        Assert.Equal(expected.Quality, actual.Quality);
        Assert.Equal(expected.CreatedUtc, actual.CreatedUtc);
    }

    private static string TemporaryPath() => Path.Combine(
        Path.GetTempPath(),
        $"ltb-profile-{Guid.NewGuid():N}.json");
}
