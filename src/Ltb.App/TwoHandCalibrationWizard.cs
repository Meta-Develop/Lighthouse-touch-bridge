using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Core;

namespace Ltb.App;

public enum CalibrationWizardState
{
    Stopped,
    DependencyCheck,
    WaitingForSteamVR,
    WaitingForDevices,
    Ready,
    OverrideRelease,
    Recording,
    Association,
    TimeAlignment,
    RotationSolve,
    TranslationAttempt,
    Validation,
    ApplyProfile,
    Active,
    SafeDisable,
}

public enum CalibrationWizardHand
{
    Left,
    Right,
}

public sealed record CalibrationWizardDependencyStatus(
    bool AlvrAvailable,
    bool VmtAvailable,
    string Diagnostic,
    bool ActiveHmdReady = true)
{
    public bool IsReady => AlvrAvailable && VmtAvailable && ActiveHmdReady;
}

public sealed record CalibrationWizardDeviceSet(
    string LeftControllerSerial,
    string RightControllerSerial,
    IReadOnlyList<string> TrackerSerials)
{
    public CalibrationWizardRecalibrationObservations Recalibration { get; init; } = new();

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LeftControllerSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(RightControllerSerial);
        ArgumentNullException.ThrowIfNull(TrackerSerials);
        if (TrackerSerials.Count != 2 ||
            TrackerSerials.Any(string.IsNullOrWhiteSpace) ||
            TrackerSerials.Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new InvalidOperationException(
                "The two-hand wizard requires exactly two distinct tracker serials.");
        }

        if (string.Equals(LeftControllerSerial, RightControllerSerial, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The two-hand wizard requires distinct left and right controller capture stream identifiers.");
        }

        Recalibration.Validate(TrackerSerials);
    }
}

public sealed record CalibrationWizardRecalibrationObservations
{
    public bool ExplicitRequest { get; init; }

    public bool MountMoved { get; init; }

    public bool ValidationThresholdExceeded { get; init; }

    public string ControllerRuntime { get; init; } = "ALVR";

    public string ControllerModel { get; init; } = "Meta Touch";

    /// <summary>
    /// Current first-party driver profile identity. The deprecated ALVR/VMT
    /// path leaves this null and persists schema version 1.
    /// </summary>
    public string? DriverProfile { get; init; }

    /// <summary>
    /// Optional stable identity reported by the controller source. Public
    /// LibOVR does not provide one, so the internal Meta Link path normally
    /// leaves these null. Capture stream identifiers remain on
    /// <see cref="CalibrationWizardDeviceSet"/> and are not persisted as
    /// hardware identities.
    /// </summary>
    public string? LeftControllerIdentity { get; init; }

    public string? RightControllerIdentity { get; init; }

    public int ExpectedSchemaVersion { get; init; } =
        CalibrationProfileSchema.LegacyVersion;

    public string ExpectedTransformConvention { get; init; } =
        CalibrationProfileSchema.TransformConvention;

    public string? ObservedLeftTrackerSerial { get; init; }

    public string? ObservedRightTrackerSerial { get; init; }

    public string? ObservedTrackerSerial(CalibrationWizardHand hand) => hand switch
    {
        CalibrationWizardHand.Left => ObservedLeftTrackerSerial,
        CalibrationWizardHand.Right => ObservedRightTrackerSerial,
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };

    public string? ControllerIdentity(CalibrationWizardHand hand) => hand switch
    {
        CalibrationWizardHand.Left => LeftControllerIdentity,
        CalibrationWizardHand.Right => RightControllerIdentity,
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };

    public void Validate(IReadOnlyList<string> trackerSerials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ControllerRuntime);
        ArgumentException.ThrowIfNullOrWhiteSpace(ControllerModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExpectedTransformConvention);
        if (DriverProfile is not null && string.IsNullOrWhiteSpace(DriverProfile))
        {
            throw new InvalidOperationException(
                "The observed driver profile must be null or a non-empty identity.");
        }

        if ((LeftControllerIdentity is not null &&
             string.IsNullOrWhiteSpace(LeftControllerIdentity)) ||
            (RightControllerIdentity is not null &&
             string.IsNullOrWhiteSpace(RightControllerIdentity)))
        {
            throw new InvalidOperationException(
                "A controller identity must be null or non-empty when the source exposes one.");
        }

        if (ExpectedSchemaVersion == CalibrationProfileSchema.LegacyVersion &&
            DriverProfile is not null)
        {
            throw new InvalidOperationException(
                "Legacy schema-version-1 ALVR/VMT observations cannot specify a driver profile.");
        }

        if (ExpectedSchemaVersion == CalibrationProfileSchema.CurrentVersion &&
            !string.Equals(
                DriverProfile,
                CalibrationDriverProfiles.LtbTouch,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Schema-version-2 observations require driver profile " +
                $"'{CalibrationDriverProfiles.LtbTouch}'.");
        }

        if (ExpectedSchemaVersion <= 0)
        {
            throw new InvalidOperationException(
                "The expected profile schema version must be positive.");
        }

        foreach (var observed in new[]
                 {
                     ObservedLeftTrackerSerial,
                     ObservedRightTrackerSerial,
                 }.Where(serial => serial is not null))
        {
            if (string.IsNullOrWhiteSpace(observed) ||
                !trackerSerials.Contains(observed, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Observed hand tracker '{observed}' is not in the current tracker set.");
            }
        }
    }
}

public sealed record CalibrationWizardCapture(
    CalibrationWizardHand Hand,
    PoseRecording Recording);

public sealed record CalibrationWizardCaptureProgress(
    CalibrationWizardHand Hand,
    int SampleCount,
    double OrientationTrackingValidFraction,
    double PositionTrackingValidFraction,
    double MotionAxisCoverage,
    double TotalRotationDegrees,
    double RotationProgress,
    double PositionProgress,
    bool RotationReady,
    bool PositionReady,
    string Diagnostic)
{
    public bool CoverageAccepted => RotationReady;
}

public sealed record CalibrationWizardAssociation(
    string LeftTrackerSerial,
    string RightTrackerSerial,
    double LeftCorrelation,
    double RightCorrelation,
    bool TrackerEnumerationWasSwapped,
    string Diagnostic);

public sealed record CalibrationWizardHandAnalysis(
    CalibrationWizardHand Hand,
    string ControllerSerial,
    string TrackerSerial,
    LagEstimate Lag,
    CalibrationResult Calibration);

public sealed record CalibrationWizardAnalysis(
    CalibrationWizardAssociation Association,
    CalibrationWizardHandAnalysis Left,
    CalibrationWizardHandAnalysis Right)
{
    public IReadOnlyList<CalibrationWizardHandAnalysis> Hands => [Left, Right];

    public CalibrationWizardRecalibrationObservations Recalibration { get; init; } = new();
}

public sealed record CalibrationWizardProfileView(
    string ProfileName,
    CalibrationWizardHand Hand,
    string? ControllerIdentity,
    string TrackerSerial,
    CalibrationModel SelectedModel,
    string SelectionReason,
    RigidTransform TrackerToController,
    double EstimatedLagMilliseconds,
    CalibrationQualityMetrics Quality,
    DateTimeOffset CreatedUtc)
{
    /// <summary>
    /// Legacy application alias retained while ALVR/VMT support remains a
    /// deprecated compile-time and runtime fallback.
    /// </summary>
    public string? ControllerSerial => ControllerIdentity;

    public int SchemaVersion { get; init; } = CalibrationProfileSchema.LegacyVersion;

    public string? DriverProfile { get; init; }
}

public sealed record CalibrationWizardProfileLookup(
    IReadOnlyList<CalibrationWizardProfileView> Profiles,
    string Diagnostic)
{
    public bool HasCompletePair =>
        Profiles.Count == 2 &&
        Profiles.Select(profile => profile.Hand).Distinct().Count() == 2;
}

public interface ICalibrationWizardRuntime
{
    Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken);

    Task WaitForSteamVrAsync(CancellationToken cancellationToken);

    Task<CalibrationWizardDeviceSet> WaitForDevicesAsync(
        CancellationToken cancellationToken);

    Task ReleaseOverridesAsync(
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken);

    Task<CalibrationWizardCapture> CaptureAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        IProgress<CalibrationWizardCaptureProgress> progress,
        CancellationToken cancellationToken);

    Task ApplyProfilesAsync(
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        CancellationToken cancellationToken);
}

/// <summary>
/// Portable-calibration and profile-persistence adapter. The application state
/// machine deliberately knows no association, coverage, solver, or JSON rules.
/// </summary>
public interface ICalibrationWizardBackend
{
    CalibrationWizardProfileLookup FindReusableProfiles(
        CalibrationWizardDeviceSet devices);

    CalibrationWizardAnalysis AnalyzeFirstRun(
        CalibrationWizardDeviceSet devices,
        CalibrationWizardCapture leftCapture,
        CalibrationWizardCapture rightCapture);

    IReadOnlyList<CalibrationWizardProfileView> SaveProfiles(
        CalibrationWizardAnalysis analysis);
}

public interface ICalibrationWizardOutput
{
    void OnStateChanged(CalibrationWizardState state, string diagnostic);

    void OnCaptureProgress(CalibrationWizardCaptureProgress progress);

    void WriteLine(string message);
}

public sealed record CalibrationWizardResult(
    bool Success,
    bool ReusedProfiles,
    CalibrationWizardState FinalState,
    IReadOnlyList<CalibrationWizardState> StateHistory,
    IReadOnlyList<CalibrationWizardProfileView> Profiles,
    string Diagnostic)
{
    public bool Cancelled { get; init; }

    public IReadOnlyList<Exception> CleanupFailures { get; init; } =
        Array.Empty<Exception>();
}

internal sealed class CalibrationWizardRunException : InvalidOperationException
{
    public CalibrationWizardRunException(
        CalibrationWizardState state,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        State = state;
    }

    public CalibrationWizardState State { get; }
}

/// <summary>
/// UI-neutral Milestone 3 orchestration. A console and a future desktop UI can
/// consume the same deterministic state and progress events.
/// </summary>
public sealed class TwoHandCalibrationWizard
{
    private readonly ICalibrationWizardRuntime _runtime;
    private readonly ICalibrationWizardBackend _backend;
    private readonly ICalibrationWizardOutput _output;
    private readonly ILtbLogSink _logSink;
    private readonly TimeProvider _timeProvider;
    private readonly List<CalibrationWizardState> _history = [];
    private bool _externalCleanupRequired;

    public TwoHandCalibrationWizard(
        ICalibrationWizardRuntime runtime,
        ICalibrationWizardBackend backend,
        ICalibrationWizardOutput output,
        ILtbLogSink? logSink = null,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _logSink = logSink ?? NullLtbLogSink.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CalibrationWizardResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        _history.Clear();
        _externalCleanupRequired = false;
        try
        {
            var result = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
            return !result.Success && RequiresProductionCleanup
                ? await CompleteAbortCleanupAsync(result).ConfigureAwait(false)
                : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = new CalibrationWizardResult(
                false,
                false,
                _history.Count == 0 ? CalibrationWizardState.Stopped : _history[^1],
                _history.AsReadOnly(),
                Array.Empty<CalibrationWizardProfileView>(),
                "Wizard cancellation was requested.")
            {
                Cancelled = true,
            };
            if (RequiresProductionCleanup)
            {
                return await CompleteAbortCleanupAsync(cancelled).ConfigureAwait(false);
            }

            Transition(
                CalibrationWizardState.Ready,
                "wizard cancellation completed before capture-path override release");
            return cancelled with
            {
                FinalState = CalibrationWizardState.Ready,
                StateHistory = _history.AsReadOnly(),
            };
        }
        catch (Exception exception) when (RequiresProductionCleanup)
        {
            var failedState = _history.Count == 0
                ? CalibrationWizardState.Stopped
                : _history[^1];
            var unexpected = new CalibrationWizardResult(
                false,
                false,
                failedState,
                _history.AsReadOnly(),
                Array.Empty<CalibrationWizardProfileView>(),
                $"An unexpected production-wizard operation failed in " +
                $"{failedState}: {exception.Message}");
            return await CompleteAbortCleanupAsync(unexpected).ConfigureAwait(false);
        }
    }

    private async Task<CalibrationWizardResult> RunCoreAsync(
        CancellationToken cancellationToken)
    {
        Transition(CalibrationWizardState.DependencyCheck,
            "checking ALVR, VMT, and active Lighthouse HMD dependencies");
        CalibrationWizardDependencyStatus dependencyStatus;
        try
        {
            dependencyStatus = await _runtime
                .CheckDependenciesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailReady(
                CalibrationWizardState.DependencyCheck,
                $"Dependency checks could not complete: {exception.Message}");
        }

        if (!dependencyStatus.IsReady)
        {
            return FailReady(
                CalibrationWizardState.DependencyCheck,
                dependencyStatus.Diagnostic);
        }

        Transition(CalibrationWizardState.WaitingForSteamVR,
            "waiting for the SteamVR runtime");
        try
        {
            await _runtime.WaitForSteamVrAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailReady(
                CalibrationWizardState.WaitingForSteamVR,
                $"SteamVR did not become ready: {exception.Message}");
        }

        Transition(CalibrationWizardState.WaitingForDevices,
            "waiting for two supported Meta Touch controllers and two Lighthouse " +
            "pose-source candidates");
        CalibrationWizardDeviceSet devices;
        try
        {
            devices = await _runtime
                .WaitForDevicesAsync(cancellationToken)
                .ConfigureAwait(false);
            devices.Validate();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailReady(
                CalibrationWizardState.WaitingForDevices,
                $"The required device set is unavailable: {exception.Message}");
        }

        Transition(CalibrationWizardState.Ready,
            "dependencies and the two-hand device set are ready");
        CalibrationWizardProfileLookup reusable;
        try
        {
            reusable = _backend.FindReusableProfiles(devices);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailReady(
                CalibrationWizardState.Ready,
                $"Stored profiles could not be evaluated safely: {exception.Message}");
        }

        _output.WriteLine($"profile_lookup: {reusable.Diagnostic}");
        if (reusable.HasCompletePair)
        {
            _externalCleanupRequired = true;
            Transition(CalibrationWizardState.ApplyProfile,
                "matching serial-and-hand profiles found; capture is not required");
            try
            {
                await _runtime
                    .ApplyProfilesAsync(reusable.Profiles, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return FailReady(
                    CalibrationWizardState.ApplyProfile,
                    $"Matching profiles were loaded but could not be applied: {exception.Message}");
            }

            ReportProfiles(reusable.Profiles, "loaded");
            Transition(CalibrationWizardState.Active,
                "two matching profiles loaded and applied");
            return Complete(true, reusable.Profiles, reusable.Diagnostic);
        }

        _externalCleanupRequired = true;
        Transition(CalibrationWizardState.OverrideRelease,
            "releasing active hand overrides before original Touch pose capture");
        try
        {
            await _runtime
                .ReleaseOverridesAsync(devices, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailReady(
                CalibrationWizardState.OverrideRelease,
                $"Override release failed; capture was not started: {exception.Message}");
        }

        Transition(CalibrationWizardState.Recording,
            "move only the left controller through pitch, yaw, roll, and moderate translation");
        var progress = new InlineProgress<CalibrationWizardCaptureProgress>(
            _output.OnCaptureProgress);
        CalibrationWizardCapture leftCapture;
        CalibrationWizardCapture rightCapture;
        try
        {
            leftCapture = await _runtime
                .CaptureAsync(CalibrationWizardHand.Left, devices, progress, cancellationToken)
                .ConfigureAwait(false);
            EnsureCaptureHand(leftCapture, CalibrationWizardHand.Left);

            _output.WriteLine(
                "prompt: move only the right controller through pitch, yaw, roll, and moderate translation");
            rightCapture = await _runtime
                .CaptureAsync(CalibrationWizardHand.Right, devices, progress, cancellationToken)
                .ConfigureAwait(false);
            EnsureCaptureHand(rightCapture, CalibrationWizardHand.Right);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailReady(
                CalibrationWizardState.Recording,
                $"Guided capture failed: {exception.Message}");
        }

        Transition(CalibrationWizardState.Association,
            "correlating coordinate-invariant angular motion; device order is ignored");
        CalibrationWizardAnalysis analysis;
        try
        {
            analysis = _backend.AnalyzeFirstRun(
                devices,
                leftCapture,
                rightCapture);
        }
        catch (CalibrationWizardRunException exception)
        {
            return FailReady(exception.State, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailReady(
                CalibrationWizardState.Association,
                $"Association or calibration analysis failed: {exception.Message}");
        }

        ReportAssociation(analysis.Association);

        Transition(CalibrationWizardState.TimeAlignment,
            "reviewing independently estimated controller-stream lag per hand");
        foreach (var hand in analysis.Hands)
        {
            _output.WriteLine(
                $"lag: hand={Format(hand.Hand)} value_ms={hand.Lag.LagSeconds * 1000d:F3} correlation={hand.Lag.CorrelationScore:F4}");
        }

        Transition(CalibrationWizardState.RotationSolve,
            "reviewing independently gated rotation solutions");
        foreach (var hand in analysis.Hands)
        {
            var result = hand.Calibration;
            if (!result.Success)
            {
                return FailReady(
                    CalibrationWizardState.RotationSolve,
                    $"{Format(hand.Hand)} rotation calibration failed: {result.SelectionReason}");
            }

            _output.WriteLine(
                $"rotation: hand={Format(hand.Hand)} observable={result.Motion.RotationObservable.ToString().ToLowerInvariant()} " +
                $"coverage={result.Motion.RotationAxisCoverage:F4} rms_deg={result.Quality.RotationRmsDegrees:F4}");
        }

        Transition(CalibrationWizardState.TranslationAttempt,
            "evaluating optional translation without weakening accepted rotation");
        foreach (var hand in analysis.Hands)
        {
            var result = hand.Calibration;
            var outcome = result.SelectedModel == CalibrationModel.FullSixDof
                ? "full_6dof"
                : "rotation_only_fallback";
            _output.WriteLine(
                $"selection: hand={Format(hand.Hand)} mode={outcome} reason={result.SelectionReason}");
            LogTranslationSelection(hand);
        }

        Transition(CalibrationWizardState.Validation,
            "reporting held-out calibration quality per hand");
        foreach (var hand in analysis.Hands)
        {
            ReportQuality(hand);
        }

        IReadOnlyList<CalibrationWizardProfileView> savedProfiles;
        try
        {
            savedProfiles = _backend.SaveProfiles(analysis);
            EnsureCompleteProfiles(savedProfiles);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailReady(
                CalibrationWizardState.Validation,
                $"Validated calibration profiles could not be persisted: {exception.Message}");
        }

        Transition(CalibrationWizardState.ApplyProfile,
            "applying the two persisted tracker-local transforms and hand overrides");
        try
        {
            await _runtime
                .ApplyProfilesAsync(savedProfiles, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FailReady(
                CalibrationWizardState.ApplyProfile,
                $"Profiles were saved but could not be applied: {exception.Message}");
        }

        ReportProfiles(savedProfiles, "saved");
        Transition(CalibrationWizardState.Active,
            "two-hand calibration profiles are active");
        return Complete(false, savedProfiles, "first-run capture calibrated, saved, and applied");
    }

    private void ReportAssociation(CalibrationWizardAssociation association)
    {
        _output.WriteLine(
            $"association: hand=left tracker={association.LeftTrackerSerial} correlation={association.LeftCorrelation:F4}");
        _output.WriteLine(
            $"association: hand=right tracker={association.RightTrackerSerial} correlation={association.RightCorrelation:F4}");
        _output.WriteLine(
            $"association_tracker_enumeration_swapped: {association.TrackerEnumerationWasSwapped.ToString().ToLowerInvariant()}");
        _output.WriteLine($"association_diagnostic: {association.Diagnostic}");
    }

    private void ReportQuality(CalibrationWizardHandAnalysis hand)
    {
        var result = hand.Calibration;
        var quality = result.Quality;
        _output.WriteLine(
            $"quality_rotation: hand={Format(hand.Hand)} " +
            $"rotation_rms_deg={FormatFinite(quality.RotationRmsDegrees, "F4")} " +
            $"rotation_percentile_deg={FormatFinite(quality.RotationPercentileDegrees, "F4")} " +
            $"rotation_inlier_ratio={FormatFinite(quality.RotationInlierRatio, "F4")}");
        _output.WriteLine(
            $"quality_position: hand={Format(hand.Hand)} " +
            $"position_rms_mm={FormatOptional(quality.PositionRmsMillimeters, "F3")} " +
            $"position_percentile_mm={FormatOptional(quality.PositionPercentileMillimeters, "F3")} " +
            $"rotation_only_position_rms_mm={FormatOptional(quality.RotationOnlyPositionRmsMillimeters, "F3")}");
        _output.WriteLine(
            $"quality_translation: hand={Format(hand.Hand)} " +
            $"translation_condition={FormatFinite(result.Motion.TranslationConditionNumber, "F3")} " +
            $"translation_inlier_ratio={FormatFinite(quality.TranslationInlierRatio, "F4")} " +
            $"translation_magnitude_mm={FormatFinite(quality.TranslationMagnitudeMeters * 1000d, "F3")} " +
            $"translation_split_disagreement_mm={FormatFinite(quality.TranslationSplitDisagreementMillimeters, "F3")}");
        _output.WriteLine(
            $"observability: hand={Format(hand.Hand)} " +
            $"rotation_observable={Format(result.Motion.RotationObservable)} " +
            $"rotation_degeneracy={result.Motion.RotationDegeneracy} " +
            $"translation_observable={Format(result.Motion.TranslationObservable)} " +
            $"translation_degeneracy={result.Motion.TranslationDegeneracy} " +
            $"axis_coverage={FormatFinite(result.Motion.RotationAxisCoverage, "F4")}");
    }

    private void ReportProfiles(
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        string action)
    {
        foreach (var profile in profiles.OrderBy(profile => profile.Hand))
        {
            _output.WriteLine(
                $"profile: hand={Format(profile.Hand)} tracker={profile.TrackerSerial} " +
                $"mode={Format(profile.SelectedModel)} action={action}");
        }
    }

    private CalibrationWizardResult Complete(
        bool reusedProfiles,
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        string diagnostic) =>
        new(
            true,
            reusedProfiles,
            CalibrationWizardState.Active,
            _history.AsReadOnly(),
            profiles,
            diagnostic);

    private CalibrationWizardResult FailReady(
        CalibrationWizardState failedState,
        string diagnostic)
    {
        if (_history.Count == 0 || _history[^1] != failedState)
        {
            Transition(failedState, "the current stage rejected the operation");
        }

        if (failedState == CalibrationWizardState.RotationSolve)
        {
            Log(
                LtbLogLevel.Error,
                LtbDiagnosticCode.BadRotationCalibration,
                diagnostic);
        }

        _output.WriteLine(
            $"diagnostic: state={failedState} message={diagnostic}");
        if (RequiresProductionCleanup)
        {
            return new CalibrationWizardResult(
                false,
                false,
                failedState,
                _history.AsReadOnly(),
                Array.Empty<CalibrationWizardProfileView>(),
                diagnostic);
        }

        Transition(CalibrationWizardState.Ready,
            "calibration stopped safely; correct the diagnostic and retry");
        return new CalibrationWizardResult(
            false,
            false,
            CalibrationWizardState.Ready,
            _history.AsReadOnly(),
            Array.Empty<CalibrationWizardProfileView>(),
            diagnostic);
    }

    private bool RequiresProductionCleanup =>
        _externalCleanupRequired && _runtime is ICalibrationWizardCleanupRuntime;

    private async Task<CalibrationWizardResult> CompleteAbortCleanupAsync(
        CalibrationWizardResult result)
    {
        TransitionForCleanup(
            CalibrationWizardState.SafeDisable,
            "disabling both capture and profile override surfaces after wizard abort");
        IReadOnlyList<Exception> cleanupFailures;
        try
        {
            cleanupFailures = await ((ICalibrationWizardCleanupRuntime)_runtime)
                .SafeDisableAsync()
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailures = [exception];
        }

        if (cleanupFailures.Count == 0)
        {
            TransitionForCleanup(
                CalibrationWizardState.Ready,
                "wizard abort cleanup completed for both hands");
            return result with
            {
                FinalState = CalibrationWizardState.Ready,
                StateHistory = _history.AsReadOnly(),
                CleanupFailures = cleanupFailures,
            };
        }

        var cleanupDiagnostic =
            $"{result.Diagnostic} SafeDisable attempted both hands but reported " +
            $"{cleanupFailures.Count} cleanup failure(s); manual state inspection is required.";
        try
        {
            _output.WriteLine($"diagnostic: state=SafeDisable message={cleanupDiagnostic}");
        }
        catch
        {
            // Console/UI output failure must not erase the completed cleanup result.
        }
        return result with
        {
            FinalState = CalibrationWizardState.SafeDisable,
            StateHistory = _history.AsReadOnly(),
            Diagnostic = cleanupDiagnostic,
            CleanupFailures = cleanupFailures,
        };
    }

    private void Transition(CalibrationWizardState state, string diagnostic)
    {
        _history.Add(state);
        _output.OnStateChanged(state, diagnostic);
        Log(
            LtbLogLevel.Information,
            LtbDiagnosticCode.StateTransition,
            diagnostic,
            new Dictionary<string, string>
            {
                ["wizardState"] = state.ToString(),
            });
    }

    private void TransitionForCleanup(CalibrationWizardState state, string diagnostic)
    {
        _history.Add(state);
        try
        {
            _output.OnStateChanged(state, diagnostic);
        }
        catch
        {
            // Cleanup is authoritative even if its UI observer is unavailable.
        }

        Log(
            LtbLogLevel.Information,
            LtbDiagnosticCode.StateTransition,
            diagnostic,
            new Dictionary<string, string>
            {
                ["wizardState"] = state.ToString(),
            });
    }

    private void LogTranslationSelection(CalibrationWizardHandAnalysis hand)
    {
        var result = hand.Calibration;
        if (result.SelectedModel != CalibrationModel.RotationOnly)
        {
            return;
        }

        var code = result.Motion.TranslationDegeneracy == CalibrationDegeneracy.MissingPosition
            ? LtbDiagnosticCode.NoPositionAvailable
            : LtbDiagnosticCode.PoorTranslationObservability;
        Log(
            LtbLogLevel.Warning,
            code,
            result.SelectionReason,
            new Dictionary<string, string>
            {
                ["hand"] = Format(hand.Hand),
                ["translationDegeneracy"] = result.Motion.TranslationDegeneracy.ToString(),
                ["selectedMode"] = "rotation_only",
            });
    }

    private void Log(
        LtbLogLevel level,
        LtbDiagnosticCode code,
        string message,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        try
        {
            _logSink.Write(new LtbLogEvent(
                _timeProvider.GetUtcNow().ToUniversalTime(),
                level,
                code,
                ToRuntimeState(_history.Count == 0
                    ? CalibrationWizardState.Stopped
                    : _history[^1]),
                message,
                properties));
        }
        catch
        {
            // Local log I/O must not alter calibration or SafeDisable behavior.
        }
    }

    private static RuntimeApplicationState ToRuntimeState(CalibrationWizardState state) =>
        state switch
        {
            CalibrationWizardState.Stopped => RuntimeApplicationState.Stopped,
            CalibrationWizardState.DependencyCheck => RuntimeApplicationState.DependencyCheck,
            CalibrationWizardState.WaitingForSteamVR => RuntimeApplicationState.WaitingForSteamVR,
            CalibrationWizardState.WaitingForDevices => RuntimeApplicationState.WaitingForDevices,
            CalibrationWizardState.Ready => RuntimeApplicationState.Ready,
            CalibrationWizardState.OverrideRelease => RuntimeApplicationState.OverrideRelease,
            CalibrationWizardState.Recording => RuntimeApplicationState.Recording,
            CalibrationWizardState.Association => RuntimeApplicationState.Association,
            CalibrationWizardState.TimeAlignment => RuntimeApplicationState.TimeAlignment,
            CalibrationWizardState.RotationSolve => RuntimeApplicationState.RotationSolve,
            CalibrationWizardState.TranslationAttempt => RuntimeApplicationState.TranslationAttempt,
            CalibrationWizardState.Validation => RuntimeApplicationState.Validation,
            CalibrationWizardState.ApplyProfile => RuntimeApplicationState.ApplyProfile,
            CalibrationWizardState.Active => RuntimeApplicationState.Active,
            CalibrationWizardState.SafeDisable => RuntimeApplicationState.SafeDisable,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static void EnsureCaptureHand(
        CalibrationWizardCapture capture,
        CalibrationWizardHand expectedHand)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Hand != expectedHand)
        {
            throw new InvalidOperationException(
                $"Capture returned {capture.Hand} data while {expectedHand} was requested.");
        }
    }

    private static void EnsureCompleteProfiles(
        IReadOnlyList<CalibrationWizardProfileView> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count != 2 ||
            profiles.Select(profile => profile.Hand).Distinct().Count() != 2)
        {
            throw new InvalidOperationException(
                "The profile store did not return one persisted profile per hand.");
        }
    }

    private static string Format(CalibrationWizardHand hand) =>
        hand.ToString().ToLowerInvariant();

    private static string Format(bool value) => value.ToString().ToLowerInvariant();

    private static string FormatFinite(double value, string format) =>
        double.IsFinite(value)
            ? value.ToString(format, System.Globalization.CultureInfo.InvariantCulture)
            : "unavailable";

    private static string FormatOptional(double? value, string format) =>
        value is { } present ? FormatFinite(present, format) : "unavailable";

    private static string Format(CalibrationModel model) => model switch
    {
        CalibrationModel.FullSixDof => "full_6dof",
        CalibrationModel.RotationOnly => "rotation_only",
        _ => "failed",
    };

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report)
        {
            _report = report ?? throw new ArgumentNullException(nameof(report));
        }

        public void Report(T value) => _report(value);
    }
}

internal sealed class ConsoleCalibrationWizardOutput : ICalibrationWizardOutput
{
    private readonly TextWriter _writer;

    public ConsoleCalibrationWizardOutput(TextWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void OnStateChanged(CalibrationWizardState state, string diagnostic) =>
        _writer.WriteLine($"state: {state} ({diagnostic})");

    public void OnCaptureProgress(CalibrationWizardCaptureProgress progress) =>
        _writer.WriteLine(
            $"coverage: hand={progress.Hand.ToString().ToLowerInvariant()} samples={progress.SampleCount} " +
            $"orientation_valid={progress.OrientationTrackingValidFraction:P1} " +
            $"position_valid={progress.PositionTrackingValidFraction:P1} " +
            $"axis_coverage={progress.MotionAxisCoverage:F4} " +
            $"total_rotation_deg={progress.TotalRotationDegrees:F1} " +
            $"rotation_progress={progress.RotationProgress:F4} " +
            $"position_progress={progress.PositionProgress:F4} " +
            $"rotation_ready={progress.RotationReady.ToString().ToLowerInvariant()} " +
            $"position_ready={progress.PositionReady.ToString().ToLowerInvariant()} " +
            $"diagnostic={progress.Diagnostic}");

    public void WriteLine(string message) => _writer.WriteLine(message);
}
