namespace Ltb.Configuration;

/// <summary>Machine-readable categories from specification section 20.</summary>
public enum RecalibrationTriggerKind
{
    ExplicitRequest,
    TrackerHandAssociationChanged,
    MountMoved,
    ValidationThresholdExceeded,
    DriverProfileChanged,
    ControllerIdentityChanged,
    TransformConventionChanged,
    SchemaVersionChanged,
}

/// <summary>One structured reason requiring a new calibration.</summary>
public sealed record RecalibrationTrigger(
    RecalibrationTriggerKind Kind,
    string Message);

/// <summary>Current observations used to evaluate whether a stored profile is reusable.</summary>
public sealed record RecalibrationContext(
    bool ExplicitRequest,
    string TrackerSerial,
    ControllerHand Hand,
    string ControllerRuntime,
    string ControllerModel,
    bool MountMoved,
    bool ValidationThresholdExceeded,
    string? DriverProfile = null,
    string? ControllerIdentity = null,
    int ExpectedSchemaVersion = CalibrationProfileSchema.CurrentVersion,
    string ExpectedTransformConvention = CalibrationProfileSchema.TransformConvention);

/// <summary>The complete, structured recalibration decision.</summary>
public sealed record RecalibrationEvaluation
{
    public RecalibrationEvaluation(IEnumerable<RecalibrationTrigger> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        Triggers = Array.AsReadOnly(triggers.ToArray());
    }

    public bool IsRequired => Triggers.Count > 0;

    public IReadOnlyList<RecalibrationTrigger> Triggers { get; }
}

/// <summary>Evaluates all specification-defined recalibration trigger categories.</summary>
public static class RecalibrationEvaluator
{
    public static RecalibrationEvaluation Evaluate(
        CalibrationProfile profile,
        RecalibrationContext context)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(context);

        var trackerSerial = ProfileValidation.RequireIdentity(context.TrackerSerial, nameof(context.TrackerSerial));
        ProfileValidation.RequireDefined(context.Hand, nameof(context.Hand));
        var driverProfile = context.DriverProfile is null
            ? null
            : CalibrationDriverProfiles.RequireSupported(
                context.DriverProfile,
                nameof(context.DriverProfile));
        var controllerRuntime = ProfileValidation.RequireIdentity(
            context.ControllerRuntime,
            nameof(context.ControllerRuntime));
        var controllerModel = ProfileValidation.RequireText(
            context.ControllerModel,
            nameof(context.ControllerModel));
        var controllerIdentity = ProfileValidation.OptionalIdentity(
            context.ControllerIdentity,
            nameof(context.ControllerIdentity));
        ProfileValidation.RequireText(context.ExpectedTransformConvention, nameof(context.ExpectedTransformConvention));

        var triggers = new List<RecalibrationTrigger>();
        if (context.ExplicitRequest)
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.ExplicitRequest,
                "The user explicitly requested recalibration."));
        }

        if (context.Hand != profile.Hand ||
            !string.Equals(trackerSerial, profile.TrackerSerial, StringComparison.Ordinal))
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.TrackerHandAssociationChanged,
                "The current tracker serial and hand association does not match the stored profile."));
        }

        if (context.MountMoved)
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.MountMoved,
                "The tracker-to-controller mount was reported as physically moved."));
        }

        if (context.ValidationThresholdExceeded)
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.ValidationThresholdExceeded,
                "The current validation check exceeded its configured threshold."));
        }

        var driverProfileChanged = profile.IsLegacy
            ? context.ExpectedSchemaVersion != CalibrationProfileSchema.LegacyVersion ||
              driverProfile is not null
            : driverProfile is null ||
              !string.Equals(driverProfile, profile.DriverProfile, StringComparison.Ordinal);
        if (driverProfileChanged)
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.DriverProfileChanged,
                "The current driver profile differs from, or is absent in, the stored profile."));
        }

        var controllerIdentityChanged =
            !string.Equals(controllerRuntime, profile.ControllerRuntime, StringComparison.Ordinal) ||
            !string.Equals(controllerModel, profile.ControllerModel, StringComparison.Ordinal) ||
            (profile.IsLegacy
                ? profile.ControllerIdentity is not null &&
                  !string.Equals(
                      controllerIdentity,
                      profile.ControllerIdentity,
                      StringComparison.Ordinal)
                : !string.Equals(
                    controllerIdentity,
                    profile.ControllerIdentity,
                    StringComparison.Ordinal));
        if (controllerIdentityChanged)
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.ControllerIdentityChanged,
                "The controller runtime, model, or optional runtime identity differs from the stored profile."));
        }

        if (!string.Equals(
                context.ExpectedTransformConvention,
                CalibrationProfileSchema.TransformConvention,
                StringComparison.Ordinal))
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.TransformConventionChanged,
                "The expected transform convention differs from the stored profile convention."));
        }

        if (context.ExpectedSchemaVersion != profile.SchemaVersion)
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.SchemaVersionChanged,
                "The expected profile schema version differs from the stored profile."));
        }

        return new RecalibrationEvaluation(triggers);
    }
}
