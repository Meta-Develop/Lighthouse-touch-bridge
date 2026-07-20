namespace Ltb.Configuration;

/// <summary>
/// Caller-supplied target controller and driver identity for an explicit
/// schema-1 to schema-2 migration.
/// </summary>
public sealed record CalibrationProfileTargetIdentity
{
    public CalibrationProfileTargetIdentity(
        string driverProfile,
        string controllerRuntime,
        string controllerModel,
        string? controllerIdentity)
    {
        DriverProfile = CalibrationDriverProfiles.RequireSupported(
            driverProfile,
            nameof(driverProfile));
        ControllerRuntime = ProfileValidation.RequireIdentity(
            controllerRuntime,
            nameof(controllerRuntime));
        ControllerModel = ProfileValidation.RequireText(
            controllerModel,
            nameof(controllerModel));
        ControllerIdentity = ProfileValidation.OptionalIdentity(
            controllerIdentity,
            nameof(controllerIdentity));
    }

    public string DriverProfile { get; }

    public string ControllerRuntime { get; }

    public string ControllerModel { get; }

    public string? ControllerIdentity { get; }
}

/// <summary>
/// The explicit migration result retains the original legacy instance so the
/// caller can reverse the choice without reconstructing or mutating it.
/// </summary>
public sealed record CalibrationProfileMigrationResult(
    CalibrationProfile OriginalLegacyProfile,
    CalibrationProfile MigratedProfile)
{
    public CalibrationProfile Revert() => OriginalLegacyProfile;
}

/// <summary>Explicit and non-mutating calibration profile migration.</summary>
public static class CalibrationProfileMigration
{
    public static CalibrationProfileMigrationResult MigrateLegacyProfile(
        CalibrationProfile legacyProfile,
        CalibrationProfileTargetIdentity targetIdentity)
    {
        ArgumentNullException.ThrowIfNull(legacyProfile);
        ArgumentNullException.ThrowIfNull(targetIdentity);

        if (!legacyProfile.IsLegacy)
        {
            throw new ArgumentException(
                "Only an unchanged schema-version-1 profile can be migrated.",
                nameof(legacyProfile));
        }

        if (!string.Equals(
                legacyProfile.ControllerRuntime,
                targetIdentity.ControllerRuntime,
                StringComparison.Ordinal) ||
            !string.Equals(
                legacyProfile.ControllerModel,
                targetIdentity.ControllerModel,
                StringComparison.Ordinal) ||
            !string.Equals(
                legacyProfile.ControllerIdentity,
                targetIdentity.ControllerIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Calibration profile migration cannot change the controller runtime, " +
                "model, or identity. Cross-runtime reuse requires recalibration; the " +
                "original legacy profile was not modified.");
        }

        var migrated = new CalibrationProfile(
            CalibrationProfileSchema.CurrentVersion,
            legacyProfile.ProfileName,
            legacyProfile.Hand,
            targetIdentity.ControllerRuntime,
            targetIdentity.ControllerModel,
            targetIdentity.ControllerIdentity,
            legacyProfile.TrackerSerial,
            targetIdentity.DriverProfile,
            legacyProfile.CalibrationPolicy,
            legacyProfile.SelectedMode,
            legacyProfile.SelectionReason,
            legacyProfile.TrackerToController,
            legacyProfile.EstimatedLagMilliseconds,
            legacyProfile.Quality,
            legacyProfile.CreatedUtc);

        return new CalibrationProfileMigrationResult(legacyProfile, migrated);
    }
}
