using Ltb.Core;
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
public sealed record InternalDriverLoadedControllerEvidence(
    string SerialNumber,
    string RuntimeBuildIdentity);

/// <summary>
/// Staged and point-in-time loaded first-party driver identity. Loaded controller
/// evidence remains absent until the exact runtime topology passes validation.
/// </summary>
public sealed record InternalDriverDriverEvidence(string StagedBuildIdentity)
{
    public InternalDriverLoadedControllerEvidence? LeftController { get; init; }

    public InternalDriverLoadedControllerEvidence? RightController { get; init; }

    public bool ExactLoadedBuildReady => LeftController is not null && RightController is not null;
}

/// <summary>
/// Stable identity and runtime metadata for the sole validated Lighthouse HMD.
/// No transient OpenVR device index is exposed as identity.
/// </summary>
public sealed record InternalDriverLighthouseHmdEvidence(
    string StableDeviceId,
    string DevicePath,
    string DriverId,
    string? TrackingSystemName,
    string? ManufacturerName,
    string? ModelNumber);

/// <summary>Calibration model selected in an exact retained schema-2 profile.</summary>
public enum InternalDriverCalibrationMode
{
    RotationOnly = 0,
    FullSixDof = 1,
}

/// <summary>Held-out calibration quality with units explicit in property names.</summary>
public sealed record InternalDriverCalibrationQualityEvidence(
    double RotationRmsDegrees,
    double? PositionRmsMillimeters,
    double? TranslationConditionNumber,
    double InlierRatio);

/// <summary>Typed evidence copied from an exact retained schema-2 profile.</summary>
public sealed record InternalDriverCalibrationEvidence(
    int SchemaVersion,
    InternalDriverCalibrationMode SelectedMode,
    string SelectionReason,
    double EstimatedLagMilliseconds,
    InternalDriverCalibrationQualityEvidence Quality,
    DateTimeOffset CreatedUtc);

/// <summary>
/// Motion and validity evidence calculated from strictly monotonic real Meta
/// pose samples. Fractions and progress values are in the range [0, 1].
/// </summary>
public sealed record InternalDriverCaptureEvidence(
    int SampleCount,
    double TrackingValidityFraction,
    double OrientationValidityFraction,
    double PositionValidityFraction,
    double MotionAxisCoverage,
    double TotalRotationDegrees,
    double RotationProgress,
    double PositionProgress,
    bool RotationReady,
    bool PositionReady);

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

/// <summary>Optional production factory tuning; every path has a zero-input default.</summary>
public sealed record InternalDriverSessionOptions
{
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
