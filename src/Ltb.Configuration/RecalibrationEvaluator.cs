namespace Ltb.Configuration;

/// <summary>Machine-readable categories from specification section 20.</summary>
public enum RecalibrationTriggerKind
{
    ExplicitRequest,
    TrackerHandAssociationChanged,
    MountMoved,
    ValidationThresholdExceeded,
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

        if (!profile.MatchesController(
                context.ControllerRuntime,
                context.ControllerModel))
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.ControllerIdentityChanged,
                "The controller model or emulation runtime differs from the stored profile."));
        }

        if (!string.Equals(
                context.ExpectedTransformConvention,
                CalibrationProfileSchema.TransformConvention,
                StringComparison.Ordinal))
        {
            triggers.Add(new RecalibrationTrigger(
                RecalibrationTriggerKind.TransformConventionChanged,
                "The expected transform convention differs from the schema-1 stored convention."));
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
