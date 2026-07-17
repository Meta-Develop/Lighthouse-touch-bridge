using System.Numerics;
using Ltb.Core;
using Ltb.OpenVr;
using Ltb.Vmt;

namespace Ltb.App;

internal interface IOneHandBridgeRuntime
{
    IReadOnlyList<SteamVrDeviceDescriptor> EnumerateDevices();

    TrackedPoseSource CreateTrackedPoseSource(SteamVrDeviceDescriptor device);
}

internal interface IOneHandBridgeVmtController
{
    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask WaitForAliveAsync(TimeSpan timeout, CancellationToken cancellationToken);

    bool IsAlive { get; }

    ValueTask ActivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken);

    ValueTask DeactivateAsync(
        VmtDeviceConfiguration configuration,
        CancellationToken cancellationToken);
}

internal interface IOneHandBridgeOverrideController
{
    void Prepare(TrackingOverrideBinding binding);

    void Enable(TrackingOverrideBinding binding);

    void Release(TrackingOverrideBinding binding);
}

internal interface IOneHandBridgeVerificationProbe
{
    ValueTask<OneHandBridgeVerificationObservation> ObserveAsync(
        OneHandBridgeVerificationRequest request,
        CancellationToken cancellationToken);
}

internal interface IOneHandBridgeDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed record OneHandBridgeVerificationRequest(
    string ExpectedInputControllerSerial,
    string ExpectedPoseSourceDevicePath,
    RigidTransform ExpectedOutputPose);

internal sealed record OneHandBridgeVerificationObservation(
    string? InputControllerSerial,
    string? PoseSourceDevicePath,
    RigidTransform? OutputPose,
    bool IsDeferred,
    string Summary)
{
    public static OneHandBridgeVerificationObservation Deferred(string summary) =>
        new(null, null, null, true, summary);
}

internal enum OneHandBridgeStopReason
{
    Cancellation,
    HealthFailure,
}

internal sealed record OneHandBridgeResult(
    OneHandBridgeStopReason StopReason,
    string Message,
    SteamVrDeviceDescriptor? Tracker,
    SteamVrDeviceDescriptor? Controller,
    SteamVrDeviceDescriptor? VmtDevice,
    OneHandBridgeVerificationObservation Verification,
    IReadOnlyList<Exception> SafeDisableFailures);

internal sealed record OneHandBridgeActiveState(
    SteamVrDeviceDescriptor Tracker,
    SteamVrDeviceDescriptor Controller,
    SteamVrDeviceDescriptor VmtDevice,
    OneHandBridgeVerificationObservation Verification,
    TimeSpan EffectiveMonitorInterval);

internal sealed class OneHandBridgeRunException : Exception
{
    public OneHandBridgeRunException(
        string message,
        Exception failure,
        IReadOnlyList<Exception> safeDisableFailures)
        : base(FormatMessage(message, safeDisableFailures), failure)
    {
        SafeDisableFailures = safeDisableFailures;
    }

    public IReadOnlyList<Exception> SafeDisableFailures { get; }

    private static string FormatMessage(
        string message,
        IReadOnlyList<Exception> safeDisableFailures) =>
        safeDisableFailures.Count == 0
            ? message
            : $"{message} SafeDisable also reported {safeDisableFailures.Count} failure(s): " +
              string.Join(" | ", safeDisableFailures.Select(failure => failure.Message));
}

internal sealed class OneHandBridgeCoordinator
{
    internal const double MinimumStaleAfterSeconds = 0.001d;
    internal static readonly TimeSpan VmtHeartbeatTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan VmtConnectionPollInterval =
        TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CleanupActionTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(5);

    private static readonly PoseValidity RequiredTrackerValidity =
        PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid;

    private const double MaximumPoseComparisonSkewSeconds = 0.05d;
    private const float MaximumVmtPositionErrorMeters = 0.15f;
    private const float MaximumVmtRotationErrorRadians = MathF.PI / 9f;

    private readonly IOneHandBridgeRuntime _runtime;
    private readonly IOneHandBridgeVmtController _vmt;
    private readonly IOneHandBridgeOverrideController _overrides;
    private readonly IOneHandBridgeVerificationProbe _verificationProbe;
    private readonly IOneHandBridgeDelay _delay;
    private readonly Action<OneHandBridgeActiveState>? _onActivated;
    private readonly TimeProvider _timeProvider;

    public OneHandBridgeCoordinator(
        IOneHandBridgeRuntime runtime,
        IOneHandBridgeVmtController vmt,
        IOneHandBridgeOverrideController overrides,
        IOneHandBridgeVerificationProbe verificationProbe,
        IOneHandBridgeDelay delay,
        Action<OneHandBridgeActiveState>? onActivated = null,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _vmt = vmt ?? throw new ArgumentNullException(nameof(vmt));
        _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
        _verificationProbe = verificationProbe ??
            throw new ArgumentNullException(nameof(verificationProbe));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _onActivated = onActivated;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<OneHandBridgeResult> RunAsync(
        BridgeProfile profile,
        VmtDeviceAddress vmtSlot,
        TimeSpan staleAfter,
        double monitorRateHz,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (staleAfter < TimeSpan.FromSeconds(MinimumStaleAfterSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(staleAfter),
                $"Tracker staleness threshold must be at least {MinimumStaleAfterSeconds:R} seconds.");
        }

        if (!double.IsFinite(monitorRateHz) || monitorRateHz <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorRateHz));
        }

        var monitorInterval = EffectiveMonitorInterval(staleAfter, monitorRateHz);

        var cleanupBinding = new TrackingOverrideBinding(
            vmtSlot.DevicePath,
            profile.Hand == BridgeHand.Left
                ? TrackingOverrideBinding.LeftHandPath
                : TrackingOverrideBinding.RightHandPath);
        var binding = cleanupBinding;
        var configuration = new VmtDeviceConfiguration(
            vmtSlot,
            profile.TrackerSerial,
            profile.TrackerToController);
        SteamVrDeviceDescriptor? tracker = null;
        SteamVrDeviceDescriptor? controller = null;
        SteamVrDeviceDescriptor? observedVmtDevice = null;

        OneHandBridgeVerificationObservation? verification = null;
        try
        {
            await _vmt.StartAsync(cancellationToken).ConfigureAwait(false);
            var bootstrapFailures = await PreActivationCleanupAsync(
                    configuration,
                    cleanupBinding)
                .ConfigureAwait(false);
            if (bootstrapFailures.Count > 0)
            {
                throw new PreActivationCleanupException(bootstrapFailures);
            }

            var devices = _runtime.EnumerateDevices();
            tracker = SelectTracker(devices, profile.TrackerSerial);
            controller = SelectController(devices, profile);
            if (VmtDeviceAddress.TryParse(
                    tracker.Identity.DevicePath,
                    out var trackerVmtSlot) &&
                trackerVmtSlot.Index == vmtSlot.Index)
            {
                throw new InvalidOperationException(
                    $"Physical tracker '{tracker.StableDeviceId}' resolves to requested VMT slot " +
                    $"{vmtSlot.Index}; a VMT device cannot follow itself.");
            }

            var trackerSource = _runtime.CreateTrackedPoseSource(tracker);
            var initialTrackerSample = trackerSource.ReadPose();
            EnsureHealthyPoseSample(initialTrackerSample, staleAfter, "Tracker");

            await _vmt.WaitForAliveAsync(VmtHeartbeatTimeout, cancellationToken)
                .ConfigureAwait(false);

            // Heartbeat acquisition can take multiple seconds. Re-read the
            // tracker immediately before activation so a loss during that
            // wait can never activate a stale virtual source.
            var activationTrackerSample = trackerSource.ReadPose();
            EnsureHealthyPoseSample(activationTrackerSample, staleAfter, "Tracker");
            EnsurePreActivationIdentities(tracker, trackerSource, controller);
            if (!_vmt.IsAlive)
            {
                throw new InvalidOperationException(
                    "VMT heartbeat was not fresh immediately before activation.");
            }

            await _vmt.ActivateAsync(configuration, cancellationToken).ConfigureAwait(false);
            observedVmtDevice = await WaitForConnectedVmtDeviceAsync(
                    vmtSlot,
                    cancellationToken)
                .ConfigureAwait(false);
            binding = new TrackingOverrideBinding(
                observedVmtDevice.Identity.DevicePath,
                cleanupBinding.SemanticHandPath);
            var vmtPoseSource = _runtime.CreateTrackedPoseSource(observedVmtDevice);
            var enableTrackerSample = trackerSource.ReadPose();
            EnsureHealthyPoseSample(enableTrackerSample, staleAfter, "Tracker");
            var enableVmtSample = vmtPoseSource.ReadPose();
            EnsureHealthyPoseSample(enableVmtSample, staleAfter, "VMT pose source");
            EnsureVmtPoseMatchesMount(
                enableTrackerSample,
                enableVmtSample,
                profile.TrackerToController);
            EnsureActiveDeviceIdentities(
                tracker,
                trackerSource,
                controller,
                observedVmtDevice,
                vmtPoseSource);
            if (!_vmt.IsAlive)
            {
                throw new OneHandBridgeHealthException(
                    "VMT heartbeat became stale before the TrackingOverride could be enabled.");
            }

            _overrides.Enable(binding);

            var verificationRequest = new OneHandBridgeVerificationRequest(
                controller.StableDeviceId,
                observedVmtDevice.Identity.DevicePath,
                CoordinateConventions.ComposeRuntimeOutput(
                    enableTrackerSample.Pose,
                    profile.TrackerToController));
            verification = await ObserveWithSafetyAsync(
                    verificationRequest,
                    trackerSource,
                    vmtPoseSource,
                    tracker,
                    controller,
                    observedVmtDevice,
                    profile.TrackerToController,
                    staleAfter,
                    monitorInterval,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateVerification(verificationRequest, verification);
            _onActivated?.Invoke(new OneHandBridgeActiveState(
                tracker,
                controller,
                observedVmtDevice,
                verification,
                monitorInterval));

            while (true)
            {
                await _delay.DelayAsync(monitorInterval, cancellationToken)
                    .ConfigureAwait(false);
                EnsureRuntimeHealthy(
                    trackerSource,
                    vmtPoseSource,
                    tracker,
                    controller,
                    observedVmtDevice,
                    profile.TrackerToController,
                    staleAfter);
            }
        }
        catch (PreActivationCleanupException failure)
        {
            throw new OneHandBridgeRunException(
                "One-hand bridge pre-activation cleanup failed; no new activation was attempted.",
                failure,
                failure.Failures);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cleanupFailures = await SafeDisableAsync(configuration, binding)
                .ConfigureAwait(false);
            return new OneHandBridgeResult(
                OneHandBridgeStopReason.Cancellation,
                cleanupFailures.Count == 0
                    ? "Cancellation requested; the one-hand bridge was safely disabled."
                    : "Cancellation requested; SafeDisable cleanup is incomplete and the override state must be checked manually.",
                tracker,
                controller,
                observedVmtDevice,
                verification ?? OneHandBridgeVerificationObservation.Deferred(
                    "Verification did not complete before cancellation."),
                cleanupFailures);
        }
        catch (OneHandBridgeHealthException failure)
        {
            var cleanupFailures = await SafeDisableAsync(configuration, binding)
                .ConfigureAwait(false);
            return new OneHandBridgeResult(
                OneHandBridgeStopReason.HealthFailure,
                failure.Message,
                tracker,
                controller,
                observedVmtDevice,
                verification ?? OneHandBridgeVerificationObservation.Deferred(
                    "Verification did not complete before the health failure."),
                cleanupFailures);
        }
        catch (Exception failure)
        {
            var cleanupFailures = await SafeDisableAsync(configuration, binding)
                .ConfigureAwait(false);
            throw new OneHandBridgeRunException(
                $"One-hand bridge failed: {failure.Message}",
                failure,
                cleanupFailures);
        }
    }

    private void EnsureRuntimeHealthy(
        TrackedPoseSource trackerSource,
        TrackedPoseSource vmtPoseSource,
        SteamVrDeviceDescriptor tracker,
        SteamVrDeviceDescriptor controller,
        SteamVrDeviceDescriptor vmtDevice,
        RigidTransform mount,
        TimeSpan staleAfter)
    {
        PoseSourceSample trackerSample;
        try
        {
            trackerSample = trackerSource.ReadPose();
            EnsureHealthyPoseSample(trackerSample, staleAfter, "Tracker");
        }
        catch (OneHandBridgeHealthException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new OneHandBridgeHealthException(
                $"Tracker monitor failed: {exception.Message}", exception);
        }

        PoseSourceSample vmtSample;
        try
        {
            vmtSample = vmtPoseSource.ReadPose();
            EnsureHealthyPoseSample(vmtSample, staleAfter, "VMT pose source");
        }
        catch (OneHandBridgeHealthException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new OneHandBridgeHealthException(
                $"VMT pose monitor failed: {exception.Message}", exception);
        }

        EnsureVmtPoseMatchesMount(trackerSample, vmtSample, mount);
        EnsureActiveDeviceIdentities(
            tracker,
            trackerSource,
            controller,
            vmtDevice,
            vmtPoseSource);

        bool vmtAlive;
        try
        {
            vmtAlive = _vmt.IsAlive;
        }
        catch (Exception exception)
        {
            throw new OneHandBridgeHealthException(
                $"VMT heartbeat monitor failed: {exception.Message}", exception);
        }

        if (!vmtAlive)
        {
            throw new OneHandBridgeHealthException(
                "VMT heartbeat became stale or unavailable.");
        }
    }

    private void EnsurePreActivationIdentities(
        SteamVrDeviceDescriptor tracker,
        TrackedPoseSource trackerSource,
        SteamVrDeviceDescriptor controller)
    {
        EnsurePoseSourceBinding(tracker, trackerSource.Device, "Tracker");
        IReadOnlyList<SteamVrDeviceDescriptor> devices;
        try
        {
            devices = _runtime.EnumerateDevices();
        }
        catch (Exception exception)
        {
            throw new OneHandBridgeHealthException(
                $"SteamVR identity check failed: {exception.Message}", exception);
        }

        EnsureCurrentDescriptor(devices, tracker, "Tracker");
        EnsureCurrentDescriptor(devices, controller, "Touch input controller");
    }

    private void EnsureActiveDeviceIdentities(
        SteamVrDeviceDescriptor tracker,
        TrackedPoseSource trackerSource,
        SteamVrDeviceDescriptor controller,
        SteamVrDeviceDescriptor vmtDevice,
        TrackedPoseSource vmtPoseSource)
    {
        EnsurePoseSourceBinding(tracker, trackerSource.Device, "Tracker");
        EnsurePoseSourceBinding(vmtDevice, vmtPoseSource.Device, "VMT pose source");
        IReadOnlyList<SteamVrDeviceDescriptor> devices;
        try
        {
            devices = _runtime.EnumerateDevices();
        }
        catch (Exception exception)
        {
            throw new OneHandBridgeHealthException(
                $"SteamVR device monitor failed: {exception.Message}", exception);
        }

        EnsureCurrentDescriptor(devices, tracker, "Tracker");
        EnsureCurrentDescriptor(devices, controller, "Touch input controller");
        EnsureCurrentDescriptor(devices, vmtDevice, "VMT pose source");
    }

    private async ValueTask<SteamVrDeviceDescriptor> WaitForConnectedVmtDeviceAsync(
        VmtDeviceAddress requestedSlot,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_timeProvider.GetElapsedTime(startedAt) >= VmtHeartbeatTimeout)
            {
                break;
            }

            IReadOnlyList<SteamVrDeviceDescriptor> devices;
            try
            {
                devices = _runtime.EnumerateDevices();
            }
            catch (Exception exception)
            {
                throw new OneHandBridgeHealthException(
                    $"VMT post-activation discovery failed: {exception.Message}", exception);
            }

            if (_timeProvider.GetElapsedTime(startedAt) >= VmtHeartbeatTimeout)
            {
                break;
            }

            var candidates = devices.Where(device =>
                VmtDeviceAddress.TryParse(device.Identity.DevicePath, out var address) &&
                address.Index == requestedSlot.Index).ToArray();
            if (candidates.Length > 1)
            {
                throw new OneHandBridgeHealthException(
                    $"SteamVR reported multiple VMT descriptors for slot {requestedSlot.Index}.");
            }

            var current = candidates.SingleOrDefault();
            if (current is not null)
            {
                if (current.Category != SteamVrDeviceCategory.GenericTracker)
                {
                    throw new OneHandBridgeHealthException(
                        $"Discovered VMT slot {requestedSlot.Index} is {current.Category}, not GenericTracker.");
                }

                if (current.IsConnected)
                {
                    return current;
                }
            }

            var remaining = VmtHeartbeatTimeout - _timeProvider.GetElapsedTime(startedAt);
            var delay = remaining < VmtConnectionPollInterval
                ? remaining
                : VmtConnectionPollInterval;
            await _delay.DelayAsync(delay, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new OneHandBridgeHealthException(
            $"VMT slot {requestedSlot.Index} was not discovered as a connected GenericTracker " +
            $"within {VmtHeartbeatTimeout.TotalSeconds:R} seconds after activation.");
    }

    private async ValueTask<IReadOnlyList<Exception>> PreActivationCleanupAsync(
        VmtDeviceConfiguration configuration,
        TrackingOverrideBinding cleanupBinding)
    {
        var failures = new List<Exception>(capacity: 2);
        try
        {
            await DeactivateWithTimeoutAsync(configuration)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                $"Pre-activation VMT deactivation failed: {exception.Message}",
                exception));
        }

        try
        {
            _overrides.Prepare(cleanupBinding);
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                $"Pre-activation TrackingOverride cleanup failed: {exception.Message}",
                exception));
        }

        return failures.AsReadOnly();
    }

    private async ValueTask DeactivateWithTimeoutAsync(
        VmtDeviceConfiguration configuration)
    {
        var operation = _vmt.DeactivateAsync(configuration, CancellationToken.None);
        if (operation.IsCompleted)
        {
            await operation.ConfigureAwait(false);
            return;
        }

        var operationTask = operation.AsTask();
        var timeoutTask = _delay
            .DelayAsync(CleanupActionTimeout, CancellationToken.None)
            .AsTask();
        if (await Task.WhenAny(operationTask, timeoutTask).ConfigureAwait(false) !=
            operationTask)
        {
            throw new TimeoutException(
                $"VMT deactivation did not complete within {CleanupActionTimeout.TotalSeconds:R} seconds.");
        }

        await operationTask.ConfigureAwait(false);
    }

    private async ValueTask<OneHandBridgeVerificationObservation> ObserveWithSafetyAsync(
        OneHandBridgeVerificationRequest request,
        TrackedPoseSource trackerSource,
        TrackedPoseSource vmtPoseSource,
        SteamVrDeviceDescriptor tracker,
        SteamVrDeviceDescriptor controller,
        SteamVrDeviceDescriptor vmtDevice,
        RigidTransform mount,
        TimeSpan staleAfter,
        TimeSpan monitorInterval,
        CancellationToken cancellationToken)
    {
        var observation = _verificationProbe.ObserveAsync(request, cancellationToken);
        if (observation.IsCompleted)
        {
            return await observation.ConfigureAwait(false);
        }

        var observationTask = observation.AsTask();
        var startedAt = _timeProvider.GetTimestamp();
        while (!observationTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = _timeProvider.GetElapsedTime(startedAt);
            if (elapsed >= VerificationTimeout)
            {
                throw new TimeoutException(
                    $"Runtime verification did not complete within {VerificationTimeout.TotalSeconds:R} seconds.");
            }

            var remaining = VerificationTimeout - elapsed;
            var delay = remaining < monitorInterval ? remaining : monitorInterval;
            await _delay.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            if (!observationTask.IsCompleted)
            {
                EnsureRuntimeHealthy(
                    trackerSource,
                    vmtPoseSource,
                    tracker,
                    controller,
                    vmtDevice,
                    mount,
                    staleAfter);
            }
        }

        return await observationTask.ConfigureAwait(false);
    }

    private static SteamVrDeviceDescriptor? SingleByStableId(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        string stableId)
    {
        try
        {
            return devices.SingleOrDefault(device =>
                string.Equals(device.StableDeviceId, stableId, StringComparison.Ordinal));
        }
        catch (InvalidOperationException exception)
        {
            throw new OneHandBridgeHealthException(
                $"SteamVR reported duplicate device serial '{stableId}'.", exception);
        }
    }

    private static SteamVrDeviceDescriptor? SingleByDevicePath(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        string devicePath)
    {
        try
        {
            return devices.SingleOrDefault(device => string.Equals(
                device.Identity.DevicePath,
                devicePath,
                StringComparison.Ordinal));
        }
        catch (InvalidOperationException exception)
        {
            throw new OneHandBridgeHealthException(
                $"SteamVR reported duplicate device path '{devicePath}'.", exception);
        }
    }

    private static void EnsurePoseSourceBinding(
        SteamVrDeviceDescriptor expected,
        SteamVrDeviceDescriptor bound,
        string sourceName)
    {
        if (!DescriptorsMatch(expected, bound))
        {
            throw new OneHandBridgeHealthException(
                $"{sourceName} pose source is bound to a different SteamVR identity or transient handle.");
        }
    }

    private static void EnsureCurrentDescriptor(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        SteamVrDeviceDescriptor expected,
        string sourceName)
    {
        if (!expected.Identity.DevicePath.StartsWith("/devices/", StringComparison.Ordinal))
        {
            throw new OneHandBridgeHealthException(
                $"{sourceName} path '{expected.Identity.DevicePath}' is not a canonical OpenVR device path.");
        }

        var bySerial = SingleByStableId(devices, expected.StableDeviceId);
        var byPath = SingleByDevicePath(devices, expected.Identity.DevicePath);
        SteamVrDeviceDescriptor? byIndex;
        try
        {
            byIndex = devices.SingleOrDefault(device =>
                device.TransientDeviceIndex == expected.TransientDeviceIndex);
        }
        catch (InvalidOperationException exception)
        {
            throw new OneHandBridgeHealthException(
                $"SteamVR reported duplicate transient index {expected.TransientDeviceIndex}.",
                exception);
        }

        if (bySerial is null || byPath is null || byIndex is null ||
            !DescriptorsMatch(expected, bySerial) ||
            !DescriptorsMatch(expected, byPath) ||
            !DescriptorsMatch(expected, byIndex) ||
            !bySerial.IsConnected)
        {
            throw new OneHandBridgeHealthException(
                $"{sourceName} '{expected.StableDeviceId}' disconnected, disappeared, changed " +
                "path/category/role, or had its transient index reused.");
        }
    }

    private static bool DescriptorsMatch(
        SteamVrDeviceDescriptor expected,
        SteamVrDeviceDescriptor actual) =>
        string.Equals(expected.StableDeviceId, actual.StableDeviceId, StringComparison.Ordinal) &&
        string.Equals(
            expected.Identity.DevicePath,
            actual.Identity.DevicePath,
            StringComparison.Ordinal) &&
        expected.TransientDeviceIndex == actual.TransientDeviceIndex &&
        expected.Category == actual.Category &&
        expected.ControllerRole == actual.ControllerRole;

    private static void EnsureVmtPoseMatchesMount(
        PoseSourceSample trackerSample,
        PoseSourceSample vmtSample,
        RigidTransform mount)
    {
        var skewSeconds = Math.Abs(
            trackerSample.MonotonicHostTimeSeconds -
            vmtSample.MonotonicHostTimeSeconds);
        if (!double.IsFinite(skewSeconds) ||
            skewSeconds > MaximumPoseComparisonSkewSeconds)
        {
            throw new OneHandBridgeHealthException(
                $"Tracker/VMT pose samples are not comparable in time (skew {skewSeconds:R}s).");
        }

        var expected = CoordinateConventions.ComposeRuntimeOutput(
            trackerSample.Pose,
            mount);
        var positionError = Vector3.Distance(
            expected.TranslationMeters,
            vmtSample.Pose.TranslationMeters);
        var quaternionDot = Math.Clamp(
            MathF.Abs(Quaternion.Dot(expected.Rotation, vmtSample.Pose.Rotation)),
            0f,
            1f);
        var rotationError = 2f * MathF.Acos(quaternionDot);
        if (positionError > MaximumVmtPositionErrorMeters ||
            rotationError > MaximumVmtRotationErrorRadians)
        {
            throw new OneHandBridgeHealthException(
                "VMT output pose does not match tracker * mount " +
                $"(position error {positionError:R}m, rotation error {rotationError:R}rad).");
        }
    }

    private static SteamVrDeviceDescriptor SelectTracker(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        string trackerSerial)
    {
        var matches = devices.Where(device =>
            string.Equals(device.StableDeviceId, trackerSerial, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one SteamVR device with tracker serial '{trackerSerial}'; found {matches.Length}.");
        }

        var tracker = matches[0];
        if (tracker.Category != SteamVrDeviceCategory.GenericTracker)
        {
            throw new InvalidOperationException(
                $"Profile tracker '{trackerSerial}' is {tracker.Category}, not GenericTracker.");
        }

        if (!tracker.IsConnected)
        {
            throw new InvalidOperationException(
                $"Profile tracker '{trackerSerial}' is disconnected.");
        }

        return tracker;
    }

    private static SteamVrDeviceDescriptor SelectController(
        IReadOnlyList<SteamVrDeviceDescriptor> devices,
        BridgeProfile profile)
    {
        var expectedRole = profile.Hand == BridgeHand.Left
            ? SteamVrControllerRole.LeftHand
            : SteamVrControllerRole.RightHand;
        var candidates = devices.Where(device =>
            device.Category == SteamVrDeviceCategory.InputController &&
            device.IsConnected &&
            (profile.ControllerSerial is null
                ? device.ControllerRole == expectedRole
                : string.Equals(
                    device.StableDeviceId,
                    profile.ControllerSerial,
                    StringComparison.Ordinal))).ToArray();
        if (candidates.Length != 1)
        {
            var selector = profile.ControllerSerial is null
                ? $"connected {expectedRole} role"
                : $"controller serial '{profile.ControllerSerial}'";
            throw new InvalidOperationException(
                $"Expected exactly one Touch device matching {selector}; found {candidates.Length}.");
        }

        var controller = candidates[0];
        if (controller.ControllerRole != expectedRole)
        {
            throw new InvalidOperationException(
                $"Touch controller '{controller.StableDeviceId}' reports role " +
                $"{controller.ControllerRole}, not {expectedRole} for the profile hand.");
        }

        return controller;
    }

    private static void EnsureHealthyPoseSample(
        PoseSourceSample sample,
        TimeSpan staleAfter,
        string sourceName)
    {
        if (!sample.IsConnected)
        {
            throw new OneHandBridgeHealthException($"{sourceName} disconnected.");
        }

        if ((sample.Validity & RequiredTrackerValidity) != RequiredTrackerValidity ||
            sample.TrackingResult != PoseTrackingResult.RunningOk)
        {
            throw new OneHandBridgeHealthException(
                $"{sourceName} is not fully tracking-valid with usable orientation and position.");
        }

        // OpenVR's synchronous GetDeviceToAbsoluteTrackingPose read does not
        // expose sensor age. When an adapter does report age, enforce it;
        // otherwise the fresh synchronous read plus runtime validity gates are
        // the available safety evidence.
        if (sample.SampleAgeSeconds.HasValue &&
            (!double.IsFinite(sample.SampleAgeSeconds.Value) ||
             sample.SampleAgeSeconds.Value < 0d ||
             sample.SampleAgeSeconds.Value >= staleAfter.TotalSeconds))
        {
            var observedAge = sample.SampleAgeSeconds.Value.ToString(
                "R",
                System.Globalization.CultureInfo.InvariantCulture);
            throw new OneHandBridgeHealthException(
                $"{sourceName} sample is stale (age {observedAge}s; threshold {staleAfter.TotalSeconds:R}s).");
        }
    }

    private static void ValidateVerification(
        OneHandBridgeVerificationRequest request,
        OneHandBridgeVerificationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.IsDeferred)
        {
            return;
        }

        if (!string.Equals(
                observation.InputControllerSerial,
                request.ExpectedInputControllerSerial,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Runtime verification did not observe Touch inputs from the selected controller.");
        }

        if (!string.Equals(
                observation.PoseSourceDevicePath,
                request.ExpectedPoseSourceDevicePath,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Runtime verification did not observe the discovered VMT pose source.");
        }

        if (!observation.OutputPose.HasValue ||
            !TransformsEquivalent(observation.OutputPose.Value, request.ExpectedOutputPose))
        {
            throw new InvalidOperationException(
                "Runtime verification output pose did not equal T_L_tracker * T_T_C.");
        }
    }

    private async ValueTask<IReadOnlyList<Exception>> SafeDisableAsync(
        VmtDeviceConfiguration configuration,
        TrackingOverrideBinding binding)
    {
        var failures = new List<Exception>(capacity: 2);
        try
        {
            // Immediate VMT deactivation is first: VMT releases the live
            // override when the virtual tracker is disabled.
            await DeactivateWithTimeoutAsync(configuration)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                $"VMT deactivation failed: {exception.Message}", exception));
        }

        try
        {
            // Persistence cleanup is always attempted even if deactivation
            // failed, and it removes only this exact source/hand mapping.
            _overrides.Release(binding);
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                $"TrackingOverride release failed: {exception.Message}", exception));
        }

        return failures.AsReadOnly();
    }

    private static bool TransformsEquivalent(RigidTransform left, RigidTransform right)
    {
        const float positionToleranceMeters = 1e-4f;
        const float quaternionDotTolerance = 1e-5f;
        return left.IsValid &&
               right.IsValid &&
               Vector3.Distance(left.TranslationMeters, right.TranslationMeters) <=
               positionToleranceMeters &&
               MathF.Abs(Quaternion.Dot(left.Rotation, right.Rotation)) >=
               1f - quaternionDotTolerance;
    }

    private static TimeSpan EffectiveMonitorInterval(
        TimeSpan staleAfter,
        double requestedMonitorRateHz)
    {
        var requestedSeconds = 1d / requestedMonitorRateHz;
        var requested = !double.IsFinite(requestedSeconds) ||
                        requestedSeconds >= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(Math.Max(
                1,
                TimeSpan.FromSeconds(requestedSeconds).Ticks));
        var trackerSafetyBound = TimeSpan.FromTicks(Math.Max(1, staleAfter.Ticks / 2));
        var heartbeatSafetyBound = TimeSpan.FromTicks(
            Math.Max(1, VmtHeartbeatTimeout.Ticks / 2));
        return requested <= trackerSafetyBound && requested <= heartbeatSafetyBound
            ? requested
            : trackerSafetyBound <= heartbeatSafetyBound
                ? trackerSafetyBound
                : heartbeatSafetyBound;
    }

    private sealed class OneHandBridgeHealthException : Exception
    {
        public OneHandBridgeHealthException(string message)
            : base(message)
        {
        }

        public OneHandBridgeHealthException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class PreActivationCleanupException : Exception
    {
        public PreActivationCleanupException(IReadOnlyList<Exception> failures)
            : base(string.Join(" | ", failures.Select(failure => failure.Message)))
        {
            Failures = failures;
        }

        public IReadOnlyList<Exception> Failures { get; }
    }
}
