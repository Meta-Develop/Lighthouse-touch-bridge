using Ltb.Core;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.OpenVr;

namespace Ltb.App;

/// <summary>Presentation-neutral state of one stopped-session prerequisite.</summary>
public enum InternalDriverPrerequisiteStatus
{
    Waiting = 0,
    Ready,
    ActionRequired,
    DeferredUntilStart,
}

/// <summary>Immutable diagnostic and remediation for one prerequisite.</summary>
public sealed record InternalDriverPrerequisite
{
    public InternalDriverPrerequisite(
        string key,
        InternalDriverPrerequisiteStatus status,
        string diagnostic,
        string remediation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        ArgumentException.ThrowIfNullOrWhiteSpace(remediation);
        Key = key;
        Status = status;
        Diagnostic = diagnostic;
        Remediation = remediation;
    }

    public string Key { get; }

    public InternalDriverPrerequisiteStatus Status { get; }

    public string Diagnostic { get; }

    public string Remediation { get; }

    public bool PermitsStart =>
        Status is InternalDriverPrerequisiteStatus.Ready or
            InternalDriverPrerequisiteStatus.DeferredUntilStart;
}

/// <summary>
/// One complete point-in-time stopped-session probe. The snapshot contains no
/// UI types and cannot begin registration, calibration, or an IPC feed.
/// </summary>
public sealed record InternalDriverPrerequisiteSnapshot
{
    private static readonly string[] GateOrder =
    [
        "platform",
        "meta-link",
        "controllers",
        "steamvr",
        "trackers",
        "driver",
        "profiles",
        "feed",
    ];

    public InternalDriverPrerequisiteSnapshot(
        bool probeCompleted,
        InternalDriverPrerequisite platform,
        InternalDriverPrerequisite metaLink,
        InternalDriverPrerequisite controllers,
        InternalDriverPrerequisite steamVr,
        InternalDriverPrerequisite trackers,
        InternalDriverPrerequisite driver,
        InternalDriverPrerequisite profiles,
        InternalDriverPrerequisite feed)
    {
        ProbeCompleted = probeCompleted;
        Platform = RequireKey(platform, "platform");
        MetaLink = RequireKey(metaLink, "meta-link");
        Controllers = RequireKey(controllers, "controllers");
        SteamVr = RequireKey(steamVr, "steamvr");
        Trackers = RequireKey(trackers, "trackers");
        Driver = RequireKey(driver, "driver");
        Profiles = RequireKey(profiles, "profiles");
        Feed = RequireKey(feed, "feed");
        Steps = Array.AsReadOnly(
        [
            Platform,
            MetaLink,
            Controllers,
            SteamVr,
            Trackers,
            Driver,
            Profiles,
            Feed,
        ]);
        CanStart = ProbeCompleted && Steps.All(step => step.PermitsStart);
        CanCalibrate = CanStart;
        StartGateReason = GateReason("Start");
        CalibrationGateReason = GateReason("Calibration");
    }

    public bool ProbeCompleted { get; }

    public InternalDriverPrerequisite Platform { get; }

    public InternalDriverPrerequisite MetaLink { get; }

    public InternalDriverPrerequisite Controllers { get; }

    public InternalDriverPrerequisite SteamVr { get; }

    public InternalDriverPrerequisite Trackers { get; }

    public InternalDriverPrerequisite Driver { get; }

    public InternalDriverPrerequisite Profiles { get; }

    public InternalDriverPrerequisite Feed { get; }

    public IReadOnlyList<InternalDriverPrerequisite> Steps { get; }

    public bool CanStart { get; }

    public bool CanCalibrate { get; }

    public string StartGateReason { get; }

    public string CalibrationGateReason { get; }

    public static InternalDriverPrerequisiteSnapshot Unprobed { get; } = new(
        probeCompleted: false,
        Waiting("platform"),
        Waiting("meta-link"),
        Waiting("controllers"),
        Waiting("steamvr"),
        Waiting("trackers"),
        Waiting("driver"),
        Waiting("profiles"),
        Waiting("feed"));

    /// <summary>
    /// Compatibility probe result for presentation test doubles that predate
    /// this boundary. Every unknown production check remains visibly deferred.
    /// </summary>
    public static InternalDriverPrerequisiteSnapshot DeferredForLegacyFactory { get; } = new(
        probeCompleted: true,
        Deferred("platform"),
        Deferred("meta-link"),
        Deferred("controllers"),
        Deferred("steamvr"),
        Deferred("trackers"),
        Deferred("driver"),
        Deferred("profiles"),
        Deferred("feed"));

    public static InternalDriverPrerequisiteSnapshot ProbeFailure(string diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        var retry = "Refresh prerequisites to retry the read-only probe.";
        return new InternalDriverPrerequisiteSnapshot(
            probeCompleted: true,
            new InternalDriverPrerequisite(
                "platform",
                InternalDriverPrerequisiteStatus.ActionRequired,
                diagnostic,
                retry),
            DeferredAfterFailure("meta-link", retry),
            DeferredAfterFailure("controllers", retry),
            DeferredAfterFailure("steamvr", retry),
            DeferredAfterFailure("trackers", retry),
            DeferredAfterFailure("driver", retry),
            DeferredAfterFailure("profiles", retry),
            DeferredAfterFailure("feed", retry));
    }

    private string GateReason(string action)
    {
        if (!ProbeCompleted)
        {
            return $"{action} is disabled until the first prerequisite probe completes.";
        }

        var byKey = Steps.ToDictionary(step => step.Key, StringComparer.Ordinal);
        foreach (var key in GateOrder)
        {
            var step = byKey[key];
            if (!step.PermitsStart)
            {
                return $"{step.Diagnostic} {step.Remediation}";
            }
        }

        var deferred = Steps
            .Where(step => step.Status == InternalDriverPrerequisiteStatus.DeferredUntilStart)
            .Select(step => step.Diagnostic)
            .ToArray();
        return deferred.Length == 0
            ? $"{action} prerequisites are ready."
            : $"{action} prerequisites are ready; deferred checks will run after Start: " +
              string.Join(" ", deferred);
    }

    private static InternalDriverPrerequisite RequireKey(
        InternalDriverPrerequisite prerequisite,
        string expectedKey)
    {
        ArgumentNullException.ThrowIfNull(prerequisite);
        if (!string.Equals(prerequisite.Key, expectedKey, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Expected prerequisite key '{expectedKey}', observed '{prerequisite.Key}'.",
                nameof(prerequisite));
        }

        return prerequisite;
    }

    private static InternalDriverPrerequisite Waiting(string key) => new(
        key,
        InternalDriverPrerequisiteStatus.Waiting,
        "Waiting for the first stopped-session prerequisite probe.",
        "Refresh prerequisites before starting.");

    private static InternalDriverPrerequisite Deferred(string key) => new(
        key,
        InternalDriverPrerequisiteStatus.DeferredUntilStart,
        $"The legacy session factory cannot probe '{key}' without Start.",
        "This check is explicitly deferred until Start.");

    private static InternalDriverPrerequisite DeferredAfterFailure(
        string key,
        string remediation) => new(
        key,
        InternalDriverPrerequisiteStatus.DeferredUntilStart,
        $"'{key}' was not evaluated because the prerequisite probe failed.",
        remediation);
}

/// <summary>Read-only stopped-session prerequisite boundary.</summary>
public interface IInternalDriverPrerequisiteProbe : IAsyncDisposable
{
    ValueTask<InternalDriverPrerequisiteSnapshot> ProbeAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// User remediation shared by the stopped-session probe and the production
/// session state machine so presentation code never forks operational policy.
/// </summary>
internal static class InternalDriverSessionRemediation
{
    public const string Platform =
        "Run the win-x64 LTB application on the SteamVR host.";
    public const string SteamVr =
        "Start SteamVR with the intended Lighthouse HMD as the sole HMD.";
    public const string MetaLink =
        "Start Quest Link or Air Link and wake both Touch controllers.";
    public const string Trackers =
        "Connect at least two distinct physical Lighthouse trackers and restore valid raw poses.";
    public const string Driver =
        "Remove disallowed SteamVR devices, verify the staged driver registration, and restart SteamVR.";
    public const string DriverRegistration =
        "Repair the staged driver and OpenVR registration, then run the session again.";
    public const string NoAction = "No remediation is required.";
}

/// <summary>Production read-only prerequisite probe.</summary>
internal sealed class InternalDriverPrerequisiteProbe : IInternalDriverPrerequisiteProbe
{
    private readonly IInternalDriverPrerequisiteRuntime _runtime;
    private bool _disposed;

    public InternalDriverPrerequisiteProbe(IInternalDriverPrerequisiteRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async ValueTask<InternalDriverPrerequisiteSnapshot> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var platform = _runtime.ProbePlatform();
        if (!platform.IsSupported)
        {
            return UnsupportedPlatform(platform);
        }

        SteamVrDriverInspection? inspection = null;
        string? inspectionFailure = null;
        try
        {
            inspection = await _runtime.InspectDriverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            inspectionFailure = exception.Message;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var observation = _runtime.Observe();
        cancellationToken.ThrowIfCancellationRequested();
        return Evaluate(platform, inspection, inspectionFailure, observation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }

    internal static InternalDriverPrerequisiteSnapshot Evaluate(
        InternalDriverPlatformProbe platform,
        SteamVrDriverInspection? inspection,
        string? inspectionFailure,
        InternalDriverRuntimeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var platformStep = Ready(
            "platform",
            platform.Diagnostic,
            platform.Remediation);
        var metaRuntimeReady = MetaRuntimeAndHeadsetReady(observation.Meta);
        var controllersReady = MetaBothReady(observation.Meta);
        var hmd = ActiveHmdReadiness.Evaluate(observation.Devices);
        var hmdCount = observation.Devices.Count(device =>
            device.Category == SteamVrDeviceCategory.HeadMountedDisplay);
        var soleHmdReady = hmd.IsReady && hmdCount == 1;
        var trackerCount = observation.TrackerSamples
            .Where(pair => IsTrackerPublishable(pair.Value))
            .Select(pair => pair.Key)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var metaDiagnostic =
            $"Meta Link readiness: left={observation.Meta.Left.Readiness} " +
            $"({observation.Meta.Left.Diagnostic}); right={observation.Meta.Right.Readiness} " +
            $"({observation.Meta.Right.Diagnostic}).";
        var controllerDiagnostic = controllersReady
            ? "Both Meta Touch controllers report current valid public input state."
            : $"Both Meta Touch controllers are not ready. {metaDiagnostic}";
        var steamVrDiagnostic = hmdCount == 1
            ? hmd.Diagnostic
            : $"{hmd.Diagnostic} SteamVR enumerated {hmdCount} HMD devices; the intended " +
              "Lighthouse HMD must be the sole HMD.";
        var steamVrStep = !observation.SteamVrRunning
            ? Action(
                "steamvr",
                observation.SteamVrDiagnostic,
                InternalDriverSessionRemediation.SteamVr)
            : soleHmdReady
                ? Ready(
                    "steamvr",
                    steamVrDiagnostic,
                    InternalDriverSessionRemediation.NoAction)
                : Action(
                    "steamvr",
                    steamVrDiagnostic,
                    InternalDriverSessionRemediation.SteamVr);
        var trackerStep = trackerCount >= 2
            ? Ready(
                "trackers",
                $"Observed {trackerCount} distinct connected valid physical Lighthouse tracker candidates.",
                InternalDriverSessionRemediation.NoAction)
            : Waiting(
                "trackers",
                $"Expected at least two distinct connected, fully tracked raw tracker poses; " +
                $"observed {trackerCount} valid physical candidate(s).",
                InternalDriverSessionRemediation.Trackers);

        return new InternalDriverPrerequisiteSnapshot(
            probeCompleted: true,
            platformStep,
            metaRuntimeReady
                ? Ready("meta-link", metaDiagnostic, InternalDriverSessionRemediation.NoAction)
                : Waiting("meta-link", metaDiagnostic, InternalDriverSessionRemediation.MetaLink),
            controllersReady
                ? Ready("controllers", controllerDiagnostic, InternalDriverSessionRemediation.NoAction)
                : Waiting("controllers", controllerDiagnostic, InternalDriverSessionRemediation.MetaLink),
            steamVrStep,
            trackerStep,
            DriverStep(inspection, inspectionFailure, observation.Devices),
            Deferred(
                "profiles",
                "Exact profile reuse or fresh calibration is resolved only after Start.",
                "This check is explicitly deferred until Start."),
            Deferred(
                "feed",
                "Same-user IPC session and heartbeat health do not exist before Start.",
                "This check is explicitly deferred until Start."));
    }

    private static InternalDriverPrerequisiteSnapshot UnsupportedPlatform(
        InternalDriverPlatformProbe platform) => new(
        probeCompleted: true,
        Action("platform", platform.Diagnostic, platform.Remediation),
        Deferred(
            "meta-link",
            "Meta Link probing is unavailable outside the supported Windows x64 host.",
            InternalDriverSessionRemediation.Platform),
        Deferred(
            "controllers",
            "Controller probing is unavailable outside the supported Windows x64 host.",
            InternalDriverSessionRemediation.Platform),
        Deferred(
            "steamvr",
            "SteamVR topology probing is unavailable outside the supported Windows x64 host.",
            InternalDriverSessionRemediation.Platform),
        Deferred(
            "trackers",
            "Lighthouse tracker probing is unavailable outside the supported Windows x64 host.",
            InternalDriverSessionRemediation.Platform),
        Deferred(
            "driver",
            "Driver probing is unavailable outside the supported Windows x64 host.",
            InternalDriverSessionRemediation.Platform),
        Deferred(
            "profiles",
            "Profile resolution is deferred until a supported Start.",
            InternalDriverSessionRemediation.Platform),
        Deferred(
            "feed",
            "IPC health is deferred until a supported Start.",
            InternalDriverSessionRemediation.Platform));

    private static InternalDriverPrerequisite DriverStep(
        SteamVrDriverInspection? inspection,
        string? inspectionFailure,
        IReadOnlyList<SteamVrDeviceDescriptor> devices)
    {
        if (inspection is null)
        {
            return Action(
                "driver",
                $"Read-only staged-driver inspection failed: " +
                $"{(string.IsNullOrWhiteSpace(inspectionFailure) ? "no diagnostic was returned." : inspectionFailure)}",
                InternalDriverSessionRemediation.DriverRegistration);
        }

        if (!inspection.IsRegistered)
        {
            return Deferred(
                "driver",
                $"Staged driver build '{inspection.StagedBuildId}' is readable but is not registered; " +
                "transactional registration is deferred until Start.",
                "Press Start once to register the staged driver; restart SteamVR when requested.");
        }

        var loaded = InternalDriverLoadedReadiness.Evaluate(devices, inspection.StagedBuildId);
        return loaded.IsReady
            ? Ready("driver", loaded.Diagnostic, InternalDriverSessionRemediation.NoAction)
            : Action("driver", loaded.Diagnostic, InternalDriverSessionRemediation.Driver);
    }

    private static bool MetaRuntimeAndHeadsetReady(MetaLinkRuntimeSnapshot meta)
    {
        static bool RuntimeUnavailable(MetaLinkReadiness readiness) => readiness is
            MetaLinkReadiness.NotInstalled or
            MetaLinkReadiness.AbiUnavailable or
            MetaLinkReadiness.RuntimeStopped or
            MetaLinkReadiness.HeadsetDisconnected or
            MetaLinkReadiness.Faulted;

        return !RuntimeUnavailable(meta.Left.Readiness) &&
               !RuntimeUnavailable(meta.Right.Readiness);
    }

    private static bool MetaBothReady(MetaLinkRuntimeSnapshot meta) =>
        MetaHandReady(meta.Left) && MetaHandReady(meta.Right);

    private static bool MetaHandReady(MetaLinkHandSnapshot hand) =>
        hand.Readiness == MetaLinkReadiness.Ready &&
        hand.Controller is { } controller &&
        controller.Analog.IsValid &&
        !controller.Battery.IsAvailable;

    private static bool IsTrackerPublishable(PoseSourceSample sample) =>
        sample.IsConnected &&
        sample.PoseSample.HasValidOrientation &&
        sample.PoseSample.HasValidPosition &&
        sample.Validity.HasFlag(PoseValidity.TrackingValid) &&
        sample.TrackingResult == PoseTrackingResult.RunningOk;

    private static InternalDriverPrerequisite Ready(
        string key,
        string diagnostic,
        string remediation) => new(
        key,
        InternalDriverPrerequisiteStatus.Ready,
        diagnostic,
        remediation);

    private static InternalDriverPrerequisite Waiting(
        string key,
        string diagnostic,
        string remediation) => new(
        key,
        InternalDriverPrerequisiteStatus.Waiting,
        diagnostic,
        remediation);

    private static InternalDriverPrerequisite Action(
        string key,
        string diagnostic,
        string remediation) => new(
        key,
        InternalDriverPrerequisiteStatus.ActionRequired,
        diagnostic,
        remediation);

    private static InternalDriverPrerequisite Deferred(
        string key,
        string diagnostic,
        string remediation) => new(
        key,
        InternalDriverPrerequisiteStatus.DeferredUntilStart,
        diagnostic,
        remediation);
}

internal interface IInternalDriverPrerequisiteRuntime : IAsyncDisposable
{
    InternalDriverPlatformProbe ProbePlatform();

    ValueTask<SteamVrDriverInspection> InspectDriverAsync(
        CancellationToken cancellationToken);

    InternalDriverRuntimeObservation Observe();
}

internal sealed class ProductionInternalDriverPrerequisiteRuntime
    : IInternalDriverPrerequisiteRuntime
{
    private readonly InternalDriverResolvedPaths _paths;
    private readonly ISteamVrDriverLifecycle _driverLifecycle;
    private readonly InternalDriverTrackerBatchSampler _trackerBatchSampler;
    private MetaLinkRuntime? _meta;
    private OpenVrSession? _openVr;
    private bool _disposed;

    public ProductionInternalDriverPrerequisiteRuntime(InternalDriverResolvedPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _driverLifecycle = SteamVrDriverLifecycle.CreateDefault(
            new ConfigurationSteamVrDriverReceiptStore(paths.DriverReceiptStorePath));
        _trackerBatchSampler = new InternalDriverTrackerBatchSampler(
            devices => _openVr!.CreateTrackedPoseBatchSource(
                devices,
                OpenVrTrackingUniverse.RawAndUncalibrated,
                predictionOffsetSeconds: 0d));
    }

    public InternalDriverPlatformProbe ProbePlatform()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return !OperatingSystem.IsWindows() || !Environment.Is64BitProcess
            ? new InternalDriverPlatformProbe(
                false,
                "The first-party internal driver requires a Windows x64 process.",
                InternalDriverSessionRemediation.Platform)
            : new InternalDriverPlatformProbe(
                true,
                "The current process is supported Windows x64.",
                InternalDriverSessionRemediation.NoAction);
    }

    public ValueTask<SteamVrDriverInspection> InspectDriverAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _driverLifecycle.InspectAsync(_paths.StagedDriverRoot, cancellationToken);
    }

    public InternalDriverRuntimeObservation Observe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _meta ??= new MetaLinkRuntime();
        var meta = _meta.Poll();
        try
        {
            _openVr ??= OpenVrSession.Open();
            var health = _openVr.GetRuntimeHealth();
            if (!health.IsRunning)
            {
                ResetOpenVr();
                return UnavailableObservation(health.Diagnostic, meta);
            }

            var devices = _openVr.EnumerateDevices();
            var candidates = devices
                .Where(device =>
                    device.Category == SteamVrDeviceCategory.GenericTracker &&
                    device.Capabilities.HasPosition &&
                    device.Capabilities.IsPhysicalPoseSourceEligible &&
                    !device.Capabilities.IsVirtualPoseSource)
                .ToArray();
            return new InternalDriverRuntimeObservation(
                SteamVrRunning: true,
                health.Diagnostic,
                meta,
                devices,
                _trackerBatchSampler.Read(candidates));
        }
        catch (OpenVrUnavailableException exception)
        {
            ResetOpenVr();
            return UnavailableObservation(exception.Message, meta);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        ResetOpenVr();
        _meta?.Dispose();
        _meta = null;
        _driverLifecycle.Dispose();
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static InternalDriverRuntimeObservation UnavailableObservation(
        string diagnostic,
        MetaLinkRuntimeSnapshot meta) => new(
        SteamVrRunning: false,
        diagnostic,
        meta,
        [],
        new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal));

    private void ResetOpenVr()
    {
        _trackerBatchSampler.Reset();
        _openVr?.Dispose();
        _openVr = null;
    }
}
