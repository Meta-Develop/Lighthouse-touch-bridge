using Ltb.Core;
using Ltb.Configuration;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.OpenVr;
using Ltb.Protocol;

namespace Ltb.App;

/// <summary>The complete first-party application state vocabulary from specification section 18.</summary>
public enum InternalDriverSessionState
{
    Stopped = 0,
    DependencyCheck,
    WaitingForSteamVR,
    WaitingForMetaLink,
    WaitingForTrackers,
    WaitingForDriver,
    Ready,
    Recording,
    Association,
    TimeAlignment,
    RotationSolve,
    TranslationAttempt,
    Validation,
    SaveProfile,
    StartingFeed,
    Active,
    Reconnecting,
    Faulted,
}

/// <summary>Why one hand is currently neutral instead of publishable.</summary>
public enum InternalDriverNeutralReason
{
    None = 0,
    SessionStopped,
    DependencyUnavailable,
    SteamVrStopped,
    MetaNotReady,
    TrackerMissing,
    TrackerDisconnected,
    TrackerPoseInvalid,
    TrackerTopologyInvalid,
    ProfileUnavailable,
    DriverNotReady,
    FeedUnavailable,
    FeedReconnecting,
    Stopping,
    Faulted,
}

/// <summary>Profile status reported independently for each hand.</summary>
public enum InternalDriverProfileReadiness
{
    Missing = 0,
    Reused,
    Calibrated,
    Incompatible,
}

/// <summary>
/// Typed manual-binding verification presented without allowing correlation
/// to silently replace the owner-selected pair.
/// </summary>
public enum InternalDriverManualBindingVerificationState
{
    Agreement = 0,
    MismatchCorrectionCandidate,
    CorrelationFailed,
}

/// <summary>
/// Authoritative manual pair plus an optional correlation-derived correction
/// candidate. Every serial uses the stored uppercase canonical form.
/// </summary>
public sealed record InternalDriverManualBindingVerificationEvidence
{
    public InternalDriverManualBindingVerificationEvidence(
        InternalDriverManualBindingVerificationState state,
        string leftTrackerSerial,
        string rightTrackerSerial,
        string diagnostic,
        string? correctionLeftTrackerSerial = null,
        string? correctionRightTrackerSerial = null,
        string? authorityGeneration = null)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        State = state;
        LeftTrackerSerial = CanonicalSerial(leftTrackerSerial, nameof(leftTrackerSerial));
        RightTrackerSerial = CanonicalSerial(rightTrackerSerial, nameof(rightTrackerSerial));
        RequireDistinct(LeftTrackerSerial, RightTrackerSerial, nameof(rightTrackerSerial));
        Diagnostic = InternalDriverEvidenceValidation.RequireNonblank(
            diagnostic,
            nameof(diagnostic));
        if (authorityGeneration is not null &&
            (authorityGeneration.Length != 64 ||
             authorityGeneration.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException(
                "Manual-binding authority generation must be a SHA-256 value.",
                nameof(authorityGeneration));
        }

        AuthorityGeneration = authorityGeneration?.ToUpperInvariant();
        if ((correctionLeftTrackerSerial is null) !=
            (correctionRightTrackerSerial is null))
        {
            throw new ArgumentException(
                "A correction candidate must contain both left and right serials.",
                nameof(correctionRightTrackerSerial));
        }

        if (correctionLeftTrackerSerial is not null)
        {
            CorrectionLeftTrackerSerial = CanonicalSerial(
                correctionLeftTrackerSerial,
                nameof(correctionLeftTrackerSerial));
            CorrectionRightTrackerSerial = CanonicalSerial(
                correctionRightTrackerSerial!,
                nameof(correctionRightTrackerSerial));
            RequireDistinct(
                CorrectionLeftTrackerSerial,
                CorrectionRightTrackerSerial,
                nameof(correctionRightTrackerSerial));
        }

        if (state == InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate &&
            CorrectionLeftTrackerSerial is null)
        {
            throw new ArgumentException(
                "A mismatch requires a complete correction candidate.",
                nameof(correctionLeftTrackerSerial));
        }

        if (state != InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate &&
            CorrectionLeftTrackerSerial is not null)
        {
            throw new ArgumentException(
                "Only a mismatch may carry a correction candidate.",
                nameof(correctionLeftTrackerSerial));
        }
    }

    public InternalDriverManualBindingVerificationState State { get; }

    public string LeftTrackerSerial { get; }

    public string RightTrackerSerial { get; }

    public string? CorrectionLeftTrackerSerial { get; }

    public string? CorrectionRightTrackerSerial { get; }

    public string Diagnostic { get; }

    /// <summary>
    /// Optional exact generation of the authoritative pre-session settings
    /// bytes observed before capture. Production evidence always supplies it.
    /// </summary>
    public string? AuthorityGeneration { get; }

    public bool RequiresDecision =>
        State == InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate;

    private static string CanonicalSerial(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim().ToUpperInvariant();
    }

    private static void RequireDistinct(string left, string right, string parameterName)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Left and right tracker serials must be distinct.",
                parameterName);
        }
    }
}

public enum InternalDriverManualBindingDecision
{
    RetainManualBinding = 0,
    AcceptCorrectionCandidate,
}

/// <summary>One loaded first-party controller's stable serial and runtime build marker.</summary>
public sealed record InternalDriverLoadedControllerEvidence
{
    public InternalDriverLoadedControllerEvidence(
        string serialNumber,
        string runtimeBuildIdentity)
    {
        SerialNumber = InternalDriverEvidenceValidation.RequireNonblank(
            serialNumber,
            nameof(serialNumber));
        RuntimeBuildIdentity = InternalDriverEvidenceValidation.RequireNonblank(
            runtimeBuildIdentity,
            nameof(runtimeBuildIdentity));
    }

    public string SerialNumber { get; }

    public string RuntimeBuildIdentity { get; }
}

/// <summary>
/// Staged and point-in-time loaded first-party driver identity. Loaded controller
/// evidence remains absent until the exact runtime topology passes validation.
/// </summary>
public sealed record InternalDriverDriverEvidence
{
    public InternalDriverDriverEvidence(
        string stagedBuildIdentity,
        InternalDriverLoadedControllerEvidence? leftController = null,
        InternalDriverLoadedControllerEvidence? rightController = null)
    {
        StagedBuildIdentity = InternalDriverEvidenceValidation.RequireNonblank(
            stagedBuildIdentity,
            nameof(stagedBuildIdentity));
        if ((leftController is null) != (rightController is null))
        {
            throw new ArgumentException(
                "Loaded controller evidence must be absent for both hands or present for both hands.");
        }

        if (leftController is not null)
        {
            RequireController(
                leftController,
                InternalDriverLoadedReadiness.LeftControllerSerial,
                StagedBuildIdentity,
                nameof(leftController));
            RequireController(
                rightController!,
                InternalDriverLoadedReadiness.RightControllerSerial,
                StagedBuildIdentity,
                nameof(rightController));
        }

        LeftController = leftController;
        RightController = rightController;
    }

    public string StagedBuildIdentity { get; }

    public InternalDriverLoadedControllerEvidence? LeftController { get; }

    public InternalDriverLoadedControllerEvidence? RightController { get; }

    public bool ExactLoadedBuildReady => LeftController is not null && RightController is not null;

    private static void RequireController(
        InternalDriverLoadedControllerEvidence controller,
        string expectedSerial,
        string expectedBuild,
        string parameterName)
    {
        if (!string.Equals(controller.SerialNumber, expectedSerial, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Loaded controller evidence must use exact serial '{expectedSerial}'.",
                parameterName);
        }

        if (!string.Equals(
                controller.RuntimeBuildIdentity,
                expectedBuild,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Loaded controller runtime build must exactly match the staged build identity.",
                parameterName);
        }
    }
}

/// <summary>
/// Stable identity and runtime metadata for the sole validated Lighthouse HMD.
/// No transient OpenVR device index is exposed as identity.
/// </summary>
public sealed record InternalDriverLighthouseHmdEvidence
{
    public InternalDriverLighthouseHmdEvidence(
        string stableDeviceId,
        string devicePath,
        string? driverId,
        string? trackingSystemName,
        string? manufacturerName,
        string? modelNumber)
        : this(
            stableDeviceId,
            devicePath,
            driverId,
            trackingSystemName,
            actualTrackingSystemName: null,
            manufacturerName,
            modelNumber)
    {
    }

    public InternalDriverLighthouseHmdEvidence(
        string stableDeviceId,
        string devicePath,
        string? driverId,
        string? trackingSystemName,
        string? actualTrackingSystemName,
        string? manufacturerName,
        string? modelNumber)
    {
        StableDeviceId = InternalDriverEvidenceValidation.RequireNonblank(
            stableDeviceId,
            nameof(stableDeviceId));
        DevicePath = InternalDriverEvidenceValidation.RequireNonblank(
            devicePath,
            nameof(devicePath));
        DriverId = InternalDriverEvidenceValidation.RequireOptionalNonblank(
            driverId,
            nameof(driverId));
        TrackingSystemName = InternalDriverEvidenceValidation.RequireOptionalNonblank(
            trackingSystemName,
            nameof(trackingSystemName));
        ActualTrackingSystemName = InternalDriverEvidenceValidation.RequireOptionalNonblank(
            actualTrackingSystemName,
            nameof(actualTrackingSystemName));
        if (DriverId is null &&
            TrackingSystemName is null &&
            ActualTrackingSystemName is null)
        {
            throw new ArgumentException(
                "Lighthouse HMD evidence requires a driver id, tracking-system name, " +
                "or actual-tracking-system name.",
                nameof(actualTrackingSystemName));
        }
        ManufacturerName = InternalDriverEvidenceValidation.RequireOptionalNonblank(
            manufacturerName,
            nameof(manufacturerName));
        ModelNumber = InternalDriverEvidenceValidation.RequireOptionalNonblank(
            modelNumber,
            nameof(modelNumber));
    }

    public string StableDeviceId { get; }

    public string DevicePath { get; }

    public string? DriverId { get; }

    public string? TrackingSystemName { get; }

    public string? ActualTrackingSystemName { get; }

    public string? ManufacturerName { get; }

    public string? ModelNumber { get; }
}

/// <summary>Calibration model selected in a reusable first-party profile.</summary>
public enum InternalDriverCalibrationMode
{
    RotationOnly = 0,
    FullSixDof = 1,
}

/// <summary>Held-out calibration quality with units explicit in property names.</summary>
public sealed record InternalDriverCalibrationQualityEvidence
{
    public InternalDriverCalibrationQualityEvidence(
        double rotationRmsDegrees,
        double? positionRmsMillimeters,
        double? translationConditionNumber,
        double inlierRatio)
    {
        RotationRmsDegrees = InternalDriverEvidenceValidation.RequireFiniteNonnegative(
            rotationRmsDegrees,
            nameof(rotationRmsDegrees));
        PositionRmsMillimeters = InternalDriverEvidenceValidation.RequireOptionalFiniteNonnegative(
            positionRmsMillimeters,
            nameof(positionRmsMillimeters));
        TranslationConditionNumber = InternalDriverEvidenceValidation.RequireOptionalFiniteNonnegative(
            translationConditionNumber,
            nameof(translationConditionNumber));
        InlierRatio = InternalDriverEvidenceValidation.RequireUnitInterval(
            inlierRatio,
            nameof(inlierRatio));
    }

    public double RotationRmsDegrees { get; }

    public double? PositionRmsMillimeters { get; }

    public double? TranslationConditionNumber { get; }

    public double InlierRatio { get; }
}

/// <summary>Typed evidence copied from an exact retained schema-2 or schema-3 profile.</summary>
public sealed record InternalDriverCalibrationEvidence
{
    public InternalDriverCalibrationEvidence(
        int schemaVersion,
        InternalDriverCalibrationMode selectedMode,
        string selectionReason,
        double estimatedLagMilliseconds,
        InternalDriverCalibrationQualityEvidence quality,
        DateTimeOffset createdUtc,
        double? leverArmMagnitudeMillimeters = null)
    {
        if (schemaVersion is not
                CalibrationProfileSchema.DriverProfileVersion and not
                CalibrationProfileSchema.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Calibration evidence requires reusable schema " +
                $"{CalibrationProfileSchema.DriverProfileVersion} or " +
                $"{CalibrationProfileSchema.CurrentVersion}.");
        }

        if (!Enum.IsDefined(selectedMode))
        {
            throw new ArgumentOutOfRangeException(nameof(selectedMode));
        }

        if (!double.IsFinite(estimatedLagMilliseconds))
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedLagMilliseconds));
        }

        if (createdUtc == default)
        {
            throw new ArgumentException("Calibration creation time must be set.", nameof(createdUtc));
        }

        var validatedQuality = quality ?? throw new ArgumentNullException(nameof(quality));
        if (selectedMode == InternalDriverCalibrationMode.FullSixDof &&
            (validatedQuality.PositionRmsMillimeters is null ||
             validatedQuality.TranslationConditionNumber is null))
        {
            throw new ArgumentException(
                "Full-6DoF calibration evidence requires position RMS and translation-condition metrics.",
                nameof(quality));
        }

        SchemaVersion = schemaVersion;
        SelectedMode = selectedMode;
        SelectionReason = InternalDriverEvidenceValidation.RequireNonblank(
            selectionReason,
            nameof(selectionReason));
        EstimatedLagMilliseconds = estimatedLagMilliseconds;
        Quality = validatedQuality;
        CreatedUtc = createdUtc.ToUniversalTime();
        LeverArmMagnitudeMillimeters =
            InternalDriverEvidenceValidation.RequireOptionalFiniteNonnegative(
                leverArmMagnitudeMillimeters,
                nameof(leverArmMagnitudeMillimeters));
    }

    public int SchemaVersion { get; }

    public InternalDriverCalibrationMode SelectedMode { get; }

    public string SelectionReason { get; }

    public double EstimatedLagMilliseconds { get; }

    public InternalDriverCalibrationQualityEvidence Quality { get; }

    public DateTimeOffset CreatedUtc { get; }

    /// <summary>
    /// Magnitude of the calibrated tracker-to-controller translation for a
    /// full-6DoF profile. Rotation-only and older compatibility evidence leave
    /// this absent rather than treating conventional zero translation as a
    /// measured lever arm.
    /// </summary>
    public double? LeverArmMagnitudeMillimeters { get; }
}

/// <summary>
/// Motion and validity evidence calculated from strictly monotonic real Meta
/// pose samples. Fractions and progress values are in the range [0, 1].
/// </summary>
public sealed record InternalDriverCaptureEvidence
{
    public InternalDriverCaptureEvidence(
        int sampleCount,
        double trackingValidityFraction,
        double orientationValidityFraction,
        double positionValidityFraction,
        double motionAxisCoverage,
        double totalRotationDegrees,
        double rotationProgress,
        double positionProgress,
        bool rotationReady,
        bool positionReady)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        SampleCount = sampleCount;
        TrackingValidityFraction = InternalDriverEvidenceValidation.RequireUnitInterval(
            trackingValidityFraction,
            nameof(trackingValidityFraction));
        OrientationValidityFraction = InternalDriverEvidenceValidation.RequireUnitInterval(
            orientationValidityFraction,
            nameof(orientationValidityFraction));
        PositionValidityFraction = InternalDriverEvidenceValidation.RequireUnitInterval(
            positionValidityFraction,
            nameof(positionValidityFraction));
        MotionAxisCoverage = InternalDriverEvidenceValidation.RequireUnitInterval(
            motionAxisCoverage,
            nameof(motionAxisCoverage));
        TotalRotationDegrees = InternalDriverEvidenceValidation.RequireFiniteNonnegative(
            totalRotationDegrees,
            nameof(totalRotationDegrees));
        RotationProgress = InternalDriverEvidenceValidation.RequireUnitInterval(
            rotationProgress,
            nameof(rotationProgress));
        PositionProgress = InternalDriverEvidenceValidation.RequireUnitInterval(
            positionProgress,
            nameof(positionProgress));
        if (rotationReady != (rotationProgress == 1d))
        {
            throw new ArgumentException(
                "Rotation readiness must exactly match complete rotation progress.",
                nameof(rotationReady));
        }

        if (positionReady != (positionProgress == 1d))
        {
            throw new ArgumentException(
                "Position readiness must exactly match complete position progress.",
                nameof(positionReady));
        }

        if (sampleCount == 0 &&
            (trackingValidityFraction != 0d ||
             orientationValidityFraction != 0d ||
             positionValidityFraction != 0d ||
             motionAxisCoverage != 0d ||
             totalRotationDegrees != 0d ||
             rotationProgress != 0d ||
             positionProgress != 0d ||
             rotationReady ||
             positionReady))
        {
            throw new ArgumentException("An empty capture cannot contain motion or readiness evidence.");
        }

        RotationReady = rotationReady;
        PositionReady = positionReady;
    }

    public int SampleCount { get; }

    public double TrackingValidityFraction { get; }

    public double OrientationValidityFraction { get; }

    public double PositionValidityFraction { get; }

    public double MotionAxisCoverage { get; }

    public double TotalRotationDegrees { get; }

    public double RotationProgress { get; }

    public double PositionProgress { get; }

    public bool RotationReady { get; }

    public bool PositionReady { get; }

    internal static InternalDriverCaptureEvidence Empty { get; } = new(
        0,
        0d,
        0d,
        0d,
        0d,
        0d,
        0d,
        0d,
        rotationReady: false,
        positionReady: false);
}

internal static class InternalDriverEvidenceValidation
{
    public static string RequireNonblank(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    public static string? RequireOptionalNonblank(string? value, string parameterName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        }

        return value;
    }

    public static double RequireFiniteNonnegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static double? RequireOptionalFiniteNonnegative(double? value, string parameterName) =>
        value is { } present
            ? RequireFiniteNonnegative(present, parameterName)
            : null;

    public static double RequireUnitInterval(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

/// <summary>Typed readiness conjunction for the first-party production path.</summary>
public sealed record InternalDriverSessionReadiness(
    bool PlatformSupported,
    bool SteamVrRunning,
    bool MetaBothHandsReady,
    bool TwoDistinctTrackersReady,
    bool ProfilesReady,
    bool DriverRegistered,
    bool DriverLoaded,
    bool FeedReady)
{
    public bool CanPublish =>
        PlatformSupported &&
        SteamVrRunning &&
        MetaBothHandsReady &&
        TwoDistinctTrackersReady &&
        ProfilesReady &&
        DriverRegistered &&
        DriverLoaded &&
        FeedReady;

    internal static InternalDriverSessionReadiness Empty { get; } = new(
        PlatformSupported: false,
        SteamVrRunning: false,
        MetaBothHandsReady: false,
        TwoDistinctTrackersReady: false,
        ProfilesReady: false,
        DriverRegistered: false,
        DriverLoaded: false,
        FeedReady: false);
}

/// <summary>Typed health for one composed controller hand.</summary>
public sealed record InternalDriverHandSnapshot(
    ProtocolHand Hand,
    string? TrackerSerial,
    bool TrackerConnected,
    bool TrackerTracked,
    MetaLinkReadiness MetaReadiness,
    bool MetaInputsValid,
    InternalDriverProfileReadiness ProfileReadiness,
    TimeSpan? PoseAge,
    bool IsPublishing,
    InternalDriverNeutralReason NeutralReason,
    string Diagnostic)
{
    /// <summary>
    /// Exact retained compatible schema-2 or schema-3 profile evidence, when
    /// one exists for this run.
    /// </summary>
    public InternalDriverCalibrationEvidence? Calibration { get; init; }

    /// <summary>Latest real guided-capture evidence for this hand in this run.</summary>
    public InternalDriverCaptureEvidence? Capture { get; init; }
}

/// <summary>Managed feed ordering, freshness, and reconnect evidence.</summary>
public sealed record InternalDriverFeedSnapshot(
    DriverFeedReadiness Readiness,
    ProtocolSessionId? SessionId,
    ulong? LastSuccessfulSequence,
    TimeSpan? LastSuccessfulSendAge,
    TimeSpan? LastSuccessfulHeartbeatAge,
    int ReconnectAttempts,
    string? LastError)
{
    internal static InternalDriverFeedSnapshot Stopped { get; } = new(
        DriverFeedReadiness.Stopped,
        SessionId: null,
        LastSuccessfulSequence: null,
        LastSuccessfulSendAge: null,
        LastSuccessfulHeartbeatAge: null,
        ReconnectAttempts: 0,
        LastError: null);
}

/// <summary>
/// Managed-loop timing measured at application boundaries. These values are
/// software-observed lower bounds and do not include device, compositor,
/// display, or motion-to-photon latency.
/// </summary>
public sealed record InternalDriverTimingSnapshot
{
    public InternalDriverTimingSnapshot(
        TimeSpan? iterationInterval,
        TimeSpan observeDuration,
        TimeSpan pairPublicationDuration,
        TimeSpan? leftTrackerHostIngressAgeAtPublish,
        TimeSpan? rightTrackerHostIngressAgeAtPublish,
        int? observedTrackerCount)
    {
        IterationInterval = RequireOptionalNonnegative(
            iterationInterval,
            nameof(iterationInterval));
        ObserveDuration = RequireNonnegative(observeDuration, nameof(observeDuration));
        PairPublicationDuration = RequireNonnegative(
            pairPublicationDuration,
            nameof(pairPublicationDuration));
        LeftTrackerHostIngressAgeAtPublish = RequireOptionalNonnegative(
            leftTrackerHostIngressAgeAtPublish,
            nameof(leftTrackerHostIngressAgeAtPublish));
        RightTrackerHostIngressAgeAtPublish = RequireOptionalNonnegative(
            rightTrackerHostIngressAgeAtPublish,
            nameof(rightTrackerHostIngressAgeAtPublish));
        if (observedTrackerCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedTrackerCount));
        }

        ObservedTrackerCount = observedTrackerCount;
        IsSoftwareLowerBound = true;
    }

    public TimeSpan? IterationInterval { get; }

    public TimeSpan ObserveDuration { get; }

    public TimeSpan PairPublicationDuration { get; }

    public TimeSpan? LeftTrackerHostIngressAgeAtPublish { get; }

    public TimeSpan? RightTrackerHostIngressAgeAtPublish { get; }

    public int? ObservedTrackerCount { get; }

    public bool IsSoftwareLowerBound { get; }

    private static TimeSpan RequireNonnegative(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    private static TimeSpan? RequireOptionalNonnegative(
        TimeSpan? value,
        string parameterName) =>
        value is { } present ? RequireNonnegative(present, parameterName) : null;
}

/// <summary>Immutable point-in-time session state for UI, CLI, tests, and structured output.</summary>
public sealed record InternalDriverSessionSnapshot(
    InternalDriverSessionState State,
    InternalDriverSessionReadiness Readiness,
    InternalDriverHandSnapshot Left,
    InternalDriverHandSnapshot Right,
    InternalDriverFeedSnapshot Feed,
    bool RestartRequired,
    string Diagnostic,
    string Remediation)
{
    /// <summary>Staged and exact loaded first-party controller build evidence.</summary>
    public InternalDriverDriverEvidence? Driver { get; init; }

    /// <summary>The sole validated active Lighthouse HMD, identified without an OpenVR index.</summary>
    public InternalDriverLighthouseHmdEvidence? LighthouseHmd { get; init; }

    /// <summary>Additive application-observed timing evidence for the current run.</summary>
    public InternalDriverTimingSnapshot? Timing { get; init; }

    /// <summary>
    /// Additive exact-two tracker-path snapshot/restore evidence from the
    /// production or test runtime capability.
    /// </summary>
    public InternalDriverTrackerNeutralizationSnapshot? TrackerNeutralization { get; init; }

    /// <summary>
    /// Latest motion-correlation verification of an authoritative manual
    /// binding. A mismatch is an explicit decision surface, never reassignment.
    /// </summary>
    public InternalDriverManualBindingVerificationEvidence?
        ManualBindingVerification { get; init; }

    internal static InternalDriverSessionSnapshot Initial { get; } = new(
        InternalDriverSessionState.Stopped,
        InternalDriverSessionReadiness.Empty,
        EmptyHand(ProtocolHand.Left),
        EmptyHand(ProtocolHand.Right),
        InternalDriverFeedSnapshot.Stopped,
        RestartRequired: false,
        "Internal-driver session is stopped.",
        "Run the internal-driver session to begin dependency checks.");

    private static InternalDriverHandSnapshot EmptyHand(ProtocolHand hand) => new(
        hand,
        TrackerSerial: null,
        TrackerConnected: false,
        TrackerTracked: false,
        MetaLinkReadiness.RuntimeStopped,
        MetaInputsValid: false,
        InternalDriverProfileReadiness.Missing,
        PoseAge: null,
        IsPublishing: false,
        InternalDriverNeutralReason.SessionStopped,
        "No active hand session.");
}

/// <summary>Minimal stable first-party application boundary.</summary>
public interface IInternalDriverSession : IAsyncDisposable
{
    event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged;

    InternalDriverSessionSnapshot CurrentSnapshot { get; }

    Task RunAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional control-plane capability for atomically replacing one hand's
/// effective mount while a session is running.
/// </summary>
public interface IInternalDriverEffectiveMountControl
{
    void UpdateEffectiveMount(ProtocolHand hand, RigidTransform trackerFromController);
}

/// <summary>App-authoritative per-hand mount-adjustment state.</summary>
public sealed record InternalDriverMountAdjustmentHandSnapshot(
    RigidTransform BaseMount,
    MountAdjustment AppliedAdjustments,
    MountAdjustment SavedAdjustments,
    RigidTransform EffectiveMount);

/// <summary>
/// Immutable mount-adjustment state. Revision advances when adjustment values
/// change and when lifecycle teardown retires availability. Status-only
/// snapshots do not advance it, so they cannot overwrite a same-revision GUI
/// edit buffer; delayed operation acknowledgements cannot overwrite teardown.
/// </summary>
public sealed record InternalDriverMountAdjustmentSnapshot(
    long Revision,
    bool IsAvailable,
    InternalDriverMountAdjustmentHandSnapshot Left,
    InternalDriverMountAdjustmentHandSnapshot Right)
{
    public static InternalDriverMountAdjustmentSnapshot Unavailable { get; } = new(
        Revision: 0,
        IsAvailable: false,
        new(
            RigidTransform.Identity,
            MountAdjustment.Identity,
            MountAdjustment.Identity,
            RigidTransform.Identity),
        new(
            RigidTransform.Identity,
            MountAdjustment.Identity,
            MountAdjustment.Identity,
            RigidTransform.Identity));
}

public sealed record InternalDriverMountAdjustmentResult(
    long AcknowledgedRevision,
    bool Succeeded,
    string Diagnostic,
    InternalDriverMountAdjustmentSnapshot Snapshot);

/// <summary>
/// Concrete App control plane for live effective mounts and explicit profile
/// persistence. Apply never writes the profile store; Save is the only
/// adjustment persistence operation.
/// </summary>
public interface IInternalDriverMountAdjustmentControl
{
    event EventHandler<InternalDriverMountAdjustmentSnapshot>? MountAdjustmentChanged;

    InternalDriverMountAdjustmentSnapshot CurrentMountAdjustment { get; }

    ValueTask<InternalDriverMountAdjustmentResult> ApplyMountAdjustmentAsync(
        long revision,
        ProtocolHand hand,
        MountAdjustment adjustment,
        CancellationToken cancellationToken = default);

    ValueTask<InternalDriverMountAdjustmentResult> SaveMountAdjustmentsAsync(
        long revision,
        MountAdjustment left,
        MountAdjustment right,
        CancellationToken cancellationToken = default);
}

/// <summary>Semantic hand set selected for an explicit calibration request.</summary>
[Flags]
public enum InternalDriverCalibrationHandSet
{
    None = 0,
    Left = 1,
    Right = 2,
    Both = Left | Right,
}

/// <summary>How a newly created first-party session resolves calibration profiles.</summary>
public enum InternalDriverSessionIntent
{
    /// <summary>Reuse an exact matching profile pair when one is available.</summary>
    NormalStart = 0,

    /// <summary>Bypass reusable profiles and perform a fresh two-hand capture.</summary>
    Calibrate,

    /// <summary>Explicitly recalibrate only the left hand.</summary>
    CalibrateLeft,

    /// <summary>Explicitly recalibrate only the right hand.</summary>
    CalibrateRight,
}

/// <summary>Optional production factory tuning; every path has a zero-input default.</summary>
public sealed record InternalDriverSessionOptions
{
    public InternalDriverSessionIntent Intent { get; init; } =
        InternalDriverSessionIntent.NormalStart;

    /// <summary>
    /// Hand set used by <see cref="InternalDriverSessionIntent.Calibrate"/>.
    /// The legacy explicit calibration intent therefore remains a both-hand
    /// request unless a caller deliberately selects one hand.
    /// </summary>
    public InternalDriverCalibrationHandSet CalibrationHands { get; init; } =
        InternalDriverCalibrationHandSet.Both;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    public TimeSpan GuidedCaptureDurationPerHand { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan ShutdownOperationTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    public string? LocalApplicationDataRoot { get; init; }

    public string? SettingsPath { get; init; }

    public string? CalibrationProfileStorePath { get; init; }

    /// <summary>
    /// Optional private durable store for exact tracker paths observed from
    /// live OpenVR enumeration. The production factory supplies a per-user
    /// default when omitted.
    /// </summary>
    public string? TrackerPathObservationStorePath { get; init; }

    public string? StagedDriverRoot { get; init; }

    public string? StructuredLogPath { get; init; }

    /// <summary>
    /// Optional prior active selected-hand keys used when a remount removes the
    /// old tracker from the current roster. These are application-owned stable
    /// serial observations, not editable OpenVR device indexes.
    /// </summary>
    public string? PreviousLeftTrackerSerial { get; init; }

    public string? PreviousRightTrackerSerial { get; init; }

    internal InternalDriverCalibrationHandSet RequestedCalibrationHands => Intent switch
    {
        InternalDriverSessionIntent.NormalStart => InternalDriverCalibrationHandSet.None,
        InternalDriverSessionIntent.Calibrate => CalibrationHands,
        InternalDriverSessionIntent.CalibrateLeft => InternalDriverCalibrationHandSet.Left,
        InternalDriverSessionIntent.CalibrateRight => InternalDriverCalibrationHandSet.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(Intent)),
    };

    internal void Validate()
    {
        if (!Enum.IsDefined(Intent))
        {
            throw new ArgumentOutOfRangeException(nameof(Intent));
        }

        if ((CalibrationHands & ~InternalDriverCalibrationHandSet.Both) != 0 ||
            Intent == InternalDriverSessionIntent.Calibrate &&
            CalibrationHands == InternalDriverCalibrationHandSet.None)
        {
            throw new ArgumentOutOfRangeException(nameof(CalibrationHands));
        }

        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }

        if (GuidedCaptureDurationPerHand <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(GuidedCaptureDurationPerHand));
        }

        if (ShutdownOperationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ShutdownOperationTimeout));
        }

        ValidateOptionalPath(LocalApplicationDataRoot, nameof(LocalApplicationDataRoot));
        ValidateOptionalPath(SettingsPath, nameof(SettingsPath));
        ValidateOptionalPath(CalibrationProfileStorePath, nameof(CalibrationProfileStorePath));
        ValidateOptionalPath(
            TrackerPathObservationStorePath,
            nameof(TrackerPathObservationStorePath));
        ValidateOptionalPath(StagedDriverRoot, nameof(StagedDriverRoot));
        ValidateOptionalPath(StructuredLogPath, nameof(StructuredLogPath));
        ValidateOptionalIdentity(
            PreviousLeftTrackerSerial,
            nameof(PreviousLeftTrackerSerial));
        ValidateOptionalIdentity(
            PreviousRightTrackerSerial,
            nameof(PreviousRightTrackerSerial));
    }

    private static void ValidateOptionalPath(string? path, string parameterName)
    {
        if (path is not null && string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Optional paths cannot be empty or whitespace.", parameterName);
        }
    }

    private static void ValidateOptionalIdentity(string? value, string parameterName)
    {
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        }
    }
}

internal readonly record struct InternalDriverPlatformProbe(
    bool IsSupported,
    string Diagnostic,
    string Remediation);

internal sealed record InternalDriverRegistration(
    bool IsRegistered,
    bool Changed,
    bool RestartRequired,
    string StagedBuildIdentity,
    string Diagnostic);

internal sealed record InternalDriverRuntimeObservation(
    bool SteamVrRunning,
    string SteamVrDiagnostic,
    MetaLinkRuntimeSnapshot Meta,
    IReadOnlyList<SteamVrDeviceDescriptor> Devices,
    IReadOnlyDictionary<string, PoseSourceSample> TrackerSamples);

internal static class InternalDriverTrackerSerial
{
    internal static string Require(string trackerSerial, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackerSerial, parameterName);
        return trackerSerial.Trim().ToUpperInvariant();
    }
}

internal sealed record InternalDriverHandProfile(
    ProtocolHand Hand,
    string TrackerSerial,
    RigidTransform TrackerFromController,
    InternalDriverProfileReadiness Readiness,
    string Diagnostic)
{
    public InternalDriverCalibrationEvidence? Calibration { get; init; }

    public MountAdjustment MountAdjustment { get; init; } = MountAdjustment.Identity;

    public CalibrationProfile? SourceProfile { get; init; }

    public RigidTransform EffectiveTrackerFromController =>
        CoordinateConventions.ComposeEffectiveMount(
            TrackerFromController,
            MountAdjustment);
}

internal sealed record InternalDriverProfilePair(
    InternalDriverHandProfile Left,
    InternalDriverHandProfile Right)
{
    public InternalDriverManualBindingVerificationEvidence?
        ManualBindingVerification { get; init; }

    public bool IsValid =>
        Left.Hand == ProtocolHand.Left &&
        Right.Hand == ProtocolHand.Right &&
        !string.Equals(
            Left.TrackerSerial,
            Right.TrackerSerial,
            StringComparison.OrdinalIgnoreCase) &&
        Left.TrackerFromController.IsValid &&
        Right.TrackerFromController.IsValid;
}

internal delegate void InternalDriverProgress(
    InternalDriverSessionState state,
    string diagnostic,
    string remediation,
    InternalDriverCaptureEvidence? leftCapture = null,
    InternalDriverCaptureEvidence? rightCapture = null,
    InternalDriverRuntimeObservation? observation = null);

internal interface IInternalDriverSessionRuntime : IAsyncDisposable
{
    InternalDriverPlatformProbe Probe();

    ValueTask<InternalDriverRegistration> EnsureDriverAsync(
        CancellationToken cancellationToken);

    InternalDriverRuntimeObservation Observe();

    ValueTask<InternalDriverProfilePair> ResolveProfilesAsync(
        InternalDriverRuntimeObservation observation,
        InternalDriverProgress progress,
        CancellationToken cancellationToken);

    InternalDriverProfilePair SaveMountAdjustments(
        InternalDriverProfilePair profiles,
        MountAdjustment left,
        MountAdjustment right) =>
        throw new NotSupportedException(
            "This runtime does not provide calibration-profile adjustment persistence.");

    IDriverFeed CreateFeed();

    void ResetMeta();

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);

    ulong GetMonotonicNanoseconds();

    ValueTask StopRunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional production capability that durably records only the exact selected
/// physical tracker pair observed during a successful live OpenVR session.
/// </summary>
internal interface IInternalDriverTrackerPathObservationRuntime
{
    void RecordSelectedTrackerPaths(
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths,
        DateTimeOffset observedAtUtc);
}

internal interface IInternalDriverSessionOutput : IDisposable
{
    void Write(InternalDriverSessionSnapshot snapshot);
}

internal sealed class NullInternalDriverSessionOutput : IInternalDriverSessionOutput
{
    public static NullInternalDriverSessionOutput Instance { get; } = new();

    private NullInternalDriverSessionOutput()
    {
    }

    public void Write(InternalDriverSessionSnapshot snapshot) =>
        ArgumentNullException.ThrowIfNull(snapshot);

    public void Dispose()
    {
    }
}
