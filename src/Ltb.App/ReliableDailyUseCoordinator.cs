using Ltb.Core;

namespace Ltb.App;

internal enum ReliableDailyUseStopReason
{
    Cancellation,
    SteamVrStopped,
    ProfileUnavailable,
    ProfileApplyFailed,
    SafeDisableFailed,
    RuntimeFailure,
}

internal sealed record ReliableDailyUseResult(
    ReliableDailyUseStopReason StopReason,
    RuntimeApplicationState FinalState,
    IReadOnlyList<RuntimeApplicationState> StateHistory,
    string Diagnostic,
    IReadOnlyList<Exception> SafeDisableFailures,
    IReadOnlyList<Exception> RollbackFailures);

internal sealed class ReliableDailyUseSteamVrStoppedException : Exception
{
    public ReliableDailyUseSteamVrStoppedException(string message)
        : base(message)
    {
    }
}

internal sealed record DailyUseDeviceReadiness
{
    private DailyUseDeviceReadiness(
        CalibrationWizardDeviceSet? devices,
        LtbDiagnosticCode? unavailableCode,
        string diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        if ((devices is null) == (unavailableCode is null))
        {
            throw new ArgumentException(
                "Readiness must contain either devices or an unavailable diagnostic.");
        }

        if (unavailableCode is not null &&
            unavailableCode is not (
                LtbDiagnosticCode.DependencyUnavailable or
                LtbDiagnosticCode.DevicesUnavailable or
                LtbDiagnosticCode.VmtUnavailable))
        {
            throw new ArgumentOutOfRangeException(nameof(unavailableCode));
        }

        Devices = devices;
        UnavailableCode = unavailableCode;
        Diagnostic = diagnostic;
    }

    public CalibrationWizardDeviceSet? Devices { get; }

    public LtbDiagnosticCode? UnavailableCode { get; }

    public string Diagnostic { get; }

    public bool IsReady => Devices is not null;

    public static DailyUseDeviceReadiness Ready(
        CalibrationWizardDeviceSet devices,
        string diagnostic = "required devices and VMT are ready") =>
        new(devices ?? throw new ArgumentNullException(nameof(devices)), null, diagnostic);

    public static DailyUseDeviceReadiness Unavailable(
        LtbDiagnosticCode code,
        string diagnostic) =>
        new(null, code, diagnostic);
}

/// <summary>
/// A handle is allocated before an apply attempt touches VMT or SteamVR. The
/// runtime must retain enough operation state to deactivate the touched VMT
/// slot and roll back any settings recovery point even when apply throws.
/// </summary>
internal sealed class DailyUseProfileApplication
{
    public DailyUseProfileApplication(CalibrationWizardProfileView profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        OperationId = Guid.NewGuid();
    }

    public Guid OperationId { get; }

    public CalibrationWizardProfileView Profile { get; }
}

/// <summary>
/// Fakeable daily-use integration boundary. Cleanup and rollback methods must
/// be bounded. VMT and settings operations are separate so the coordinator can
/// attempt every cleanup surface even after one operation fails.
/// </summary>
internal interface IReliableDailyUseRuntime
{
    Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken);

    Task WaitForSteamVrAsync(CancellationToken cancellationToken);

    Task<DailyUseDeviceReadiness> ProbeDeviceReadinessAsync(
        CancellationToken cancellationToken);

    DailyUseProfileApplication CreateProfileApplication(
        CalibrationWizardProfileView profile);

    Task ApplyProfileAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken);

    Task DeactivateProfileAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken);

    Task RollbackProfileOverrideAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken);

    Task ReleaseProfileOverrideAsync(
        DailyUseProfileApplication application,
        CancellationToken cancellationToken);

    Task<RuntimeHealthSnapshot> CheckHealthAsync(
        IReadOnlyList<DailyUseProfileApplication> activeApplications,
        CancellationToken cancellationToken);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed record ReliableDailyUseOptions
{
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan ReconnectRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (MonitorInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MonitorInterval));
        }

        if (ReconnectRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ReconnectRetryDelay));
        }

        if (CleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CleanupTimeout));
        }
    }
}

/// <summary>
/// Milestone 4 later-run coordinator. It never considers an application active
/// until both hands commit, and it removes every active override before waiting
/// for a lost device to return.
/// </summary>
internal sealed class ReliableDailyUseCoordinator
{
    private readonly IReliableDailyUseRuntime _runtime;
    private readonly ICalibrationWizardBackend _backend;
    private readonly ILtbLogSink _logSink;
    private readonly ReliableDailyUseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly List<RuntimeApplicationState> _history = [];

    public ReliableDailyUseCoordinator(
        IReliableDailyUseRuntime runtime,
        ICalibrationWizardBackend backend,
        ILtbLogSink? logSink = null,
        ReliableDailyUseOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logSink = logSink ?? NullLtbLogSink.Instance;
        _options = options ?? new ReliableDailyUseOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RuntimeApplicationState CurrentState { get; private set; } =
        RuntimeApplicationState.Stopped;

    public IReadOnlyList<RuntimeApplicationState> StateHistory => _history.AsReadOnly();

    public async Task<ReliableDailyUseResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        _history.Clear();
        CurrentState = RuntimeApplicationState.Stopped;
        RecordCurrentState("daily-use coordinator is stopped before startup");

        IReadOnlyList<CalibrationWizardProfileView>? profiles = null;
        IReadOnlyList<DailyUseProfileApplication> activeApplications =
            Array.Empty<DailyUseProfileApplication>();
        var accumulatedRollbackFailures = new List<Exception>();

        try
        {
            var devices = await WaitForCompleteStartupAsync(
                    expectedProfiles: null,
                    cancellationToken)
                .ConfigureAwait(false);

            Transition(
                RuntimeApplicationState.Ready,
                "dependencies and the two-hand device set are ready");
            CalibrationWizardProfileLookup lookup;
            try
            {
                lookup = _backend.FindReusableProfiles(devices);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log(
                    LtbLogLevel.Error,
                    LtbDiagnosticCode.ProfileUnavailable,
                    $"Stored profiles could not be evaluated safely: {exception.Message}");
                return Result(
                    ReliableDailyUseStopReason.ProfileUnavailable,
                    $"Stored profiles could not be evaluated safely: {exception.Message}",
                    rollbackFailures: accumulatedRollbackFailures);
            }

            if (!lookup.HasCompletePair)
            {
                Log(
                    LtbLogLevel.Warning,
                    LtbDiagnosticCode.ProfileUnavailable,
                    lookup.Diagnostic);
                return Result(
                    ReliableDailyUseStopReason.ProfileUnavailable,
                    lookup.Diagnostic,
                    rollbackFailures: accumulatedRollbackFailures);
            }

            profiles = NormalizeProfiles(lookup.Profiles);

            while (true)
            {
                Transition(
                    RuntimeApplicationState.ApplyProfile,
                    "applying the two serial-matched profiles as one transaction");
                var apply = await ApplyProfilesTransactionAsync(profiles, cancellationToken)
                    .ConfigureAwait(false);
                accumulatedRollbackFailures.AddRange(apply.RollbackFailures);
                if (!apply.Success)
                {
                    Log(
                        LtbLogLevel.Error,
                        LtbDiagnosticCode.ProfileApplyFailed,
                        $"Profile application failed: {apply.Failure!.Message}");
                    if (apply.RollbackFailures.Count > 0)
                    {
                        Transition(
                            RuntimeApplicationState.Stopped,
                            "profile rollback is incomplete; manual state inspection is required");
                    }
                    else if (apply.Failure is OperationCanceledException &&
                             cancellationToken.IsCancellationRequested)
                    {
                        Transition(
                            RuntimeApplicationState.Stopped,
                            "profile application was cancelled and rolled back cleanly");
                    }
                    else
                    {
                        Transition(
                            RuntimeApplicationState.Ready,
                            "profile application rolled back; correct the diagnostic and retry");
                    }

                    var stopReason = apply.Failure is OperationCanceledException &&
                                     cancellationToken.IsCancellationRequested
                        ? ReliableDailyUseStopReason.Cancellation
                        : ReliableDailyUseStopReason.ProfileApplyFailed;
                    return Result(
                        stopReason,
                        apply.Failure.Message,
                        rollbackFailures: accumulatedRollbackFailures);
                }

                activeApplications = apply.Applications;
                var health = await _runtime.CheckHealthAsync(
                        activeApplications,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (health.IsHealthy)
                {
                    Log(
                        LtbLogLevel.Information,
                        LtbDiagnosticCode.ProfileApplied,
                        "Both hand profiles passed the post-apply health gate and were committed.",
                        new Dictionary<string, string>
                        {
                            ["profileCount"] = activeApplications.Count.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                        });
                    Transition(
                        RuntimeApplicationState.Active,
                        "both serial-matched hand profiles are active");

                    health = await MonitorUntilFailureAsync(
                            activeApplications,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                LogHealthFailure(health);

                var safeDisableFailures = await SafeDisableAsync(activeApplications)
                    .ConfigureAwait(false);
                activeApplications = Array.Empty<DailyUseProfileApplication>();
                if (safeDisableFailures.Count > 0)
                {
                    Transition(
                        RuntimeApplicationState.Stopped,
                        "SafeDisable is incomplete; manual state inspection is required");
                    return Result(
                        ReliableDailyUseStopReason.SafeDisableFailed,
                        health.Diagnostic,
                        safeDisableFailures,
                        accumulatedRollbackFailures);
                }

                if (health.FailureKind == RuntimeHealthFailureKind.SteamVrStopped)
                {
                    Transition(
                        RuntimeApplicationState.Stopped,
                        "SteamVR stopped after active overrides were disabled");
                    return Result(
                        ReliableDailyUseStopReason.SteamVrStopped,
                        health.Diagnostic,
                        rollbackFailures: accumulatedRollbackFailures);
                }

                if (health.FailureKind == RuntimeHealthFailureKind.VmtUnavailable)
                {
                    var reconnectedDevices = await WaitForCompleteStartupAsync(
                            profiles,
                            cancellationToken)
                        .ConfigureAwait(false);
                    EnsureDevicesMatchProfiles(reconnectedDevices, profiles);
                }
                else
                {
                    Transition(
                        RuntimeApplicationState.WaitingForDevices,
                        "waiting for the exact serial-matched devices to reconnect");
                    _ = await WaitForDevicesAsync(profiles, cancellationToken)
                        .ConfigureAwait(false);
                }

                Log(
                    LtbLogLevel.Information,
                    LtbDiagnosticCode.Reconnected,
                    "The exact serial-matched devices and dependencies were reacquired.");
                Transition(
                    RuntimeApplicationState.Ready,
                    "reacquired devices are ready for a clean profile reapplication");
            }
        }
        catch (ReliableDailyUseSteamVrStoppedException exception)
        {
            Log(
                LtbLogLevel.Error,
                LtbDiagnosticCode.SteamVrStopped,
                exception.Message);
            var safeDisableFailures = activeApplications.Count == 0
                ? Array.Empty<Exception>()
                : await SafeDisableAsync(activeApplications).ConfigureAwait(false);
            activeApplications = Array.Empty<DailyUseProfileApplication>();
            Transition(
                RuntimeApplicationState.Stopped,
                safeDisableFailures.Count == 0
                    ? "SteamVR stopped; the current run will not reopen or reapply"
                    : "SteamVR stopped and bounded cleanup reported failures");
            return Result(
                safeDisableFailures.Count == 0
                    ? ReliableDailyUseStopReason.SteamVrStopped
                    : ReliableDailyUseStopReason.SafeDisableFailed,
                exception.Message,
                safeDisableFailures,
                accumulatedRollbackFailures);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log(
                LtbLogLevel.Information,
                LtbDiagnosticCode.ShutdownRequested,
                "Clean shutdown was requested.");
            var safeDisableFailures = activeApplications.Count == 0
                ? Array.Empty<Exception>()
                : await SafeDisableAsync(activeApplications).ConfigureAwait(false);
            Transition(
                RuntimeApplicationState.Stopped,
                safeDisableFailures.Count == 0
                    ? "clean shutdown completed"
                    : "clean shutdown attempted all cleanup but reported failures");
            return Result(
                safeDisableFailures.Count == 0
                    ? ReliableDailyUseStopReason.Cancellation
                    : ReliableDailyUseStopReason.SafeDisableFailed,
                safeDisableFailures.Count == 0
                    ? "Cancellation requested; active profiles were safely disabled."
                    : "Cancellation requested; SafeDisable cleanup is incomplete.",
                safeDisableFailures,
                accumulatedRollbackFailures);
        }
        catch (Exception exception)
        {
            Log(
                LtbLogLevel.Error,
                LtbDiagnosticCode.RuntimeFailure,
                "An unexpected daily-use runtime operation failed.",
                new Dictionary<string, string>
                {
                    ["exceptionType"] = exception.GetType().Name,
                    ["exceptionMessage"] = exception.Message,
                });
            var safeDisableFailures = activeApplications.Count == 0
                ? Array.Empty<Exception>()
                : await SafeDisableAsync(activeApplications).ConfigureAwait(false);
            Transition(
                RuntimeApplicationState.Stopped,
                "an unexpected runtime failure stopped the daily-use coordinator");
            return Result(
                safeDisableFailures.Count == 0
                    ? ReliableDailyUseStopReason.RuntimeFailure
                    : ReliableDailyUseStopReason.SafeDisableFailed,
                exception.Message,
                safeDisableFailures,
                accumulatedRollbackFailures);
        }
    }

    private async Task<CalibrationWizardDeviceSet> WaitForCompleteStartupAsync(
        IReadOnlyList<CalibrationWizardProfileView>? expectedProfiles,
        CancellationToken cancellationToken)
    {
        Transition(
            RuntimeApplicationState.DependencyCheck,
            "checking ALVR and VMT dependencies");
        while (true)
        {
            var status = await _runtime.CheckDependenciesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (status.IsReady)
            {
                break;
            }

            Log(
                LtbLogLevel.Warning,
                LtbDiagnosticCode.DependencyUnavailable,
                status.Diagnostic);
            await _runtime.DelayAsync(_options.ReconnectRetryDelay, cancellationToken)
                .ConfigureAwait(false);
        }

        Transition(
            RuntimeApplicationState.WaitingForSteamVR,
            "waiting for the SteamVR runtime");
        await _runtime.WaitForSteamVrAsync(cancellationToken).ConfigureAwait(false);

        Transition(
            RuntimeApplicationState.WaitingForDevices,
            expectedProfiles is null
                ? "waiting for two supported Meta Touch controllers and two Lighthouse " +
                  "pose-source candidates"
                : "waiting for the exact serial-matched devices to reconnect");
        return await WaitForDevicesAsync(expectedProfiles, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CalibrationWizardDeviceSet> WaitForDevicesAsync(
        IReadOnlyList<CalibrationWizardProfileView>? expectedProfiles,
        CancellationToken cancellationToken)
    {
        string? lastDiagnosticKey = null;
        while (true)
        {
            var readiness = await _runtime.ProbeDeviceReadinessAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!readiness.IsReady)
            {
                var code = readiness.UnavailableCode!.Value;
                LogReadinessOnce(code, readiness.Diagnostic, ref lastDiagnosticKey);
                await _runtime.DelayAsync(_options.ReconnectRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var devices = readiness.Devices!;
            try
            {
                devices.Validate();
                if (expectedProfiles is not null)
                {
                    EnsureDevicesMatchProfiles(devices, expectedProfiles);
                }

                return devices;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var code = expectedProfiles is null
                        ? LtbDiagnosticCode.DevicesUnavailable
                        : LtbDiagnosticCode.ReconnectWaiting;
                    LogReadinessOnce(code, exception.Message, ref lastDiagnosticKey);
                    await _runtime.DelayAsync(_options.ReconnectRetryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
        }
    }

    private void LogReadinessOnce(
        LtbDiagnosticCode code,
        string diagnostic,
        ref string? lastDiagnosticKey)
    {
        var key = $"{code}\n{diagnostic}";
        if (string.Equals(lastDiagnosticKey, key, StringComparison.Ordinal))
        {
            return;
        }

        lastDiagnosticKey = key;
        Log(LtbLogLevel.Warning, code, diagnostic);
    }

    private async Task<RuntimeHealthSnapshot> MonitorUntilFailureAsync(
        IReadOnlyList<DailyUseProfileApplication> activeApplications,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await _runtime.DelayAsync(_options.MonitorInterval, cancellationToken)
                .ConfigureAwait(false);
            var health = await _runtime.CheckHealthAsync(
                    activeApplications,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!health.IsHealthy)
            {
                return health;
            }
        }
    }

    private async Task<ProfileApplyAttempt> ApplyProfilesTransactionAsync(
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        CancellationToken cancellationToken)
    {
        var touched = new List<DailyUseProfileApplication>(profiles.Count);
        try
        {
            foreach (var profile in profiles.OrderBy(profile => profile.Hand))
            {
                var application = _runtime.CreateProfileApplication(profile)
                    ?? throw new InvalidOperationException(
                        "The daily-use runtime returned a null profile-application handle.");
                if (!ReferenceEquals(application.Profile, profile) &&
                    application.Profile != profile)
                {
                    throw new InvalidOperationException(
                        "The daily-use runtime created a handle for a different profile.");
                }

                // Add before apply: a throw after the first external side effect
                // must still deactivate this touched VMT slot and settings entry.
                touched.Add(application);
                await _runtime.ApplyProfileAsync(application, cancellationToken)
                    .ConfigureAwait(false);
            }

            return ProfileApplyAttempt.Succeeded(touched.AsReadOnly());
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var application in touched.AsEnumerable().Reverse())
            {
                var deactivateFailure = await RunBoundedCleanupAsync(
                        token => _runtime.DeactivateProfileAsync(application, token),
                        $"rollback VMT deactivation for {application.Profile.Hand}")
                    .ConfigureAwait(false);
                if (deactivateFailure is not null)
                {
                    rollbackFailures.Add(new InvalidOperationException(
                        $"Rollback VMT deactivation failed for {application.Profile.Hand} profile " +
                        $"'{application.Profile.ProfileName}': {deactivateFailure.Message}",
                        deactivateFailure));
                }

                var overrideFailure = await RunBoundedCleanupAsync(
                        token => _runtime.RollbackProfileOverrideAsync(application, token),
                        $"TrackingOverride rollback for {application.Profile.Hand}")
                    .ConfigureAwait(false);
                if (overrideFailure is not null)
                {
                    rollbackFailures.Add(new InvalidOperationException(
                        $"TrackingOverride rollback failed for {application.Profile.Hand} profile " +
                        $"'{application.Profile.ProfileName}': {overrideFailure.Message}",
                        overrideFailure));
                }
            }

            Log(
                rollbackFailures.Count == 0 ? LtbLogLevel.Warning : LtbLogLevel.Error,
                rollbackFailures.Count == 0
                    ? LtbDiagnosticCode.RollbackCompleted
                    : LtbDiagnosticCode.RollbackFailed,
                rollbackFailures.Count == 0
                    ? "Partial profile application was rolled back in reverse order."
                    : $"Profile rollback reported {rollbackFailures.Count} failure(s).");
            return ProfileApplyAttempt.Failed(failure, rollbackFailures.AsReadOnly());
        }
    }

    private async Task<IReadOnlyList<Exception>> SafeDisableAsync(
        IReadOnlyList<DailyUseProfileApplication> applications)
    {
        Transition(
            RuntimeApplicationState.SafeDisable,
            "disabling every active profile before leaving Active");
        Log(
            LtbLogLevel.Warning,
            LtbDiagnosticCode.SafeDisableStarted,
            "SafeDisable started for all active hand profiles.");

        var failures = new List<Exception>();
        foreach (var application in applications.AsEnumerable().Reverse())
        {
            var deactivateFailure = await RunBoundedCleanupAsync(
                    token => _runtime.DeactivateProfileAsync(application, token),
                    $"SafeDisable VMT deactivation for {application.Profile.Hand}")
                .ConfigureAwait(false);
            if (deactivateFailure is not null)
            {
                failures.Add(new InvalidOperationException(
                    $"SafeDisable VMT deactivation failed for {application.Profile.Hand} profile " +
                    $"'{application.Profile.ProfileName}': {deactivateFailure.Message}",
                    deactivateFailure));
            }

            var releaseFailure = await RunBoundedCleanupAsync(
                    token => _runtime.ReleaseProfileOverrideAsync(application, token),
                    $"SafeDisable TrackingOverride release for {application.Profile.Hand}")
                .ConfigureAwait(false);
            if (releaseFailure is not null)
            {
                failures.Add(new InvalidOperationException(
                    $"SafeDisable TrackingOverride release failed for " +
                    $"{application.Profile.Hand} profile '{application.Profile.ProfileName}': " +
                    releaseFailure.Message,
                    releaseFailure));
            }
        }

        Log(
            failures.Count == 0 ? LtbLogLevel.Information : LtbLogLevel.Error,
            failures.Count == 0
                ? LtbDiagnosticCode.SafeDisableCompleted
                : LtbDiagnosticCode.SafeDisableFailed,
            failures.Count == 0
                ? "SafeDisable completed for every active hand profile."
                : $"SafeDisable attempted every hand and reported {failures.Count} failure(s).");
        return failures.AsReadOnly();
    }

    private async Task<Exception?> RunBoundedCleanupAsync(
        Func<CancellationToken, Task> operation,
        string operationName)
    {
        using var timeoutStop = new CancellationTokenSource(_options.CleanupTimeout);
        Task operationTask;
        try
        {
            operationTask = operation(timeoutStop.Token)
                ?? throw new InvalidOperationException(
                    $"{operationName} returned a null task.");
        }
        catch (Exception exception)
        {
            return exception;
        }

        try
        {
            await operationTask.WaitAsync(_options.CleanupTimeout).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException exception) when (timeoutStop.IsCancellationRequested)
        {
            ObserveLateFailure(operationTask);
            return new TimeoutException(
                $"{operationName} exceeded the {_options.CleanupTimeout.TotalSeconds:R}-second cleanup timeout.",
                exception);
        }
        catch (TimeoutException exception)
        {
            await timeoutStop.CancelAsync().ConfigureAwait(false);
            ObserveLateFailure(operationTask);
            return new TimeoutException(
                $"{operationName} exceeded the {_options.CleanupTimeout.TotalSeconds:R}-second cleanup timeout.",
                exception);
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void ObserveLateFailure(Task operationTask)
    {
        _ = operationTask.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static IReadOnlyList<CalibrationWizardProfileView> NormalizeProfiles(
        IReadOnlyList<CalibrationWizardProfileView> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var normalized = profiles.OrderBy(profile => profile.Hand).ToArray();
        if (normalized.Length != 2 ||
            normalized.Select(profile => profile.Hand).Distinct().Count() != 2 ||
            normalized.Select(profile => profile.TrackerSerial)
                .Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new InvalidOperationException(
                "Daily use requires one profile per hand with two distinct tracker serials.");
        }

        return Array.AsReadOnly(normalized);
    }

    private static void EnsureDevicesMatchProfiles(
        CalibrationWizardDeviceSet devices,
        IReadOnlyList<CalibrationWizardProfileView> profiles)
    {
        var left = profiles.Single(profile => profile.Hand == CalibrationWizardHand.Left);
        var right = profiles.Single(profile => profile.Hand == CalibrationWizardHand.Right);
        var expectedTrackers = profiles
            .Select(profile => profile.TrackerSerial)
            .OrderBy(serial => serial, StringComparer.Ordinal)
            .ToArray();
        var observedTrackers = devices.TrackerSerials
            .OrderBy(serial => serial, StringComparer.Ordinal)
            .ToArray();
        if (!expectedTrackers.SequenceEqual(observedTrackers, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The reacquired tracker serial set does not match the active profiles.");
        }

        if ((!string.IsNullOrEmpty(left.ControllerSerial) &&
             !string.Equals(
                 left.ControllerSerial,
                 devices.LeftControllerSerial,
                 StringComparison.Ordinal)) ||
            (!string.IsNullOrEmpty(right.ControllerSerial) &&
             !string.Equals(
                 right.ControllerSerial,
                 devices.RightControllerSerial,
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The reacquired Touch controller serials do not match the active profiles.");
        }
    }

    private void LogHealthFailure(RuntimeHealthSnapshot health)
    {
        var properties = new Dictionary<string, string>();
        if (health.Hand is not null)
        {
            properties["hand"] = health.Hand;
        }

        if (health.DeviceIdentity is not null)
        {
            properties["deviceIdentity"] = health.DeviceIdentity;
        }

        Log(
            health.FailureKind == RuntimeHealthFailureKind.SteamVrStopped
                ? LtbLogLevel.Error
                : LtbLogLevel.Warning,
            health.FailureKind switch
            {
                RuntimeHealthFailureKind.TrackerLost => LtbDiagnosticCode.TrackerLost,
                RuntimeHealthFailureKind.TouchInputLost => LtbDiagnosticCode.TouchInputLost,
                RuntimeHealthFailureKind.VmtUnavailable => LtbDiagnosticCode.VmtUnavailable,
                RuntimeHealthFailureKind.SteamVrStopped => LtbDiagnosticCode.SteamVrStopped,
                _ => throw new InvalidOperationException(
                    "A healthy observation cannot terminate the watchdog."),
            },
            health.Diagnostic,
            properties);
    }

    private ReliableDailyUseResult Result(
        ReliableDailyUseStopReason stopReason,
        string diagnostic,
        IReadOnlyList<Exception>? safeDisableFailures = null,
        IReadOnlyList<Exception>? rollbackFailures = null) =>
        new(
            stopReason,
            CurrentState,
            Array.AsReadOnly(_history.ToArray()),
            diagnostic,
            safeDisableFailures ?? Array.Empty<Exception>(),
            rollbackFailures ?? Array.Empty<Exception>());

    private void RecordCurrentState(string diagnostic)
    {
        _history.Add(CurrentState);
        Log(
            LtbLogLevel.Information,
            LtbDiagnosticCode.StateTransition,
            diagnostic,
            new Dictionary<string, string>
            {
                ["state"] = CurrentState.ToString(),
            });
    }

    private void Transition(RuntimeApplicationState state, string diagnostic)
    {
        CurrentState = state;
        RecordCurrentState(diagnostic);
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
                CurrentState,
                message,
                properties));
        }
        catch
        {
            // A local log destination must never prevent SafeDisable or rollback.
        }
    }

    private sealed record ProfileApplyAttempt(
        bool Success,
        IReadOnlyList<DailyUseProfileApplication> Applications,
        Exception? Failure,
        IReadOnlyList<Exception> RollbackFailures)
    {
        public static ProfileApplyAttempt Succeeded(
            IReadOnlyList<DailyUseProfileApplication> applications) =>
            new(true, applications, null, Array.Empty<Exception>());

        public static ProfileApplyAttempt Failed(
            Exception failure,
            IReadOnlyList<Exception> rollbackFailures) =>
            new(
                false,
                Array.Empty<DailyUseProfileApplication>(),
                failure,
                rollbackFailures);
    }
}
