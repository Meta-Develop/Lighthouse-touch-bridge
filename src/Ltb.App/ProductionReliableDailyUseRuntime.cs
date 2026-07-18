using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Ltb.Alvr;
using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Core;
using Ltb.OpenVr;
using Ltb.Vmt;

namespace Ltb.App;

/// <summary>
/// Live Windows adapter for the Milestone 4 later-run coordinator. One instance
/// owns one OpenVR session lifecycle and one VMT response pump for both hands.
/// Every profile-application handle captures the current transient descriptors;
/// reconnect creates new handles after stable-serial matching succeeds again.
/// Mutable state (_applications, _currentDevices, _session, _lastAlvrSnapshot,
/// _lastAlvrProbeTimestamp, _vmtStarted) is unsynchronized: an instance must
/// only be driven from one logical async flow at a time. The wizard path shares
/// one instance between the wizard run and the subsequent monitor lease, but
/// strictly sequentially, never concurrently.
/// </summary>
internal sealed class ProductionReliableDailyUseRuntime :
    IReliableDailyUseRuntime,
    IProductionCalibrationWizardBackend,
    IAsyncDisposable
{
    private static readonly TimeSpan DependencyProbeTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AlvrProbeRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VmtDiscoveryTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VmtDiscoveryPollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly PoseValidity RequiredPoseValidity =
        PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid;

    private readonly string _profileStorePath;
    private readonly IAlvrAvailabilityProbe _alvr;
    private readonly IDisposable? _ownedAlvrProbe;
    private readonly VmtClientOneHandBridgeController _vmt;
    private readonly SteamVrSettingsManager _settings;
    private readonly VmtDeviceAddress _leftSlot;
    private readonly VmtDeviceAddress _rightSlot;
    private readonly TimeSpan _staleAfter;
    private readonly TimeSpan _retryDelay;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, ApplicationState> _applications = [];

    private OpenVrSession? _session;
    private AlvrAvailabilitySnapshot? _lastAlvrSnapshot;
    private long _lastAlvrProbeTimestamp;
    private IReadOnlyList<SteamVrDeviceDescriptor> _currentDevices =
        Array.Empty<SteamVrDeviceDescriptor>();
    private bool _vmtStarted;
    private bool _disposed;

    public ProductionReliableDailyUseRuntime(
        string profileStorePath,
        string steamVrSettingsPath,
        VmtDeviceAddress leftSlot,
        VmtDeviceAddress rightSlot,
        TimeSpan staleAfter,
        TimeSpan retryDelay,
        TimeProvider? timeProvider = null,
        IAlvrAvailabilityProbe? alvrProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileStorePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(steamVrSettingsPath);
        if (leftSlot == rightSlot)
        {
            throw new ArgumentException("Left and right hands require distinct VMT slots.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryDelay, TimeSpan.Zero);

        _profileStorePath = Path.GetFullPath(profileStorePath);
        _settings = new SteamVrSettingsManager(steamVrSettingsPath);
        _leftSlot = leftSlot;
        _rightSlot = rightSlot;
        _staleAfter = staleAfter;
        _retryDelay = retryDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (alvrProbe is null)
        {
            var productionAlvrProbe = new AlvrLocalDashboardProbe();
            _alvr = productionAlvrProbe;
            _ownedAlvrProbe = productionAlvrProbe;
        }
        else
        {
            _alvr = alvrProbe;
        }

        UdpVmtDatagramTransport transport;
        try
        {
            transport = new UdpVmtDatagramTransport(
                new IPEndPoint(IPAddress.Loopback, UdpVmtDatagramTransport.DefaultDriverPort),
                new IPEndPoint(IPAddress.Loopback, UdpVmtDatagramTransport.DefaultResponsePort));
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(
                $"Cannot bind VMT response port {UdpVmtDatagramTransport.DefaultResponsePort} on loopback. " +
                "Close VMT Manager or the other response-port owner, then retry.",
                exception);
        }

        _vmt = new VmtClientOneHandBridgeController(
            new VmtClient(transport, OneHandBridgeCoordinator.VmtHeartbeatTimeout));
    }

    public async Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_vmtStarted)
        {
            await _vmt.StartAsync(cancellationToken).ConfigureAwait(false);
            _vmtStarted = true;
        }

        var alvr = await ProbeAlvrAsync(cancellationToken).ConfigureAwait(false);

        if (_session is null)
        {
            try
            {
                _session = OpenVrSession.Open();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new CalibrationWizardDependencyStatus(
                    AlvrAvailable: alvr.IsAvailable,
                    VmtAvailable: true,
                    $"{alvr.Diagnostic}. The loopback VMT client and response pump " +
                    $"initialized, but the active Lighthouse HMD cannot be verified " +
                    $"until SteamVR opens: {exception.Message}",
                    ActiveHmdReady: false);
            }
        }

        var session = _session;
        ActiveHmdReadinessResult activeHmd;
        try
        {
            var runtimeHealth = session.GetRuntimeHealth();
            if (!runtimeHealth.IsRunning)
            {
                throw new ReliableDailyUseSteamVrStoppedException(
                    "SteamVR stopped before active Lighthouse HMD readiness could be " +
                    $"verified: {runtimeHealth.Diagnostic}");
            }

            activeHmd = ActiveHmdReadiness.Evaluate(session.EnumerateDevices());
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and
            not ReliableDailyUseSteamVrStoppedException)
        {
            activeHmd = new ActiveHmdReadinessResult(
                false,
                "Active SteamVR display HMD readiness could not be inspected safely: " +
                exception.Message);
        }

        var vmtAvailable = false;
        string vmtDiagnostic;
        try
        {
            await _vmt.WaitForAliveAsync(DependencyProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
            vmtAvailable = true;
            vmtDiagnostic = "VMT Alive heartbeat is fresh";
        }
        catch (TimeoutException)
        {
            vmtDiagnostic = "VMT Alive heartbeat has not been observed yet";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            vmtDiagnostic = $"VMT dependency check failed: {exception.Message}";
        }

        return new CalibrationWizardDependencyStatus(
            AlvrAvailable: alvr.IsAvailable,
            VmtAvailable: vmtAvailable,
            $"{alvr.Diagnostic}. {vmtDiagnostic}. {activeHmd.Diagnostic} Connected " +
            "supported Meta Touch identity properties are mandatory before Ready.",
            ActiveHmdReady: activeHmd.IsReady);
    }

    public async Task WaitForSteamVrAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session is not null)
            {
                var health = _session.GetRuntimeHealth();
                if (health.IsRunning)
                {
                    return;
                }

                throw new ReliableDailyUseSteamVrStoppedException(
                    $"SteamVR stopped after this daily-use run acquired its session: " +
                    health.Diagnostic);
            }

            try
            {
                _session = OpenVrSession.Open();
                return;
            }
            catch (OpenVrUnavailableException exception) when (
                exception.Reason == OpenVrUnavailableReason.RuntimeInitializationFailed)
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<DailyUseDeviceReadiness> ProbeDeviceReadinessAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var session = Session;
        cancellationToken.ThrowIfCancellationRequested();
        var alvr = await ProbeAlvrAsync(cancellationToken).ConfigureAwait(false);
        if (!alvr.IsAvailable)
        {
            return DailyUseDeviceReadiness.Unavailable(
                LtbDiagnosticCode.DependencyUnavailable,
                alvr.Diagnostic);
        }

        var runtimeHealth = session.GetRuntimeHealth();
        if (!runtimeHealth.IsRunning)
        {
            throw new ReliableDailyUseSteamVrStoppedException(
                $"SteamVR stopped while waiting for devices: {runtimeHealth.Diagnostic}");
        }

        var devices = session.EnumerateDevices();
        var activeHmd = ActiveHmdReadiness.Evaluate(devices);
        if (!activeHmd.IsReady)
        {
            return DailyUseDeviceReadiness.Unavailable(
                LtbDiagnosticCode.DependencyUnavailable,
                activeHmd.Diagnostic);
        }

        if (!TryCreateDeviceSet(devices, out var deviceSet, out var deviceDiagnostic))
        {
            return DailyUseDeviceReadiness.Unavailable(
                LtbDiagnosticCode.DevicesUnavailable,
                deviceDiagnostic);
        }

        try
        {
            await _vmt.WaitForAliveAsync(DependencyProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!_vmt.IsAlive)
            {
                return DailyUseDeviceReadiness.Unavailable(
                    LtbDiagnosticCode.VmtUnavailable,
                    "VMT Alive heartbeat was not fresh after the bounded readiness probe.");
            }
        }
        catch (TimeoutException exception)
        {
            return DailyUseDeviceReadiness.Unavailable(
                LtbDiagnosticCode.VmtUnavailable,
                exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DailyUseDeviceReadiness.Unavailable(
                LtbDiagnosticCode.VmtUnavailable,
                $"VMT readiness probe failed: {exception.Message}");
        }

        _currentDevices = devices.ToArray();
        return DailyUseDeviceReadiness.Ready(
            deviceSet!,
            "Compatible Meta Touch roles, Lighthouse pose-source candidates, and VMT " +
            "heartbeat are ready.");
    }

    public async Task DeactivateWizardVmtAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(devices);
        var slot = hand == CalibrationWizardHand.Left ? _leftSlot : _rightSlot;
        var followSerial = devices.TrackerSerials.Count > 0
            ? devices.TrackerSerials[0]
            : throw new InvalidOperationException(
                "Wizard VMT cleanup requires at least one discovered tracker serial.");
        await _vmt.DeactivateAsync(
                new VmtDeviceConfiguration(slot, followSerial, RigidTransform.Identity),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task ReleaseWizardHandOverrideAsync(
        CalibrationWizardHand hand,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var slot = hand == CalibrationWizardHand.Left ? _leftSlot : _rightSlot;
        _ = _settings.ReleaseApplicationSafetyOverrides(
            new TrackingOverrideBinding(
                slot.DevicePath,
                hand == CalibrationWizardHand.Left
                    ? TrackingOverrideBinding.LeftHandPath
                    : TrackingOverrideBinding.RightHandPath));
        return Task.CompletedTask;
    }

    public Task VerifyOriginalTouchPosesAsync(
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(devices);
        cancellationToken.ThrowIfCancellationRequested();
        var current = Session.EnumerateDevices();
        foreach (var hand in Enum.GetValues<CalibrationWizardHand>())
        {
            var controller = SelectWizardController(current, devices, hand);
            var source = Session.CreateInputControllerPoseSource(controller);
            if (source.Device != controller)
            {
                throw new InvalidOperationException(
                    $"The {hand} recorder source does not retain the discovered original " +
                    "Touch device descriptor.");
            }

            EnsureHealthyOriginalTouchPose(source.ReadPose(), hand);
        }

        _currentDevices = current.ToArray();
        return Task.CompletedTask;
    }

    public Task<CalibrationWizardCapture> CaptureWizardHandAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        double durationSeconds,
        double sampleRateHz,
        IProgress<CalibrationWizardCaptureProgress> progress,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(progress);
        var current = Session.EnumerateDevices();
        var controller = SelectWizardController(current, devices, hand);
        var controllerSource = Session.CreateInputControllerPoseSource(controller);
        var trackerSources = devices.TrackerSerials
            .Select(serial => Session.CreateTrackedPoseSource(
                SelectCurrentTracker(current, serial)))
            .ToArray();
        var controllerSamples = new List<TimestampedPoseSample>();
        var reportEvery = Math.Max(1, (int)Math.Round(sampleRateHz / 5d));
        var lastReportedCount = 0;
        var capture = PoseRecordingCapture.Capture(
            trackerSources,
            [controllerSource],
            durationSeconds,
            sampleRateHz,
            new StopwatchRecordingCaptureClock(),
            cancellationToken,
            tick =>
            {
                var sample = tick.Samples.Single(observation =>
                    observation.Identity.SourceKind == PoseSourceKind.InputController);
                controllerSamples.Add(sample.Sample.PoseSample);
                if (controllerSamples.Count - lastReportedCount < reportEvery)
                {
                    return;
                }

                ReportWizardCoverage(hand, controllerSamples, progress);
                lastReportedCount = controllerSamples.Count;
            });
        if (controllerSamples.Count != lastReportedCount)
        {
            ReportWizardCoverage(hand, controllerSamples, progress);
        }

        _currentDevices = current.ToArray();
        return Task.FromResult(new CalibrationWizardCapture(hand, capture.Recording));
    }

    public DailyUseProfileApplication CreateProfileApplication(
        CalibrationWizardProfileView profile)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);
        var application = new DailyUseProfileApplication(profile);
        var tracker = SelectCurrentTracker(_currentDevices, profile.TrackerSerial);
        var controller = SelectCurrentController(_currentDevices, profile);
        var slot = profile.Hand == CalibrationWizardHand.Left ? _leftSlot : _rightSlot;
        var configuration = new VmtDeviceConfiguration(
            slot,
            profile.TrackerSerial,
            profile.TrackerToController);
        var cleanupBinding = new TrackingOverrideBinding(
            slot.DevicePath,
            profile.Hand == CalibrationWizardHand.Left
                ? TrackingOverrideBinding.LeftHandPath
                : TrackingOverrideBinding.RightHandPath);
        _applications.Add(
            application.OperationId,
            new ApplicationState(
                tracker,
                controller,
                configuration,
                cleanupBinding));
        return application;
    }

    public async Task ApplyProfileAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var state = State(application);
        var session = Session;

        // Remove both source-centric and hand-centric mappings before the slot
        // can become an inactive pose source. If a later step fails, rollback
        // consumes the recovery point and repeats this safety release before the
        // shared transaction is allowed to deactivate VMT.
        state.SettingsRecoveryPoints.Add(ReleaseApplicationSafetyOverrides(state));
        await _vmt.DeactivateAsync(state.Configuration, cancellationToken)
            .ConfigureAwait(false);

        var devices = session.EnumerateDevices();
        EnsureCurrentDescriptor(devices, state.Tracker, "physical tracker");
        EnsureCurrentDescriptor(devices, state.Controller, "Touch input controller");
        var trackerSource = session.CreateTrackedPoseSource(state.Tracker);
        EnsureHealthyPoseSample(trackerSource.ReadPose(), "Physical tracker");

        await _vmt.WaitForAliveAsync(
                OneHandBridgeCoordinator.VmtHeartbeatTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureHealthyPoseSample(trackerSource.ReadPose(), "Physical tracker");
        if (!_vmt.IsAlive)
        {
            throw new InvalidOperationException(
                "VMT heartbeat was not fresh immediately before activation.");
        }

        await _vmt.ActivateAsync(state.Configuration, cancellationToken)
            .ConfigureAwait(false);
        var vmtDevice = await WaitForConnectedVmtDeviceAsync(
                state.Configuration.Device,
                cancellationToken)
            .ConfigureAwait(false);
        var binding = new TrackingOverrideBinding(
            vmtDevice.Identity.DevicePath,
            state.CleanupBinding.SemanticHandPath);
        var vmtSource = session.CreateTrackedPoseSource(vmtDevice);
        var trackerSample = trackerSource.ReadPose();
        var vmtSample = vmtSource.ReadPose();
        EnsureHealthyPoseSample(trackerSample, "Physical tracker");
        EnsureHealthyPoseSample(vmtSample, "VMT pose source");
        VmtPoseMatchSafety.EnsureVmtPoseMatchesMount(
            trackerSample,
            vmtSample,
            application.Profile.TrackerToController,
            static message => new InvalidOperationException(message));
        var postActivationDevices = session.EnumerateDevices();
        EnsureCurrentDescriptor(postActivationDevices, state.Tracker, "physical tracker");
        EnsureCurrentDescriptor(postActivationDevices, state.Controller, "Touch input controller");
        EnsureCurrentDescriptor(postActivationDevices, vmtDevice, "VMT pose source");
        if (!_vmt.IsAlive)
        {
            throw new InvalidOperationException(
                "VMT heartbeat became stale before TrackingOverrides could be enabled.");
        }

        state.ActiveBinding = binding;
        state.SettingsRecoveryPoints.Add(_settings.EnableOverride(binding));
        state.TrackerSource = trackerSource;
        state.VmtDevice = vmtDevice;
        state.VmtSource = vmtSource;
    }

    public async Task DeactivateProfileAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var state = State(application);
        await _vmt.DeactivateAsync(state.Configuration, cancellationToken)
            .ConfigureAwait(false);
        _applications.Remove(application.OperationId);
    }

    public Task RollbackProfileOverrideAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var state = State(application);
        var failures = new List<Exception>();
        for (var index = state.SettingsRecoveryPoints.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _settings.Rollback(state.SettingsRecoveryPoints[index]);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        state.SettingsRecoveryPoints.Clear();
        try
        {
            _ = ReleaseApplicationSafetyOverrides(state);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more profile-application settings recovery points could not be " +
                "rolled back, or the semantic hand could not be confirmed released.",
                failures);
        }

        return Task.CompletedTask;
    }

    public Task ReleaseProfileOverrideAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var state = State(application);
        _ = ReleaseApplicationSafetyOverrides(state);
        state.SettingsRecoveryPoints.Clear();
        return Task.CompletedTask;
    }

    public async Task<RuntimeHealthSnapshot> CheckHealthAsync(
        IReadOnlyList<DailyUseProfileApplication> activeApplications,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(activeApplications);
        cancellationToken.ThrowIfCancellationRequested();
        var alvr = await ProbeAlvrAsync(cancellationToken).ConfigureAwait(false);
        if (!alvr.IsAvailable)
        {
            return new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.TouchInputLost,
                $"ALVR input runtime became unavailable: {alvr.Diagnostic}");
        }

        return await CheckOpenVrHealthAsync(activeApplications, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AlvrAvailabilitySnapshot> ProbeAlvrAsync(
        CancellationToken cancellationToken)
    {
        if (_lastAlvrSnapshot is not null &&
            _timeProvider.GetElapsedTime(_lastAlvrProbeTimestamp) <
                AlvrProbeRefreshInterval)
        {
            return _lastAlvrSnapshot;
        }

        var snapshot = await _alvr.ProbeAsync(cancellationToken).ConfigureAwait(false);
        _lastAlvrSnapshot = snapshot;
        _lastAlvrProbeTimestamp = _timeProvider.GetTimestamp();
        return snapshot;
    }

    private Task<RuntimeHealthSnapshot> CheckOpenVrHealthAsync(
        IReadOnlyList<DailyUseProfileApplication> activeApplications,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(activeApplications);
        cancellationToken.ThrowIfCancellationRequested();
        if (_session is null)
        {
            return Task.FromResult(new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.SteamVrStopped,
                "The shared OpenVR session is not connected."));
        }

        try
        {
            var runtimeHealth = _session.GetRuntimeHealth();
            if (!runtimeHealth.IsRunning)
            {
                return Task.FromResult(new RuntimeHealthSnapshot(
                    RuntimeHealthFailureKind.SteamVrStopped,
                    runtimeHealth.Diagnostic));
            }
        }
        catch (Exception exception)
        {
            return Task.FromResult(new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.SteamVrStopped,
                $"SteamVR runtime health check failed: {exception.Message}"));
        }

        try
        {
            if (!_vmt.IsAlive)
            {
                return Task.FromResult(new RuntimeHealthSnapshot(
                    RuntimeHealthFailureKind.VmtUnavailable,
                    "VMT heartbeat became stale or unavailable."));
            }
        }
        catch (Exception exception)
        {
            return Task.FromResult(new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.VmtUnavailable,
                $"VMT heartbeat monitor failed: {exception.Message}"));
        }

        IReadOnlyList<SteamVrDeviceDescriptor> devices;
        try
        {
            devices = _session.EnumerateDevices();
        }
        catch (Exception exception)
        {
            return Task.FromResult(new RuntimeHealthSnapshot(
                RuntimeHealthFailureKind.SteamVrStopped,
                $"SteamVR device enumeration failed: {exception.Message}"));
        }

        foreach (var application in activeApplications)
        {
            var state = State(application);
            var hand = application.Profile.Hand.ToString().ToLowerInvariant();
            if (!DescriptorIsCurrent(devices, state.Tracker))
            {
                return Task.FromResult(new RuntimeHealthSnapshot(
                    RuntimeHealthFailureKind.TrackerLost,
                    $"The {hand} tracker disconnected, disappeared, or changed transient identity.",
                    hand,
                    state.Tracker.StableDeviceId));
            }

            if (!DescriptorIsCurrent(devices, state.Controller))
            {
                return Task.FromResult(new RuntimeHealthSnapshot(
                    RuntimeHealthFailureKind.TouchInputLost,
                    $"The {hand} Touch input device disconnected, disappeared, or changed role.",
                    hand,
                    state.Controller.StableDeviceId));
            }

            if (state.VmtDevice is null || state.TrackerSource is null || state.VmtSource is null ||
                !DescriptorIsCurrent(devices, state.VmtDevice))
            {
                return Task.FromResult(new RuntimeHealthSnapshot(
                    RuntimeHealthFailureKind.VmtUnavailable,
                    $"The {hand} VMT pose source is unavailable.",
                    hand,
                    state.VmtDevice?.StableDeviceId));
            }

            try
            {
                var trackerSample = state.TrackerSource.ReadPose();
                EnsureHealthyPoseSample(trackerSample, "Physical tracker");
                var vmtSample = state.VmtSource.ReadPose();
                EnsureHealthyPoseSample(vmtSample, "VMT pose source");
                VmtPoseMatchSafety.EnsureVmtPoseMatchesMount(
                    trackerSample,
                    vmtSample,
                    application.Profile.TrackerToController,
                    static message => new InvalidOperationException(message));
            }
            catch (Exception exception)
            {
                var trackerHealthy = TryReadHealthy(state.TrackerSource);
                return Task.FromResult(new RuntimeHealthSnapshot(
                    trackerHealthy
                        ? RuntimeHealthFailureKind.VmtUnavailable
                        : RuntimeHealthFailureKind.TrackerLost,
                    $"The {hand} pose health check failed: {exception.Message}",
                    hand,
                    trackerHealthy ? state.VmtDevice.StableDeviceId : state.Tracker.StableDeviceId));
            }
        }

        return Task.FromResult(RuntimeHealthSnapshot.Healthy(
            "SteamVR, both exact Touch/tracker pairs, and both VMT pose sources are healthy."));
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _session?.Dispose();
        _session = null;
        await _vmt.DisposeAsync().ConfigureAwait(false);
        _ownedAlvrProbe?.Dispose();
        _disposed = true;
    }

    private OpenVrSession Session => _session ?? throw new InvalidOperationException(
        "The shared OpenVR session has not reached WaitingForSteamVR readiness.");

    private ApplicationState State(DailyUseProfileApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return _applications.TryGetValue(application.OperationId, out var state)
            ? state
            : throw new InvalidOperationException("Unknown daily-use profile-application handle.");
    }

    private SteamVrSettingsRecoveryPoint ReleaseApplicationSafetyOverrides(
        ApplicationState state)
    {
        if (state.ActiveBinding is null ||
            string.Equals(
                state.ActiveBinding.PoseSourceDevicePath,
                state.CleanupBinding.PoseSourceDevicePath,
                StringComparison.Ordinal))
        {
            return _settings.ReleaseApplicationSafetyOverrides(state.CleanupBinding);
        }

        return _settings.ReleaseApplicationSafetyOverrides(
            state.CleanupBinding,
            state.ActiveBinding.PoseSourceDevicePath);
    }

    private bool TryCreateDeviceSet(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        out CalibrationWizardDeviceSet? deviceSet,
        out string diagnostic)
    {
        deviceSet = null;
        diagnostic = string.Empty;
        var inputControllers = devices.Where(device =>
            device.Category == SteamVrDeviceCategory.InputController &&
            device.IsConnected).ToArray();
        var leftControllers = inputControllers.Where(device =>
            device.ControllerRole == SteamVrControllerRole.LeftHand &&
            SteamVrInputDeviceClassifier.Classify(device).IsSupported).ToArray();
        var rightControllers = inputControllers.Where(device =>
            device.ControllerRole == SteamVrControllerRole.RightHand &&
            SteamVrInputDeviceClassifier.Classify(device).IsSupported).ToArray();
        if (leftControllers.Length != 1 || rightControllers.Length != 1)
        {
            var observations = inputControllers.Length == 0
                ? "no connected input controllers were reported"
                : string.Join(
                    "; ",
                    inputControllers.Select(device =>
                    {
                        var classification = SteamVrInputDeviceClassifier.Classify(device);
                        return $"{device.ControllerRole}/{device.StableDeviceId}: " +
                            classification.Diagnostic;
                    }));
            diagnostic =
                "Waiting for exactly one supported Meta Touch controller per hand; " +
                $"supported left={leftControllers.Length}, right={rightControllers.Length}; " +
                observations;
            return false;
        }

        var physicalTrackers = devices.Where(device =>
            device.CanUseAsPhysicalPoseSource &&
            !VmtDeviceAddress.TryParse(device.Identity.DevicePath, out _)).ToArray();
        if (!TrySelectTrackerPair(physicalTrackers, out var leftTracker, out var rightTracker))
        {
            diagnostic =
                "Waiting for the exact stored left/right tracker pair, or an unambiguous " +
                $"two-tracker set; connected physical tracker count={physicalTrackers.Length}.";
            return false;
        }

        var readyLeftTracker = leftTracker!;
        var readyRightTracker = rightTracker!;
        CalibrationWizardRecalibrationObservations recalibration;
        try
        {
            recalibration = CreateCurrentRecalibrationObservations(
                leftControllers[0],
                rightControllers[0],
                readyLeftTracker,
                readyRightTracker);
        }
        catch (InvalidOperationException exception)
        {
            diagnostic =
                $"Waiting for a compatible Meta Touch controller pair: {exception.Message}";
            return false;
        }

        deviceSet = new CalibrationWizardDeviceSet(
            leftControllers[0].StableDeviceId,
            rightControllers[0].StableDeviceId,
            [readyLeftTracker.StableDeviceId, readyRightTracker.StableDeviceId])
        {
            Recalibration = recalibration,
        };
        diagnostic = "Current device metadata and stable identities satisfy daily-use readiness.";
        return true;
    }

    internal static CalibrationWizardRecalibrationObservations
        CreateCurrentRecalibrationObservations(
            SteamVrDeviceDescriptor leftController,
            SteamVrDeviceDescriptor rightController,
            SteamVrDeviceDescriptor leftTracker,
            SteamVrDeviceDescriptor rightTracker)
    {
        ArgumentNullException.ThrowIfNull(leftController);
        ArgumentNullException.ThrowIfNull(rightController);
        ArgumentNullException.ThrowIfNull(leftTracker);
        ArgumentNullException.ThrowIfNull(rightTracker);
        if (leftController.ControllerRole != SteamVrControllerRole.LeftHand ||
            rightController.ControllerRole != SteamVrControllerRole.RightHand)
        {
            throw new InvalidOperationException(
                "Controller compatibility observations require the current left controller " +
                "followed by the current right controller.");
        }

        var left = SteamVrInputDeviceClassifier.Classify(leftController);
        var right = SteamVrInputDeviceClassifier.Classify(rightController);
        if (!left.IsSupported || !right.IsSupported ||
            string.IsNullOrWhiteSpace(left.ControllerRuntime) ||
            string.IsNullOrWhiteSpace(left.ControllerModel) ||
            string.IsNullOrWhiteSpace(right.ControllerRuntime) ||
            string.IsNullOrWhiteSpace(right.ControllerModel))
        {
            throw new InvalidOperationException(
                "Current left and right controllers must both have supported runtime/model " +
                "classifications before profile compatibility can be evaluated.");
        }

        if (!string.Equals(
                left.ControllerRuntime,
                right.ControllerRuntime,
                StringComparison.Ordinal) ||
            !string.Equals(
                left.ControllerModel,
                right.ControllerModel,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Current left and right controllers report incompatible runtime/model " +
                $"identities: left={left.ControllerRuntime}/{left.ControllerModel}, " +
                $"right={right.ControllerRuntime}/{right.ControllerModel}.");
        }

        return new CalibrationWizardRecalibrationObservations
        {
            ObservedLeftTrackerSerial = leftTracker.StableDeviceId,
            ObservedRightTrackerSerial = rightTracker.StableDeviceId,
            ControllerRuntime = left.ControllerRuntime,
            ControllerModel = left.ControllerModel,
        };
    }

    private bool TrySelectTrackerPair(
        IReadOnlyList<SteamVrDeviceDescriptor> physicalTrackers,
        out SteamVrDeviceDescriptor? left,
        out SteamVrDeviceDescriptor? right)
    {
        left = null;
        right = null;
        if (File.Exists(_profileStorePath))
        {
            try
            {
                var profiles = CalibrationProfileFile.LoadStore(_profileStorePath).Profiles;
                var leftCandidates = profiles
                    .Where(profile => profile.Hand == ControllerHand.Left)
                    .Select(profile => physicalTrackers.SingleOrDefault(device => string.Equals(
                        device.StableDeviceId,
                        profile.TrackerSerial,
                        StringComparison.Ordinal)))
                    .Where(device => device is not null)
                    .DistinctBy(device => device!.StableDeviceId)
                    .ToArray();
                var rightCandidates = profiles
                    .Where(profile => profile.Hand == ControllerHand.Right)
                    .Select(profile => physicalTrackers.SingleOrDefault(device => string.Equals(
                        device.StableDeviceId,
                        profile.TrackerSerial,
                        StringComparison.Ordinal)))
                    .Where(device => device is not null)
                    .DistinctBy(device => device!.StableDeviceId)
                    .ToArray();
                if (leftCandidates.Length == 1 && rightCandidates.Length == 1 &&
                    !string.Equals(
                        leftCandidates[0]!.StableDeviceId,
                        rightCandidates[0]!.StableDeviceId,
                        StringComparison.Ordinal))
                {
                    left = leftCandidates[0];
                    right = rightCandidates[0];
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or FormatException)
            {
                // The backend reports the durable profile diagnostic. Device
                // readiness still falls back to an unambiguous physical pair.
            }
        }

        if (physicalTrackers.Count != 2)
        {
            return false;
        }

        left = physicalTrackers[0];
        right = physicalTrackers[1];
        return true;
    }

    private async Task<SteamVrDeviceDescriptor> WaitForConnectedVmtDeviceAsync(
        VmtDeviceAddress slot,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        while (_timeProvider.GetElapsedTime(startedAt) < VmtDiscoveryTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = Session.EnumerateDevices().Where(device =>
                VmtDeviceAddress.TryParse(device.Identity.DevicePath, out var address) &&
                address == slot).ToArray();
            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"SteamVR reported multiple VMT descriptors for slot {slot.Index}.");
            }

            if (candidates.SingleOrDefault() is { } current)
            {
                if (current.Category != SteamVrDeviceCategory.GenericTracker)
                {
                    throw new InvalidOperationException(
                        $"VMT slot {slot.Index} is {current.Category}, not GenericTracker.");
                }

                if (current.IsConnected)
                {
                    return current;
                }
            }

            await Task.Delay(VmtDiscoveryPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"VMT slot {slot.Index} was not discovered as a connected GenericTracker " +
            $"within {VmtDiscoveryTimeout.TotalSeconds:R} seconds after activation.");
    }

    private static SteamVrDeviceDescriptor SelectWizardController(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        CalibrationWizardDeviceSet expected,
        CalibrationWizardHand hand)
    {
        var serial = hand == CalibrationWizardHand.Left
            ? expected.LeftControllerSerial
            : expected.RightControllerSerial;
        var role = hand == CalibrationWizardHand.Left
            ? SteamVrControllerRole.LeftHand
            : SteamVrControllerRole.RightHand;
        var matches = devices.Where(device =>
            device.Category == SteamVrDeviceCategory.InputController &&
            device.ControllerRole == role &&
            device.IsConnected &&
            SteamVrInputDeviceClassifier.Classify(device).IsSupported &&
            string.Equals(device.StableDeviceId, serial, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Expected exactly one connected original {hand} Touch pose source with " +
                $"stable serial '{serial}'.");
    }

    private void EnsureHealthyOriginalTouchPose(
        PoseSourceSample sample,
        CalibrationWizardHand hand)
    {
        const PoseValidity required =
            PoseValidity.Orientation | PoseValidity.TrackingValid;
        if (!sample.IsConnected ||
            sample.TrackingResult != PoseTrackingResult.RunningOk ||
            (sample.Validity & required) != required)
        {
            throw new InvalidOperationException(
                $"The original {hand} Touch pose is not connected with a tracking-valid " +
                "orientation after semantic-hand overrides were released.");
        }

        if (sample.SampleAgeSeconds is { } age &&
            (!double.IsFinite(age) || age < 0d || age >= _staleAfter.TotalSeconds))
        {
            throw new InvalidOperationException(
                $"The original {hand} Touch pose is stale (age {age:R}s; threshold " +
                $"{_staleAfter.TotalSeconds:R}s). Keep Quest cameras able to observe " +
                "the controllers, then retry.");
        }
    }

    private static void ReportWizardCoverage(
        CalibrationWizardHand hand,
        IReadOnlyList<TimestampedPoseSample> samples,
        IProgress<CalibrationWizardCaptureProgress> progress)
    {
        var coverage = MotionCoverageAnalyzer.Evaluate(samples);
        var diagnostic = !coverage.IsRotationSufficient
            ? "continue multi-axis rotation for capture readiness"
            : coverage.IsPositionSufficient
                ? "rotation and position capture gates passed"
                : "rotation capture gates passed; position unavailable, so Auto may fall back normally";
        progress.Report(new CalibrationWizardCaptureProgress(
            hand,
            coverage.SampleCount,
            coverage.OrientationValidityFraction,
            coverage.PositionValidityFraction,
            coverage.RotationAxisCoverage,
            coverage.TotalRotationDegrees,
            coverage.RotationProgress,
            coverage.PositionProgress,
            coverage.IsRotationSufficient,
            coverage.IsPositionSufficient,
            diagnostic));
    }

    private void EnsureHealthyPoseSample(PoseSourceSample sample, string sourceName)
    {
        if (!sample.IsConnected ||
            sample.TrackingResult != PoseTrackingResult.RunningOk ||
            (sample.Validity & RequiredPoseValidity) != RequiredPoseValidity)
        {
            throw new InvalidOperationException(
                $"{sourceName} is not connected and fully tracking-valid with position and orientation.");
        }

        if (sample.SampleAgeSeconds is { } age &&
            (!double.IsFinite(age) || age < 0d || age >= _staleAfter.TotalSeconds))
        {
            throw new InvalidOperationException(
                $"{sourceName} sample is stale (age {age:R}s; threshold {_staleAfter.TotalSeconds:R}s).");
        }
    }

    private bool TryReadHealthy(TrackedPoseSource source)
    {
        try
        {
            EnsureHealthyPoseSample(source.ReadPose(), "Physical tracker");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SteamVrDeviceDescriptor SelectCurrentTracker(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        string serial)
    {
        var matches = devices.Where(device => string.Equals(
            device.StableDeviceId,
            serial,
            StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 ||
            !matches[0].CanUseAsPhysicalPoseSource ||
            VmtDeviceAddress.TryParse(matches[0].Identity.DevicePath, out _))
        {
            throw new InvalidOperationException(
                "Expected one connected physical Lighthouse pose source with exact serial " +
                $"'{serial}'.");
        }

        return matches[0];
    }

    internal static SteamVrDeviceDescriptor SelectCurrentController(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        CalibrationWizardProfileView profile)
    {
        var role = profile.Hand == CalibrationWizardHand.Left
            ? SteamVrControllerRole.LeftHand
            : SteamVrControllerRole.RightHand;
        var matches = devices.Where(device =>
            device.Category == SteamVrDeviceCategory.InputController &&
            device.ControllerRole == role &&
            device.IsConnected &&
            SteamVrInputDeviceClassifier.Classify(device).IsSupported &&
            (string.IsNullOrEmpty(profile.ControllerSerial) ||
             string.Equals(
                 device.StableDeviceId,
                 profile.ControllerSerial,
                 StringComparison.Ordinal))).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                string.IsNullOrEmpty(profile.ControllerSerial)
                    ? $"Expected exactly one connected Touch controller with role {role}."
                    : $"Expected one connected {role} Touch controller with exact serial " +
                      $"'{profile.ControllerSerial}'.");
        }

        return matches[0];
    }

    private static void EnsureCurrentDescriptor(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        SteamVrDeviceDescriptor expected,
        string sourceName)
    {
        if (!DescriptorIsCurrent(devices, expected))
        {
            throw new InvalidOperationException(
                $"The {sourceName} '{expected.StableDeviceId}' disconnected, disappeared, " +
                "changed path/category/role, or had its transient index reused.");
        }
    }

    private static bool DescriptorIsCurrent(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        SteamVrDeviceDescriptor expected)
    {
        var matches = devices.Where(device =>
            string.Equals(device.StableDeviceId, expected.StableDeviceId, StringComparison.Ordinal) ||
            string.Equals(
                device.Identity.DevicePath,
                expected.Identity.DevicePath,
                StringComparison.Ordinal) ||
            device.TransientDeviceIndex == expected.TransientDeviceIndex).ToArray();
        return matches.Length == 1 &&
            matches[0].IsConnected &&
            string.Equals(matches[0].StableDeviceId, expected.StableDeviceId, StringComparison.Ordinal) &&
            string.Equals(
                matches[0].Identity.DevicePath,
                expected.Identity.DevicePath,
                StringComparison.Ordinal) &&
            matches[0].TransientDeviceIndex == expected.TransientDeviceIndex &&
            matches[0].Category == expected.Category &&
            matches[0].ControllerRole == expected.ControllerRole &&
            matches[0].Metadata == expected.Metadata &&
            matches[0].Capabilities == expected.Capabilities;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class ApplicationState
    {
        public ApplicationState(
            SteamVrDeviceDescriptor tracker,
            SteamVrDeviceDescriptor controller,
            VmtDeviceConfiguration configuration,
            TrackingOverrideBinding cleanupBinding)
        {
            Tracker = tracker;
            Controller = controller;
            Configuration = configuration;
            CleanupBinding = cleanupBinding;
        }

        public SteamVrDeviceDescriptor Tracker { get; }

        public SteamVrDeviceDescriptor Controller { get; }

        public VmtDeviceConfiguration Configuration { get; }

        public TrackingOverrideBinding CleanupBinding { get; }

        public List<SteamVrSettingsRecoveryPoint> SettingsRecoveryPoints { get; } = [];

        public TrackedPoseSource? TrackerSource { get; set; }

        public SteamVrDeviceDescriptor? VmtDevice { get; set; }

        public TrackedPoseSource? VmtSource { get; set; }

        public TrackingOverrideBinding? ActiveBinding { get; set; }
    }
}
