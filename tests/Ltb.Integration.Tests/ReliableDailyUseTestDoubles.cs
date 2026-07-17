using System.Numerics;
using Ltb.App;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.Integration.Tests;

internal sealed class ReliableDailyUseFakeRuntime : IReliableDailyUseRuntime
{
    private readonly Queue<CalibrationWizardDependencyStatus> _dependencyStatuses = [];
    private readonly Queue<DailyUseDeviceReadiness> _deviceReadiness = [];
    private readonly Queue<int> _transientHandles = [];
    private readonly Queue<RuntimeHealthSnapshot> _healthSnapshots = [];
    private readonly Dictionary<Guid, ApplicationEffect> _applications = [];
    private readonly CancellationTokenSource? _cancellation;
    private readonly ReliableManualTimeProvider _time;
    private int _applyCalls;
    private int _deactivateCalls;
    private int _monitorDelayCalls;

    public ReliableDailyUseFakeRuntime(
        ReliableManualTimeProvider time,
        CancellationTokenSource? cancellation = null)
    {
        _time = time;
        _cancellation = cancellation;
    }

    public static CalibrationWizardDeviceSet MatchingDevices { get; } = new(
        "TOUCH-LEFT",
        "TOUCH-RIGHT",
        ["TRACKER-LEFT", "TRACKER-RIGHT"]);

    public List<string> Journal { get; } = [];

    public List<TimeSpan> Delays { get; } = [];

    public List<int> AppliedTransientHandles { get; } = [];

    public HashSet<CalibrationWizardHand> ActiveVmtHands { get; } = [];

    public HashSet<CalibrationWizardHand> ActiveOverrideHands { get; } = [];

    public Func<RuntimeApplicationState>? StateProvider { get; set; }

    public TimeSpan ExpectedMonitorInterval { get; set; } = TimeSpan.FromMilliseconds(17);

    public int? CancelOnMonitorDelayNumber { get; set; }

    public int? FailApplyCall { get; set; }

    public int? FailDeactivateCall { get; set; }

    public int? StopSteamVrOnWaitCall { get; set; }

    public int? HangDeactivateCall { get; set; }

    public int? FailHealthCheckCall { get; set; }

    public CalibrationWizardHand? FailRollbackOverrideHand { get; set; }

    public CalibrationWizardHand? HangRollbackOverrideHand { get; set; }

    public CalibrationWizardHand? FailReleaseOverrideHand { get; set; }

    public int CurrentTransientHandle { get; private set; }

    public int DependencyCheckCalls { get; private set; }

    public int WaitForSteamVrCalls { get; private set; }

    public int WaitForDevicesCalls { get; private set; }

    public int HealthCheckCalls { get; private set; }

    public int StaleApplyAttempts { get; private set; }

    public void QueueDependencies(params CalibrationWizardDependencyStatus[] statuses)
    {
        foreach (var status in statuses)
        {
            _dependencyStatuses.Enqueue(status);
        }
    }

    public void QueueDeviceSet(CalibrationWizardDeviceSet devices, int transientHandle)
    {
        _deviceReadiness.Enqueue(DailyUseDeviceReadiness.Ready(devices));
        _transientHandles.Enqueue(transientHandle);
    }

    public void QueueDeviceReadiness(params DailyUseDeviceReadiness[] observations)
    {
        foreach (var observation in observations)
        {
            _deviceReadiness.Enqueue(observation);
        }
    }

    public void QueueHealth(params RuntimeHealthSnapshot[] snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            _healthSnapshots.Enqueue(snapshot);
        }
    }

    public Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DependencyCheckCalls++;
        Journal.Add("dependency-check");
        return Task.FromResult(_dependencyStatuses.Count == 0
            ? new CalibrationWizardDependencyStatus(true, true, "dependencies ready")
            : _dependencyStatuses.Dequeue());
    }

    public Task WaitForSteamVrAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WaitForSteamVrCalls++;
        Journal.Add("wait-steamvr");
        if (WaitForSteamVrCalls == StopSteamVrOnWaitCall)
        {
            throw new ReliableDailyUseSteamVrStoppedException(
                "synthetic terminal SteamVR session during recovery");
        }

        return Task.CompletedTask;
    }

    public Task<DailyUseDeviceReadiness> ProbeDeviceReadinessAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WaitForDevicesCalls++;
        var readiness = _deviceReadiness.Count == 0
            ? DailyUseDeviceReadiness.Ready(MatchingDevices)
            : _deviceReadiness.Dequeue();
        if (!readiness.IsReady)
        {
            Journal.Add($"devices-unavailable:{readiness.UnavailableCode}");
            return Task.FromResult(readiness);
        }

        var devices = readiness.Devices!;
        CurrentTransientHandle = _transientHandles.Count == 0
            ? (CurrentTransientHandle == 0 ? 3 : CurrentTransientHandle)
            : _transientHandles.Dequeue();
        Journal.Add($"devices:{CurrentTransientHandle}");
        return Task.FromResult(DailyUseDeviceReadiness.Ready(devices));
    }

    public DailyUseProfileApplication CreateProfileApplication(
        CalibrationWizardProfileView profile)
    {
        var application = new DailyUseProfileApplication(profile);
        _applications.Add(
            application.OperationId,
            new ApplicationEffect(profile.Hand, CurrentTransientHandle));
        Journal.Add($"create:{Format(profile.Hand)}:{CurrentTransientHandle}");
        return application;
    }

    public Task ApplyProfileAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effect = Effect(application);
        _applyCalls++;
        if (effect.TransientHandle != CurrentTransientHandle)
        {
            StaleApplyAttempts++;
            throw new InvalidOperationException(
                $"application captured stale handle {effect.TransientHandle}; current is {CurrentTransientHandle}");
        }

        AppliedTransientHandles.Add(effect.TransientHandle);
        ActiveVmtHands.Add(effect.Hand);
        ActiveOverrideHands.Add(effect.Hand);
        Journal.Add($"apply:{Format(effect.Hand)}:{effect.TransientHandle}");
        if (_applyCalls == FailApplyCall)
        {
            throw new IOException($"synthetic {Format(effect.Hand)} apply failure after effects");
        }

        return Task.CompletedTask;
    }

    public Task DeactivateProfileAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        var effect = Effect(application);
        _deactivateCalls++;
        Journal.Add($"deactivate:{Format(effect.Hand)}:{effect.TransientHandle}");
        if (_deactivateCalls == HangDeactivateCall)
        {
            return new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }

        if (_deactivateCalls == FailDeactivateCall)
        {
            throw new IOException($"synthetic {Format(effect.Hand)} VMT deactivation failure");
        }

        ActiveVmtHands.Remove(effect.Hand);
        return Task.CompletedTask;
    }

    public Task RollbackProfileOverrideAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        var effect = Effect(application);
        Journal.Add($"rollback-override:{Format(effect.Hand)}:{effect.TransientHandle}");
        if (HangRollbackOverrideHand == effect.Hand)
        {
            return new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }

        ActiveOverrideHands.Remove(effect.Hand);
        if (FailRollbackOverrideHand == effect.Hand)
        {
            throw new IOException($"synthetic {Format(effect.Hand)} override rollback report");
        }

        return Task.CompletedTask;
    }

    public Task ReleaseProfileOverrideAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken)
    {
        var effect = Effect(application);
        Journal.Add($"release-override:{Format(effect.Hand)}:{effect.TransientHandle}");
        ActiveOverrideHands.Remove(effect.Hand);
        if (FailReleaseOverrideHand == effect.Hand)
        {
            throw new IOException($"synthetic {Format(effect.Hand)} override release report");
        }

        return Task.CompletedTask;
    }

    public Task<RuntimeHealthSnapshot> CheckHealthAsync(
        IReadOnlyList<DailyUseProfileApplication> activeApplications,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HealthCheckCalls++;
        var effects = activeApplications.Select(Effect).ToArray();
        if (effects.Any(effect => effect.TransientHandle != CurrentTransientHandle))
        {
            throw new InvalidOperationException("watchdog received an application bound to a stale handle");
        }

        if (ActiveOverrideHands.Count != 2 || ActiveVmtHands.Count != 2)
        {
            throw new InvalidOperationException("Active was observed without both VMT and override effects");
        }

        Journal.Add($"health:{CurrentTransientHandle}:{HealthCheckCalls}");
        if (HealthCheckCalls == FailHealthCheckCall)
        {
            throw new IOException("synthetic monitor adapter failure");
        }

        return Task.FromResult(_healthSnapshots.Count == 0
            ? RuntimeHealthSnapshot.Healthy()
            : _healthSnapshots.Dequeue());
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        _time.Advance(delay);
        var state = StateProvider?.Invoke();
        Journal.Add($"delay:{delay.TotalMilliseconds:R}:{state}");
        if (state != RuntimeApplicationState.Active && ActiveOverrideHands.Count != 0)
        {
            throw new InvalidOperationException(
                $"Safety checkpoint {state} retained {ActiveOverrideHands.Count} override(s)");
        }

        if (delay == ExpectedMonitorInterval)
        {
            _monitorDelayCalls++;
            if (_monitorDelayCalls == CancelOnMonitorDelayNumber)
            {
                _cancellation?.Cancel();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private ApplicationEffect Effect(DailyUseProfileApplication application) =>
        _applications.TryGetValue(application.OperationId, out var effect)
            ? effect
            : throw new InvalidOperationException("unknown profile-application receipt");

    private static string Format(CalibrationWizardHand hand) =>
        hand.ToString().ToLowerInvariant();

    private sealed record ApplicationEffect(
        CalibrationWizardHand Hand,
        int TransientHandle);
}

internal sealed class ReliableProfileBackend : ICalibrationWizardBackend
{
    public ReliableProfileBackend(IReadOnlyList<CalibrationWizardProfileView>? profiles = null)
    {
        Profiles = profiles ?? CreateProfiles();
    }

    public IReadOnlyList<CalibrationWizardProfileView> Profiles { get; }

    public CalibrationWizardProfileLookup FindReusableProfiles(
        CalibrationWizardDeviceSet devices) =>
        new(Profiles, "matched exact stable tracker serials and hands");

    public CalibrationWizardAnalysis AnalyzeFirstRun(
        CalibrationWizardDeviceSet devices,
        CalibrationWizardCapture leftCapture,
        CalibrationWizardCapture rightCapture) =>
        throw new NotSupportedException("Daily-use tests never perform calibration capture.");

    public IReadOnlyList<CalibrationWizardProfileView> SaveProfiles(
        CalibrationWizardAnalysis analysis) =>
        throw new NotSupportedException("Daily-use tests never persist calibration profiles.");

    public static IReadOnlyList<CalibrationWizardProfileView> CreateProfiles() =>
    [
        Profile(CalibrationWizardHand.Left, "TOUCH-LEFT", "TRACKER-LEFT"),
        Profile(CalibrationWizardHand.Right, "TOUCH-RIGHT", "TRACKER-RIGHT"),
    ];

    private static CalibrationWizardProfileView Profile(
        CalibrationWizardHand hand,
        string controllerSerial,
        string trackerSerial) =>
        new(
            $"Reliable {hand} profile",
            hand,
            controllerSerial,
            trackerSerial,
            CalibrationModel.FullSixDof,
            "validated synthetic profile",
            new RigidTransform(
                Quaternion.Identity,
                hand == CalibrationWizardHand.Left
                    ? new Vector3(0.01f, -0.04f, 0.02f)
                    : new Vector3(-0.01f, -0.04f, 0.02f)),
            10d,
            new CalibrationQualityMetrics(0.5d, 0.8d, 4d, 6d, 20d, 0.045d)
            {
                RotationInlierRatio = 0.99d,
                TranslationInlierRatio = 0.98d,
                TranslationSplitDisagreementMillimeters = 0.5d,
            },
            new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero));
}

internal sealed class ReliableManualTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _origin =
        new(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
    private long _elapsedTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _elapsedTicks;

    public override DateTimeOffset GetUtcNow() =>
        _origin.AddTicks(_elapsedTicks);

    public void Advance(TimeSpan elapsed) =>
        _elapsedTicks += elapsed.Ticks;
}
