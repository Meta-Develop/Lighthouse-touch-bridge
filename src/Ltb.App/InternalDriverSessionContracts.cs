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
    string Diagnostic);

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
    string Diagnostic);

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
    string remediation);

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
