namespace Ltb.Configuration.Tests;

public sealed class RecalibrationEvaluatorTests
{
    [Fact]
    public void MatchingCurrentStateDoesNotRequireRecalibration()
    {
        var evaluation = RecalibrationEvaluator.Evaluate(Profile(), MatchingContext());

        Assert.False(evaluation.IsRequired);
        Assert.Empty(evaluation.Triggers);
    }

    [Theory]
    [InlineData("Quest 2 Touch")]
    [InlineData("Quest 3 Touch Plus")]
    [InlineData("Quest Pro Touch")]
    public void MatchingUsesPersistedDriverAndControllerIdentityWithoutFamilySpecificBranches(
        string controllerModel)
    {
        var evaluation = RecalibrationEvaluator.Evaluate(
            Profile(controllerModel),
            MatchingContext(controllerModel));

        Assert.False(evaluation.IsRequired);
    }

    [Theory]
    [InlineData(RecalibrationTriggerKind.ExplicitRequest)]
    [InlineData(RecalibrationTriggerKind.TrackerHandAssociationChanged)]
    [InlineData(RecalibrationTriggerKind.MountMoved)]
    [InlineData(RecalibrationTriggerKind.ValidationThresholdExceeded)]
    [InlineData(RecalibrationTriggerKind.ControllerIdentityChanged)]
    [InlineData(RecalibrationTriggerKind.TransformConventionChanged)]
    [InlineData(RecalibrationTriggerKind.SchemaVersionChanged)]
    public void EachTriggerCategoryProducesItsStructuredReason(RecalibrationTriggerKind kind)
    {
        var context = kind switch
        {
            RecalibrationTriggerKind.ExplicitRequest => MatchingContext() with
            {
                ExplicitRequest = true,
            },
            RecalibrationTriggerKind.TrackerHandAssociationChanged => MatchingContext() with
            {
                Hand = ControllerHand.Right,
            },
            RecalibrationTriggerKind.MountMoved => MatchingContext() with
            {
                MountMoved = true,
            },
            RecalibrationTriggerKind.ValidationThresholdExceeded => MatchingContext() with
            {
                ValidationThresholdExceeded = true,
            },
            RecalibrationTriggerKind.ControllerIdentityChanged => MatchingContext() with
            {
                ControllerModel = "Different Touch Model",
            },
            RecalibrationTriggerKind.TransformConventionChanged => MatchingContext() with
            {
                ExpectedTransformConvention = "T_C_T:translation_m:rotation_xyzw",
            },
            RecalibrationTriggerKind.SchemaVersionChanged => MatchingContext() with
            {
                ExpectedSchemaVersion = CalibrationProfileSchema.LegacyVersion,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var evaluation = RecalibrationEvaluator.Evaluate(Profile(), context);

        Assert.True(evaluation.IsRequired);
        var trigger = Assert.Single(evaluation.Triggers);
        Assert.Equal(kind, trigger.Kind);
        Assert.False(string.IsNullOrWhiteSpace(trigger.Message));
    }

    [Fact]
    public void DifferentTrackerSerialIsAnAssociationChange()
    {
        var evaluation = RecalibrationEvaluator.Evaluate(
            Profile(),
            MatchingContext() with { TrackerSerial = "LHR-TEST0002" });

        Assert.Equal(
            RecalibrationTriggerKind.TrackerHandAssociationChanged,
            Assert.Single(evaluation.Triggers).Kind);
    }

    [Fact]
    public void DifferentControllerRuntimeIsAControllerIdentityChange()
    {
        var evaluation = RecalibrationEvaluator.Evaluate(
            Profile(),
            MatchingContext() with { ControllerRuntime = "Different Runtime" });

        Assert.Equal(
            RecalibrationTriggerKind.ControllerIdentityChanged,
            Assert.Single(evaluation.Triggers).Kind);
    }

    [Fact]
    public void DifferentControllerRuntimeIdentityIsAControllerIdentityChange()
    {
        var evaluation = RecalibrationEvaluator.Evaluate(
            Profile(),
            MatchingContext() with { ControllerIdentity = "DIFFERENT-IDENTITY" });

        Assert.Equal(
            RecalibrationTriggerKind.ControllerIdentityChanged,
            Assert.Single(evaluation.Triggers).Kind);
    }

    [Fact]
    public void LegacyAlvrProfileRequiresRecalibrationForMetaLinkDriverIdentity()
    {
        var evaluation = RecalibrationEvaluator.Evaluate(
            LegacyProfile(),
            MatchingContext());

        Assert.Equal(
            [
                RecalibrationTriggerKind.DriverProfileChanged,
                RecalibrationTriggerKind.ControllerIdentityChanged,
                RecalibrationTriggerKind.SchemaVersionChanged,
            ],
            evaluation.Triggers.Select(trigger => trigger.Kind));
    }

    [Fact]
    public void UnexpectedCurrentDriverProfileIsRejected()
    {
        Assert.Throws<ArgumentException>(() => RecalibrationEvaluator.Evaluate(
            Profile(),
            MatchingContext() with { DriverProfile = "unexpected_driver" }));
    }

    [Fact]
    public void MissingCurrentDriverProfileRequiresRecalibration()
    {
        var evaluation = RecalibrationEvaluator.Evaluate(
            Profile(),
            MatchingContext() with { DriverProfile = null });

        Assert.Equal(
            RecalibrationTriggerKind.DriverProfileChanged,
            Assert.Single(evaluation.Triggers).Kind);
    }

    [Fact]
    public void MultipleTriggersAreAllReturnedInStableOrder()
    {
        var evaluation = RecalibrationEvaluator.Evaluate(
            Profile(),
            MatchingContext() with
            {
                ExplicitRequest = true,
                TrackerSerial = "LHR-TEST0002",
                MountMoved = true,
                ValidationThresholdExceeded = true,
                ControllerModel = "Different Touch Model",
                ExpectedTransformConvention = "changed",
                ExpectedSchemaVersion = CalibrationProfileSchema.LegacyVersion,
            });

        Assert.Equal(
            [
                RecalibrationTriggerKind.ExplicitRequest,
                RecalibrationTriggerKind.TrackerHandAssociationChanged,
                RecalibrationTriggerKind.MountMoved,
                RecalibrationTriggerKind.ValidationThresholdExceeded,
                RecalibrationTriggerKind.ControllerIdentityChanged,
                RecalibrationTriggerKind.TransformConventionChanged,
                RecalibrationTriggerKind.SchemaVersionChanged,
            ],
            evaluation.Triggers.Select(trigger => trigger.Kind));
    }

    private static CalibrationProfile Profile(
        string controllerModel = "Quest 2 Touch") => new(
        CalibrationProfileSchema.CurrentVersion,
        "Synthetic left profile",
        ControllerHand.Left,
        ControllerRuntimeIdentities.MetaLinkLibOvr,
        controllerModel,
        "CTRL-TEST0001",
        "LHR-TEST0001",
        CalibrationDriverProfiles.LtbTouch,
        ProfileCalibrationPolicy.Auto,
        ProfileCalibrationMode.RotationOnly,
        "translation unobservable; rotation-only fallback",
        new TrackerToControllerTransform(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity),
        10d,
        new CalibrationProfileQuality(1d, null, null, 0.95d),
        new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero));

    private static RecalibrationContext MatchingContext(
        string controllerModel = "Quest 2 Touch") => new(
        ExplicitRequest: false,
        TrackerSerial: "LHR-TEST0001",
        Hand: ControllerHand.Left,
        ControllerRuntime: ControllerRuntimeIdentities.MetaLinkLibOvr,
        ControllerModel: controllerModel,
        MountMoved: false,
        ValidationThresholdExceeded: false,
        DriverProfile: CalibrationDriverProfiles.LtbTouch,
        ControllerIdentity: "CTRL-TEST0001");

    private static CalibrationProfile LegacyProfile() => new(
        CalibrationProfileSchema.LegacyVersion,
        "Legacy left profile",
        ControllerHand.Left,
        ControllerRuntimeIdentities.LegacyAlvr,
        "Quest 2 Touch",
        "CTRL-TEST0001",
        "LHR-TEST0001",
        ProfileCalibrationPolicy.Auto,
        ProfileCalibrationMode.RotationOnly,
        "translation unobservable; rotation-only fallback",
        new TrackerToControllerTransform(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity),
        10d,
        new CalibrationProfileQuality(1d, null, null, 0.95d),
        new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero));
}
