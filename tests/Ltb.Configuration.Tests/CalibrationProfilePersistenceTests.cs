using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ltb.Core;

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
        yield return ["mount_adjustment.tracker_side"];
        yield return ["mount_adjustment.controller_side"];
        yield return ["estimated_lag_ms"];
        yield return ["quality"];
        yield return ["created_utc"];
        yield return ["tracker_to_controller.translation_m"];
        yield return ["tracker_to_controller.rotation_xyzw"];
        yield return ["mount_adjustment.tracker_side.translation_m"];
        yield return ["mount_adjustment.tracker_side.rotation_xyzw"];
        yield return ["mount_adjustment.controller_side.translation_m"];
        yield return ["mount_adjustment.controller_side.rotation_xyzw"];
        yield return ["quality.rotation_rms_deg"];
        yield return ["quality.position_rms_mm"];
        yield return ["quality.translation_condition"];
        yield return ["quality.inlier_ratio"];
    }

    [Fact]
    public void ProfileRoundTripUsesExactSchemaVersionThreeNamesAndValues()
    {
        var adjustment = Adjustment();
        var profile = Profile(
            ControllerHand.Left,
            "LHR-TEST0001",
            ProfileCalibrationMode.FullSixDof,
            new Vector3(0.014f, -0.052f, 0.031f),
            mountAdjustment: adjustment);

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
                "controller_identity",
                "tracker_serial",
                "driver_profile",
                "calibration_policy",
                "selected_mode",
                "selection_reason",
                "tracker_to_controller",
                "mount_adjustment",
                "estimated_lag_ms",
                "quality",
                "created_utc",
            ],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(3, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("left", root.GetProperty("hand").GetString());
        Assert.Equal("meta_link_libovr", root.GetProperty("controller_runtime").GetString());
        Assert.Equal("Quest 2 Touch", root.GetProperty("controller_model").GetString());
        Assert.Equal("CTRL-TEST0001", root.GetProperty("controller_identity").GetString());
        Assert.Equal("LHR-TEST0001", root.GetProperty("tracker_serial").GetString());
        Assert.Equal("ltb_touch", root.GetProperty("driver_profile").GetString());
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

        var mountAdjustment = root.GetProperty("mount_adjustment");
        Assert.Equal(
            ["tracker_side", "controller_side"],
            mountAdjustment.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["translation_m", "rotation_xyzw"],
            mountAdjustment
                .GetProperty("tracker_side")
                .EnumerateObject()
                .Select(property => property.Name));
        Assert.Equal(
            adjustment.TrackerSideAdjustment.TranslationMeters.X,
            mountAdjustment
                .GetProperty("tracker_side")
                .GetProperty("translation_m")[0]
                .GetSingle());
        Assert.Equal(
            adjustment.ControllerSideAdjustment.Rotation.W,
            mountAdjustment
                .GetProperty("controller_side")
                .GetProperty("rotation_xyzw")[3]
                .GetSingle());

        var quality = root.GetProperty("quality");
        Assert.Equal(
            ["rotation_rms_deg", "position_rms_mm", "translation_condition", "inlier_ratio"],
            quality.EnumerateObject().Select(property => property.Name));

        var loaded = CalibrationProfileJson.DeserializeProfile(json);
        AssertProfileEqual(profile, loaded);
    }

    [Theory]
    [InlineData("Quest 2 Touch")]
    [InlineData("Quest 3 Touch Plus")]
    [InlineData("Quest Pro Touch")]
    public void SchemaVersionTwoLoadsWithIdentityAdjustmentAndPreservesTouchFamilyCompatibility(
        string controllerModel)
    {
        var profile = Profile(
            controllerRuntime: ControllerRuntimeIdentities.MetaLinkLibOvr,
            controllerModel: controllerModel,
            schemaVersion: CalibrationProfileSchema.DriverProfileVersion);

        var json = CalibrationProfileJson.SerializeProfile(profile);
        var loaded = CalibrationProfileJson.DeserializeProfile(json);

        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal(MountAdjustment.Identity, loaded.MountAdjustment);
        Assert.DoesNotContain("\"mount_adjustment\"", json, StringComparison.Ordinal);
        Assert.Equal(json, CalibrationProfileJson.SerializeProfile(loaded));
        Assert.Equal(ControllerRuntimeIdentities.MetaLinkLibOvr, loaded.ControllerRuntime);
        Assert.Equal(controllerModel, loaded.ControllerModel);
        Assert.True(loaded.MatchesController(
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            controllerModel,
            "CTRL-TEST0001"));
        Assert.False(loaded.MatchesController(
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Different Meta Touch family",
            "CTRL-TEST0001"));
        Assert.False(loaded.MatchesController(
            CalibrationDriverProfiles.LtbTouch,
            "Different runtime",
            controllerModel,
            "CTRL-TEST0001"));
    }

    [Fact]
    public void SchemaVersionOneLegacyReadPreservesExactIdentityAndShape()
    {
        var legacy = LegacyProfile();
        var json = CalibrationProfileJson.SerializeProfile(legacy);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("ALVR", root.GetProperty("controller_runtime").GetString());
        Assert.Equal("CTRL-TEST0001", root.GetProperty("controller_serial").GetString());
        Assert.False(root.TryGetProperty("controller_identity", out _));
        Assert.False(root.TryGetProperty("driver_profile", out _));
        Assert.False(root.TryGetProperty("mount_adjustment", out _));

        var loaded = CalibrationProfileJson.DeserializeProfile(json);

        Assert.True(loaded.IsLegacy);
        Assert.Null(loaded.DriverProfile);
        Assert.Equal(MountAdjustment.Identity, loaded.MountAdjustment);
        Assert.Equal("ALVR", loaded.ControllerRuntime);
        Assert.Equal("CTRL-TEST0001", loaded.ControllerSerial);
        Assert.True(loaded.MatchesController("ALVR", "Quest 2 Touch"));
        Assert.False(loaded.MatchesController(
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            "CTRL-TEST0001"));
        Assert.Equal(json, CalibrationProfileJson.SerializeProfile(loaded));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unexpected_driver")]
    public void SchemaVersionTwoRejectsMissingBlankOrUnexpectedDriverProfile(
        string? driverProfile)
    {
        var root = SerializedSchemaTwoProfileObject();
        if (driverProfile is null)
        {
            Assert.True(root.Remove("driver_profile"));
        }
        else
        {
            root["driver_profile"] = driverProfile;
        }

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(root.ToJsonString()));

        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, exception.Reason);
        Assert.Contains("driver", exception.Message + exception.InnerException?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SchemaSpecificIdentityMembersCannotSilentlyRelabelProfiles()
    {
        var legacyRoot = JsonNode.Parse(
            CalibrationProfileJson.SerializeProfile(LegacyProfile()))!.AsObject();
        legacyRoot["driver_profile"] = CalibrationDriverProfiles.LtbTouch;

        var legacyException = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(legacyRoot.ToJsonString()));
        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, legacyException.Reason);

        var currentRoot = SerializedProfileObject();
        currentRoot["controller_serial"] = "legacy-serial";
        var currentException = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(currentRoot.ToJsonString()));
        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, currentException.Reason);
    }

    [Fact]
    public void ExplicitMigrationPreservesControllerContractAndReversibleOriginal()
    {
        var legacy = LegacyProfile();
        var originalJson = CalibrationProfileJson.SerializeProfile(legacy);
        var target = new CalibrationProfileTargetIdentity(
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.LegacyAlvr,
            "Quest 2 Touch",
            "CTRL-TEST0001");

        var result = CalibrationProfileMigration.MigrateLegacyProfile(legacy, target);

        Assert.Same(legacy, result.OriginalLegacyProfile);
        Assert.Same(legacy, result.Revert());
        Assert.Equal(originalJson, CalibrationProfileJson.SerializeProfile(legacy));
        Assert.Equal(3, result.MigratedProfile.SchemaVersion);
        Assert.Equal(CalibrationDriverProfiles.LtbTouch, result.MigratedProfile.DriverProfile);
        Assert.Equal(ControllerRuntimeIdentities.LegacyAlvr, result.MigratedProfile.ControllerRuntime);
        Assert.Equal("CTRL-TEST0001", result.MigratedProfile.ControllerIdentity);
        Assert.Equal(legacy.TrackerToController, result.MigratedProfile.TrackerToController);
        Assert.Equal(MountAdjustment.Identity, result.MigratedProfile.MountAdjustment);
        Assert.Equal(legacy.Quality, result.MigratedProfile.Quality);
        Assert.NotEqual(originalJson, CalibrationProfileJson.SerializeProfile(result.MigratedProfile));
    }

    [Fact]
    public void MigrationRejectsCrossRuntimeRelabelAndLeavesOriginalUnchanged()
    {
        var legacy = LegacyProfile();
        var originalJson = CalibrationProfileJson.SerializeProfile(legacy);
        var target = new CalibrationProfileTargetIdentity(
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            "META-RUNTIME-IDENTITY");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CalibrationProfileMigration.MigrateLegacyProfile(legacy, target));

        Assert.Contains("recalibration", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cross-runtime", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalJson, CalibrationProfileJson.SerializeProfile(legacy));
        Assert.True(legacy.IsLegacy);
    }

    [Fact]
    public void MigrationRejectsCurrentProfilesAndRequiresSupportedCallerTarget()
    {
        Assert.Throws<ArgumentException>(() =>
            CalibrationProfileMigration.MigrateLegacyProfile(
                Profile(),
                new CalibrationProfileTargetIdentity(
                    CalibrationDriverProfiles.LtbTouch,
                    ControllerRuntimeIdentities.MetaLinkLibOvr,
                    "Quest 2 Touch",
                    "CTRL-TEST0001")));
        Assert.Throws<ArgumentException>(() =>
            new CalibrationProfileTargetIdentity(
                "unexpected_driver",
                ControllerRuntimeIdentities.MetaLinkLibOvr,
                "Quest 2 Touch",
                "CTRL-TEST0001"));
        var identityFreeTarget = new CalibrationProfileTargetIdentity(
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            controllerIdentity: null);
        Assert.Null(identityFreeTarget.ControllerIdentity);
    }

    [Fact]
    public void ExplicitMigrationPreservesAnUnavailableControllerIdentity()
    {
        var legacy = LegacyProfile(controllerSerial: null);
        var target = new CalibrationProfileTargetIdentity(
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.LegacyAlvr,
            "Quest 2 Touch",
            controllerIdentity: null);

        var result = CalibrationProfileMigration.MigrateLegacyProfile(legacy, target);

        Assert.Null(result.OriginalLegacyProfile.ControllerIdentity);
        Assert.Null(result.MigratedProfile.ControllerIdentity);
        Assert.Equal(CalibrationProfileSchema.CurrentVersion, result.MigratedProfile.SchemaVersion);
    }

    [Fact]
    public void ProfileSerializationIsByteDeterministicAcrossRoundTrip()
    {
        var first = CalibrationProfileJson.SerializeProfile(
            Profile(mountAdjustment: Adjustment()));
        var second = CalibrationProfileJson.SerializeProfile(
            CalibrationProfileJson.DeserializeProfile(first));

        Assert.Equal(first, second);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibleSchemaThreeConstructorDefaultsToIdentityAdjustment()
    {
        var profile = Profile();

        Assert.Equal(MountAdjustment.Identity, profile.MountAdjustment);

        using var document = JsonDocument.Parse(
            CalibrationProfileJson.SerializeProfile(profile));
        var adjustment = document.RootElement.GetProperty("mount_adjustment");
        foreach (var side in new[] { "tracker_side", "controller_side" })
        {
            Assert.Equal(
                [0f, 0f, 0f],
                adjustment
                    .GetProperty(side)
                    .GetProperty("translation_m")
                    .EnumerateArray()
                    .Select(component => component.GetSingle()));
            Assert.Equal(
                [0f, 0f, 0f, 1f],
                adjustment
                    .GetProperty(side)
                    .GetProperty("rotation_xyzw")
                    .EnumerateArray()
                    .Select(component => component.GetSingle()));
        }
    }

    [Fact]
    public void SchemaVersionTwoRoundTripsWithoutAControllerIdentity()
    {
        var missing = SerializedSchemaTwoProfileObject();
        Assert.True(missing.Remove("controller_identity"));
        var missingIdentity = CalibrationProfileJson.DeserializeProfile(
            missing.ToJsonString());
        Assert.Null(missingIdentity.ControllerIdentity);
        Assert.False(JsonNode.Parse(
            CalibrationProfileJson.SerializeProfile(missingIdentity))!
            .AsObject()
            .ContainsKey("controller_identity"));

        var nullIdentity = SerializedSchemaTwoProfileObject();
        nullIdentity["controller_identity"] = null;
        var explicitNullIdentity = CalibrationProfileJson.DeserializeProfile(
            nullIdentity.ToJsonString());
        Assert.Null(explicitNullIdentity.ControllerIdentity);

        var constructed = Profile(
            controllerSerial: null,
            schemaVersion: CalibrationProfileSchema.DriverProfileVersion);
        Assert.Null(constructed.ControllerIdentity);
        Assert.Equal(
            CalibrationProfileJson.SerializeProfile(constructed),
            CalibrationProfileJson.SerializeProfile(
                CalibrationProfileJson.DeserializeProfile(
                    CalibrationProfileJson.SerializeProfile(constructed))));
    }

    [Fact]
    public void LegacyConstructorRejectsCurrentVersionInsteadOfSilentlyWritingSchemaThree()
    {
        var legacy = LegacyProfile();

        var exception = Assert.Throws<ArgumentException>(() => new CalibrationProfile(
            CalibrationProfileSchema.CurrentVersion,
            legacy.ProfileName,
            legacy.Hand,
            legacy.ControllerRuntime,
            legacy.ControllerModel,
            legacy.ControllerIdentity,
            legacy.TrackerSerial,
            legacy.CalibrationPolicy,
            legacy.SelectedMode,
            legacy.SelectionReason,
            legacy.TrackerToController,
            legacy.EstimatedLagMilliseconds,
            legacy.Quality,
            legacy.CreatedUtc));

        Assert.Equal("driverProfile", exception.ParamName);
        Assert.Contains("Schema-version-3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullableQualityFieldsRemainExplicit()
    {
        var profile = Profile(
            ControllerHand.Right,
            "LHR-TEST0002",
            ProfileCalibrationMode.RotationOnly,
            Vector3.Zero,
            positionRmsMillimeters: null,
            translationCondition: null);

        var json = CalibrationProfileJson.SerializeProfile(profile);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("controller_serial", out _));
        Assert.Equal("CTRL-TEST0001", root.GetProperty("controller_identity").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("quality").GetProperty("position_rms_mm").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("quality").GetProperty("translation_condition").ValueKind);

        var loaded = CalibrationProfileJson.DeserializeProfile(json);
        Assert.Equal("CTRL-TEST0001", loaded.ControllerIdentity);
        Assert.Null(loaded.Quality.PositionRmsMillimeters);
        Assert.Null(loaded.Quality.TranslationCondition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void UnsupportedSchemaVersionsAreRejected(int schemaVersion)
    {
        var json = CalibrationProfileJson.SerializeProfile(Profile());
        json = json.Replace(
            $"\"schema_version\": {CalibrationProfileSchema.CurrentVersion}",
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
        root["profiles"]!.AsArray()[0]!.AsObject()["schema_version"] = 4;

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
    public void SchemaVersionThreeRequiresNonNullMountAdjustment()
    {
        var missing = SerializedProfileObject();
        Assert.True(missing.Remove("mount_adjustment"));
        var missingException = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(missing.ToJsonString()));
        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, missingException.Reason);

        var explicitNull = SerializedProfileObject();
        explicitNull["mount_adjustment"] = null;
        var nullException = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(explicitNull.ToJsonString()));
        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, nullException.Reason);
    }

    [Theory]
    [InlineData("tracker_side")]
    [InlineData("controller_side")]
    public void AdjustmentSlotTranslationAllowsHalfMeterAndRejectsJustOverBoundary(string side)
    {
        foreach (var translationJson in new[] { "[0.5,0,0]", "[0.3,0.4,0]" })
        {
            var boundary = SerializedProfileObject();
            SetProperty(
                boundary,
                $"mount_adjustment.{side}.translation_m",
                JsonNode.Parse(translationJson));

            var loaded = CalibrationProfileJson.DeserializeProfile(boundary.ToJsonString());
            var loadedSlot = side == "tracker_side"
                ? loaded.MountAdjustment.TrackerSideAdjustment
                : loaded.MountAdjustment.ControllerSideAdjustment;
            Assert.Equal(0.5f, loadedSlot.TranslationMeters.Length());
        }

        var overBoundary = SerializedProfileObject();
        SetProperty(
            overBoundary,
            $"mount_adjustment.{side}.translation_m",
            JsonNode.Parse("[0.5000001,0,0]"));

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(overBoundary.ToJsonString()));
        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, exception.Reason);
        Assert.Contains("0.5", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tracker_side")]
    [InlineData("controller_side")]
    public void AdjustmentSlotRejectsMalformedDegenerateAndNonUnitRotations(string side)
    {
        foreach (var rotationJson in new[]
                 {
                     "[0,0,0]",
                     "[0,0,0,0]",
                     "[0,0,0,1.0003]",
                 })
        {
            var root = SerializedProfileObject();
            SetProperty(
                root,
                $"mount_adjustment.{side}.rotation_xyzw",
                JsonNode.Parse(rotationJson));

            var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
                CalibrationProfileJson.DeserializeProfile(root.ToJsonString()));
            Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, exception.Reason);
        }

        var withinTolerance = SerializedProfileObject();
        SetProperty(
            withinTolerance,
            $"mount_adjustment.{side}.rotation_xyzw",
            JsonNode.Parse("[0,0,0,1.0002]"));
        var loaded = CalibrationProfileJson.DeserializeProfile(withinTolerance.ToJsonString());
        var loadedRotation = side == "tracker_side"
            ? loaded.MountAdjustment.TrackerSideAdjustment.Rotation
            : loaded.MountAdjustment.ControllerSideAdjustment.Rotation;
        Assert.Equal(Quaternion.Identity, loadedRotation);
    }

    [Theory]
    [InlineData("tracker_side")]
    [InlineData("controller_side")]
    public void AdjustmentSlotRejectsNonFiniteJsonComponents(string side)
    {
        var json = CalibrationProfileJson.SerializeProfile(Profile());
        using var document = JsonDocument.Parse(json);
        var original = document.RootElement
            .GetProperty("mount_adjustment")
            .GetProperty(side)
            .GetProperty("rotation_xyzw")
            .GetRawText();
        var malformed = json.Replace(
            original,
            "[0,0,0,NaN]",
            StringComparison.Ordinal);

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(malformed));
        Assert.Equal(CalibrationProfileFormatReason.MalformedJson, exception.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OlderSchemasRejectMountAdjustmentMembersInsteadOfDiscardingThem(int schemaVersion)
    {
        var source = SerializedProfileObject();
        var adjustment = source["mount_adjustment"]!.DeepClone();
        var older = schemaVersion == CalibrationProfileSchema.LegacyVersion
            ? JsonNode.Parse(CalibrationProfileJson.SerializeProfile(LegacyProfile()))!.AsObject()
            : SerializedSchemaTwoProfileObject();
        older["mount_adjustment"] = adjustment;

        var exception = Assert.Throws<CalibrationProfileFormatException>(() =>
            CalibrationProfileJson.DeserializeProfile(older.ToJsonString()));
        Assert.Equal(CalibrationProfileFormatReason.InvalidProfileData, exception.Reason);
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
    [InlineData("mount_adjustment")]
    [InlineData("mount_adjustment.tracker_side")]
    [InlineData("mount_adjustment.controller_side")]
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
    [InlineData("mount_adjustment", "\"not-an-object\"")]
    [InlineData("mount_adjustment.tracker_side", "[]")]
    [InlineData("mount_adjustment.controller_side.translation_m", "\"not-an-array\"")]
    [InlineData("mount_adjustment.tracker_side.rotation_xyzw", "{}")]
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
    public void CandidateLookupUsesExactTrackerSerialPlusHand()
    {
        var left = Profile(ControllerHand.Left, "LHR-TEST0001");
        var right = Profile(ControllerHand.Right, "LHR-TEST0002");
        var store = new CalibrationProfileStore([right, left]);

        Assert.Same(left, store.FindCandidateProfile("LHR-TEST0001", ControllerHand.Left));
        Assert.Same(right, store.FindCandidateProfile("LHR-TEST0002", ControllerHand.Right));
        Assert.Null(store.FindCandidateProfile("LHR-TEST0001", ControllerHand.Right));
        Assert.Null(store.FindCandidateProfile("LHR-TEST0002", ControllerHand.Left));
        Assert.Null(store.FindCandidateProfile("lhr-test0001", ControllerHand.Left));
        Assert.Null(store.FindCandidateProfile("LHR-TEST9999", ControllerHand.Left));
    }

    [Fact]
    public void ReusableMatchingIncludesDriverRuntimeModelAndRuntimeIdentity()
    {
        var profile = Profile();
        var store = new CalibrationProfileStore([profile]);

        Assert.Same(profile, store.FindMatchingProfile(
            "LHR-TEST0001",
            ControllerHand.Left,
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            "CTRL-TEST0001"));
        Assert.Null(store.FindMatchingProfile(
            "LHR-TEST0001",
            ControllerHand.Left,
            CalibrationDriverProfiles.LtbTouch,
            "ALVR",
            "Quest 2 Touch",
            "CTRL-TEST0001"));
        Assert.Null(store.FindMatchingProfile(
            "LHR-TEST0001",
            ControllerHand.Left,
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            "DIFFERENT-IDENTITY"));
    }

    [Fact]
    public void IdentityFreeMetaProfileRemainsBoundToExactTrackerHandRuntimeAndModel()
    {
        var profile = Profile(controllerSerial: null);
        var store = new CalibrationProfileStore([profile]);

        Assert.Same(profile, store.FindMatchingProfile(
            "LHR-TEST0001",
            ControllerHand.Left,
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            controllerIdentity: null));
        Assert.Null(store.FindMatchingProfile(
            "LHR-TEST0001",
            ControllerHand.Right,
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            controllerIdentity: null));
        Assert.Null(store.FindMatchingProfile(
            "LHR-DIFFERENT",
            ControllerHand.Left,
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            controllerIdentity: null));
        Assert.Null(store.FindMatchingProfile(
            "LHR-TEST0001",
            ControllerHand.Left,
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.LegacyAlvr,
            "Quest 2 Touch",
            controllerIdentity: null));
        Assert.Null(store.FindMatchingProfile(
            "LHR-TEST0001",
            ControllerHand.Left,
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Different Touch model",
            controllerIdentity: null));
        Assert.Null(store.FindMatchingProfile(
            "LHR-TEST0001",
            ControllerHand.Left,
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            "NOW-EXPOSED-IDENTITY"));
    }

    [Fact]
    public void LegacyCandidateCannotBeSelectedAsMetaLinkProfile()
    {
        var legacy = LegacyProfile();
        var store = new CalibrationProfileStore([legacy]);

        Assert.Same(legacy, store.FindCandidateProfile("LHR-TEST0001", ControllerHand.Left));
        Assert.Null(store.FindMatchingProfile(
            "LHR-TEST0001",
            ControllerHand.Left,
            CalibrationDriverProfiles.LtbTouch,
            ControllerRuntimeIdentities.MetaLinkLibOvr,
            "Quest 2 Touch",
            "CTRL-TEST0001"));
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
    public void ProfileRejectsOversizedAdjustmentSlotsAndSchemaTwoDataLoss()
    {
        var oversizedException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MountAdjustment(
                new RigidTransform(
                    Quaternion.Identity,
                    new Vector3(0.5000001f, 0f, 0f)),
                RigidTransform.Identity));
        Assert.Equal("trackerSideAdjustment", oversizedException.ParamName);

        var nonIdentity = Adjustment();
        var schemaTwoException = Assert.Throws<ArgumentException>(() =>
            Profile(
                mountAdjustment: nonIdentity,
                schemaVersion: CalibrationProfileSchema.DriverProfileVersion));
        Assert.Equal("mountAdjustment", schemaTwoException.ParamName);
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
                loaded.FindCandidateProfile("LHR-TEST0001", ControllerHand.Left)));
            AssertProfileEqual(right, Assert.IsType<CalibrationProfile>(
                loaded.FindCandidateProfile("LHR-TEST0002", ControllerHand.Right)));

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
        var replacement = Profile(
            ControllerHand.Left,
            "LHR-TEST0001",
            profileName: "Replacement",
            mountAdjustment: Adjustment());
        var preserved = Profile(
            ControllerHand.Right,
            "LHR-TEST0002",
            ProfileCalibrationMode.RotationOnly,
            Vector3.Zero,
            positionRmsMillimeters: null,
            translationCondition: null,
            profileName: "Preserved");
        var updated = new CalibrationProfileStore([original, preserved]).Upsert(replacement);
        var path = TemporaryPath();

        try
        {
            CalibrationProfileFile.SaveStore(path, new CalibrationProfileStore([original, preserved]));
            CalibrationProfileFile.SaveStore(path, updated);

            var loaded = CalibrationProfileFile.LoadStore(path);
            var matched = loaded.FindCandidateProfile("LHR-TEST0001", ControllerHand.Left);
            Assert.NotNull(matched);
            Assert.Equal("Replacement", matched.ProfileName);
            Assert.Equal(Adjustment(), matched.MountAdjustment);
            AssertProfileEqual(
                preserved,
                Assert.IsType<CalibrationProfile>(
                    loaded.FindCandidateProfile("LHR-TEST0002", ControllerHand.Right)));

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
        string controllerRuntime = ControllerRuntimeIdentities.MetaLinkLibOvr,
        string controllerModel = "Quest 2 Touch",
        ProfileCalibrationPolicy policy = ProfileCalibrationPolicy.Auto,
        double estimatedLagMilliseconds = 11.5d,
        DateTimeOffset? createdUtc = null,
        MountAdjustment? mountAdjustment = null,
        int schemaVersion = CalibrationProfileSchema.CurrentVersion)
    {
        var selectionReason = mode == ProfileCalibrationMode.FullSixDof
            ? "translation observable; held-out position RMS improved"
            : "translation unobservable; rotation-only fallback";
        var trackerToController = new TrackerToControllerTransform(
            translationMeters ?? (mode == ProfileCalibrationMode.RotationOnly
                ? Vector3.Zero
                : new Vector3(0.014f, -0.052f, 0.031f)),
            Quaternion.Normalize(new Quaternion(0.012f, -0.704f, 0.019f, 0.710f)));
        var quality = new CalibrationProfileQuality(
            1.2d,
            positionRmsMillimeters,
            translationCondition,
            0.94d);
        var creationTime =
            createdUtc ?? new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

        return mountAdjustment is null
            ? new CalibrationProfile(
                schemaVersion,
                profileName,
                hand,
                controllerRuntime,
                controllerModel,
                controllerSerial,
                trackerSerial,
                CalibrationDriverProfiles.LtbTouch,
                policy,
                mode,
                selectionReason,
                trackerToController,
                estimatedLagMilliseconds,
                quality,
                creationTime)
            : new CalibrationProfile(
                schemaVersion,
                profileName,
                hand,
                controllerRuntime,
                controllerModel,
                controllerSerial,
                trackerSerial,
                CalibrationDriverProfiles.LtbTouch,
                policy,
                mode,
                selectionReason,
                trackerToController,
                mountAdjustment,
                estimatedLagMilliseconds,
                quality,
                creationTime);
    }

    private static CalibrationProfile LegacyProfile(
        string? controllerSerial = "CTRL-TEST0001") => new(
        CalibrationProfileSchema.LegacyVersion,
        "Legacy Quest2 Touch + Vive Tracker test mount",
        ControllerHand.Left,
        ControllerRuntimeIdentities.LegacyAlvr,
        "Quest 2 Touch",
        controllerSerial,
        "LHR-TEST0001",
        ProfileCalibrationPolicy.Auto,
        ProfileCalibrationMode.RotationOnly,
        "translation unobservable; rotation-only fallback",
        new TrackerToControllerTransform(Vector3.Zero, Quaternion.Identity),
        10d,
        new CalibrationProfileQuality(1d, null, null, 0.95d),
        new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero));

    private static JsonObject SerializedProfileObject() =>
        JsonNode.Parse(CalibrationProfileJson.SerializeProfile(Profile()))!.AsObject();

    private static JsonObject SerializedSchemaTwoProfileObject() =>
        JsonNode.Parse(CalibrationProfileJson.SerializeProfile(
            Profile(schemaVersion: CalibrationProfileSchema.DriverProfileVersion)))!.AsObject();

    private static MountAdjustment Adjustment() => new(
        new RigidTransform(
            Quaternion.Normalize(new Quaternion(0.03f, -0.01f, 0.02f, 0.999f)),
            new Vector3(0.012f, -0.023f, 0.034f)),
        new RigidTransform(
            Quaternion.Normalize(new Quaternion(-0.02f, 0.04f, 0.01f, 0.998f)),
            new Vector3(-0.017f, 0.009f, -0.028f)));

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
        Assert.Equal(expected.ControllerIdentity, actual.ControllerIdentity);
        Assert.Equal(expected.TrackerSerial, actual.TrackerSerial);
        Assert.Equal(expected.DriverProfile, actual.DriverProfile);
        Assert.Equal(expected.CalibrationPolicy, actual.CalibrationPolicy);
        Assert.Equal(expected.SelectedMode, actual.SelectedMode);
        Assert.Equal(expected.SelectionReason, actual.SelectionReason);
        Assert.Equal(expected.TrackerToController, actual.TrackerToController);
        Assert.Equal(expected.MountAdjustment, actual.MountAdjustment);
        Assert.Equal(expected.EstimatedLagMilliseconds, actual.EstimatedLagMilliseconds);
        Assert.Equal(expected.Quality, actual.Quality);
        Assert.Equal(expected.CreatedUtc, actual.CreatedUtc);
    }

    private static string TemporaryPath() => Path.Combine(
        Path.GetTempPath(),
        $"ltb-profile-{Guid.NewGuid():N}.json");
}
