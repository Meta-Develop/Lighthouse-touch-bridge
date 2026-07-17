using Ltb.Core;

namespace Ltb.App;

/// <summary>
/// Optional lifecycle surface used by the production wizard. Once capture-path
/// override release starts, the wizard invokes this cleanup after every failure
/// or cancellation. Implementations must ignore caller cancellation and bound
/// every external cleanup operation independently.
/// </summary>
internal interface ICalibrationWizardCleanupRuntime
{
    Task<IReadOnlyList<Exception>> SafeDisableAsync();
}

/// <summary>
/// Narrow injected live boundary for the production wizard composition. The
/// Windows implementation is also the daily-use runtime, so one object owns a
/// single OpenVR session, VMT response pump, and settings-manager lifecycle.
/// </summary>
internal interface IProductionCalibrationWizardBackend : IReliableDailyUseRuntime
{
    Task DeactivateWizardVmtAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken);

    Task ReleaseWizardHandOverrideAsync(
        CalibrationWizardHand hand,
        CancellationToken cancellationToken);

    Task VerifyOriginalTouchPosesAsync(
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken);

    Task<CalibrationWizardCapture> CaptureWizardHandAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        double durationSeconds,
        double sampleRateHz,
        IProgress<CalibrationWizardCaptureProgress> progress,
        CancellationToken cancellationToken);
}

internal sealed record ProductionCalibrationWizardOptions
{
    public double CaptureDurationSeconds { get; init; } = 10d;

    public double CaptureRateHz { get; init; } = 90d;

    public TimeSpan DeviceRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (!double.IsFinite(CaptureDurationSeconds) || CaptureDurationSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(CaptureDurationSeconds));
        }

        if (!double.IsFinite(CaptureRateHz) || CaptureRateHz <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(CaptureRateHz));
        }

        if (DeviceRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DeviceRetryDelay));
        }

        if (CleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CleanupTimeout));
        }
    }
}

/// <summary>
/// Production composition adapter for <see cref="TwoHandCalibrationWizard"/>.
/// Association, solving, and persistence remain in
/// <see cref="FileCalibrationWizardBackend"/>; this type only coordinates the
/// live device/capture/application surfaces and retains the successful two-hand
/// application lease for the reliable-use watchdog.
/// </summary>
internal sealed class ProductionCalibrationWizardRuntime :
    ICalibrationWizardRuntime,
    ICalibrationWizardCleanupRuntime
{
    private readonly IProductionCalibrationWizardBackend _backend;
    private readonly ProductionCalibrationWizardOptions _options;
    private readonly TwoHandProfileApplicationTransaction _applicationTransaction;
    private readonly List<Exception> _pendingCleanupFailures = [];

    public ProductionCalibrationWizardRuntime(
        IProductionCalibrationWizardBackend backend,
        ProductionCalibrationWizardOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _options = options ?? new ProductionCalibrationWizardOptions();
        _options.Validate();
        _applicationTransaction = new TwoHandProfileApplicationTransaction(
            backend,
            _options.CleanupTimeout);
    }

    public TwoHandProfileApplicationLease? ActiveLease { get; private set; }

    public Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken) =>
        _backend.CheckDependenciesAsync(cancellationToken);

    public Task WaitForSteamVrAsync(CancellationToken cancellationToken) =>
        _backend.WaitForSteamVrAsync(cancellationToken);

    public async Task<CalibrationWizardDeviceSet> WaitForDevicesAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var readiness = await _backend.ProbeDeviceReadinessAsync(cancellationToken)
                .ConfigureAwait(false);
            if (readiness.IsReady)
            {
                readiness.Devices!.Validate();
                _currentDevices = readiness.Devices;
                return readiness.Devices;
            }

            await _backend.DelayAsync(_options.DeviceRetryDelay, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task ReleaseOverridesAsync(
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var failures = await RunCaptureSafetyPassAsync().ConfigureAwait(false);
        if (failures.Count > 0)
        {
            _pendingCleanupFailures.AddRange(failures);
            throw new AggregateException(
                "Capture-path override release did not safely complete for both hands.",
                failures);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _backend.VerifyOriginalTouchPosesAsync(devices, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<CalibrationWizardCapture> CaptureAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        IProgress<CalibrationWizardCaptureProgress> progress,
        CancellationToken cancellationToken) =>
        _backend.CaptureWizardHandAsync(
            hand,
            devices,
            _options.CaptureDurationSeconds,
            _options.CaptureRateHz,
            progress,
            cancellationToken);

    public async Task ApplyProfilesAsync(
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        CancellationToken cancellationToken)
    {
        if (ActiveLease is not null)
        {
            throw new InvalidOperationException(
                "The production wizard already owns an active two-hand application lease.");
        }

        var apply = await _applicationTransaction.ApplyAsync(profiles, cancellationToken)
            .ConfigureAwait(false);
        if (!apply.Success)
        {
            _pendingCleanupFailures.AddRange(apply.RollbackFailures);
            throw apply.Failure!;
        }

        var lease = apply.Lease!;
        RuntimeHealthSnapshot health;
        try
        {
            health = await _backend.CheckHealthAsync(
                    lease.Applications,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _pendingCleanupFailures.AddRange(
                await lease.SafeDisableAsync().ConfigureAwait(false));
            throw;
        }

        if (!health.IsHealthy)
        {
            _pendingCleanupFailures.AddRange(
                await lease.SafeDisableAsync().ConfigureAwait(false));
            throw new InvalidOperationException(
                "The two persisted profiles were applied, but the post-apply health gate " +
                $"rejected Active: {health.Diagnostic}");
        }

        ActiveLease = lease;
    }

    public async Task<IReadOnlyList<Exception>> SafeDisableAsync()
    {
        var failures = new List<Exception>(_pendingCleanupFailures);
        _pendingCleanupFailures.Clear();
        if (ActiveLease is not null)
        {
            try
            {
                failures.AddRange(await ActiveLease.SafeDisableAsync().ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"The active two-hand lease could not start SafeDisable: {exception.Message}",
                    exception));
            }
            finally
            {
                ActiveLease = null;
            }
        }

        failures.AddRange(await RunCaptureSafetyPassAsync().ConfigureAwait(false));
        return failures.AsReadOnly();
    }

    private async Task<IReadOnlyList<Exception>> RunCaptureSafetyPassAsync()
    {
        var failures = new List<Exception>();
        foreach (var hand in Enum.GetValues<CalibrationWizardHand>())
        {
            var releaseFailure = await RunBoundedCleanupAsync(
                    token => _backend.ReleaseWizardHandOverrideAsync(hand, token),
                    $"semantic-hand override release for {hand}")
                .ConfigureAwait(false);
            if (releaseFailure is not null)
            {
                failures.Add(releaseFailure);
            }
        }

        if (failures.Count > 0)
        {
            return failures.AsReadOnly();
        }

        foreach (var hand in Enum.GetValues<CalibrationWizardHand>())
        {
            var deactivateFailure = await RunBoundedCleanupAsync(
                    token => _backend.DeactivateWizardVmtAsync(
                        hand,
                        CurrentDevices,
                        token),
                    $"VMT deactivation after confirmed semantic-hand release for {hand}")
                .ConfigureAwait(false);
            if (deactivateFailure is not null)
            {
                failures.Add(deactivateFailure);
            }
        }

        return failures.AsReadOnly();
    }

    private CalibrationWizardDeviceSet CurrentDevices => _currentDevices ??
        throw new InvalidOperationException(
            "Wizard capture safety requires a successfully discovered device set.");

    private CalibrationWizardDeviceSet? _currentDevices;

    private async Task<Exception?> RunBoundedCleanupAsync(
        Func<CancellationToken, Task> operation,
        string operationName)
    {
        using var timeoutStop = new CancellationTokenSource(_options.CleanupTimeout);
        Task operationTask;
        try
        {
            operationTask = operation(timeoutStop.Token) ??
                throw new InvalidOperationException($"{operationName} returned a null task.");
        }
        catch (Exception exception)
        {
            return new InvalidOperationException($"{operationName} failed: {exception.Message}", exception);
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
            return new InvalidOperationException($"{operationName} failed: {exception.Message}", exception);
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
}
