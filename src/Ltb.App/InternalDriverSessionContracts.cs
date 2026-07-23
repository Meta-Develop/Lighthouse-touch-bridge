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

/// <summary>Calibration model selected in an exact retained schema-2 profile.</summary>
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

/// <summary>Typed evidence copied from an exact retained schema-2 profile.</summary>
public sealed record InternalDriverCalibrationEvidence
{
    public InternalDriverCalibrationEvidence(
        int schemaVersion,
        InternalDriverCalibrationMode selectedMode,
        string selectionReason,
        double estimatedLagMilliseconds,
        InternalDriverCalibrationQualityEvidence quality,
        DateTimeOffset createdUtc)
    {
        if (schemaVersion != CalibrationProfileSchema.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                $"Calibration evidence requires schema {CalibrationProfileSchema.CurrentVersion}.");
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
    }

    public int SchemaVersion { get; }

    public InternalDriverCalibrationMode SelectedMode { get; }

    public string SelectionReason { get; }

    public double EstimatedLagMilliseconds { get; }

    public InternalDriverCalibrationQualityEvidence Quality { get; }

    public DateTimeOffset CreatedUtc { get; }
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
    /// <summary>Exact retained schema-2 profile evidence, when one exists for this run.</summary>
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

/// <summary>How a newly created first-party session resolves calibration profiles.</summary>
public enum InternalDriverSessionIntent
{
    /// <summary>Reuse an exact matching profile pair when one is available.</summary>
    NormalStart = 0,

    /// <summary>Bypass reusable profiles and perform a fresh two-hand capture.</summary>
    Calibrate,
}

/// <summary>Optional production factory tuning; every path has a zero-input default.</summary>
public sealed record InternalDriverSessionOptions
{
    public InternalDriverSessionIntent Intent { get; init; } =
        InternalDriverSessionIntent.NormalStart;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    public TimeSpan GuidedCaptureDurationPerHand { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan ShutdownOperationTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    public string? LocalApplicationDataRoot { get; init; }

    public string? SettingsPath { get; init; }

    public string? CalibrationProfileStorePath { get; init; }

    public string? StagedDriverRoot { get; init; }

    public string? StructuredLogPath { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(Intent))
        {
            throw new ArgumentOutOfRangeException(nameof(Intent));
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
        ValidateOptionalPath(StagedDriverRoot, nameof(StagedDriverRoot));
        ValidateOptionalPath(StructuredLogPath, nameof(StructuredLogPath));
    }

    private static void ValidateOptionalPath(string? path, string parameterName)
    {
        if (path is not null && string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Optional paths cannot be empty or whitespace.", parameterName);
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

internal sealed record InternalDriverHandProfile(
    ProtocolHand Hand,
    string TrackerSerial,
    RigidTransform TrackerFromController,
    InternalDriverProfileReadiness Readiness,
    string Diagnostic)
{
    public InternalDriverCalibrationEvidence? Calibration { get; init; }
}

internal sealed record InternalDriverProfilePair(
    InternalDriverHandProfile Left,
    InternalDriverHandProfile Right)
{
    public bool IsValid =>
        Left.Hand == ProtocolHand.Left &&
        Right.Hand == ProtocolHand.Right &&
        !string.Equals(Left.TrackerSerial, Right.TrackerSerial, StringComparison.Ordinal) &&
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

    IDriverFeed CreateFeed();

    void ResetMeta();

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);

    ulong GetMonotonicNanoseconds();

    ValueTask StopRunAsync(CancellationToken cancellationToken);
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
