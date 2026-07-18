using Ltb.Core;
using Ltb.Vmt;

namespace Ltb.App;

/// <summary>
/// Validated, UI-neutral inputs for one production two-hand wizard session.
/// Console and desktop callers share this contract so native composition,
/// watchdog, and cleanup policy remain in <c>Ltb.App</c>.
/// </summary>
public sealed record ProductionCalibrationWizardSessionOptions
{
    public const double MaximumCaptureDurationSeconds = 3_600d;
    public const double MaximumCaptureRateHz = 1_000d;
    public const double MaximumMonitorRateHz = 1_000d;
    public const double MaximumReconnectDelaySeconds = 300d;

    public string ProfileStorePath { get; init; } = string.Empty;

    public int LeftVmtSlot { get; init; } = -1;

    public int RightVmtSlot { get; init; } = -1;

    public string SteamVrSettingsPath { get; init; } = string.Empty;

    public double CaptureDurationSeconds { get; init; } = 10d;

    public double CaptureRateHz { get; init; } = 90d;

    public string? LogPath { get; init; }

    public double MonitorRateHz { get; init; } = 20d;

    public double ReconnectDelaySeconds { get; init; } = 0.25d;

    /// <summary>Validates the complete production contract without opening native resources.</summary>
    public bool TryValidate(out string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(ProfileStorePath))
        {
            diagnostic = "--profiles requires a non-empty profile-store path.";
            return false;
        }

        if (LeftVmtSlot is < VmtDeviceAddress.MinimumIndex or > VmtDeviceAddress.MaximumIndex ||
            RightVmtSlot is < VmtDeviceAddress.MinimumIndex or > VmtDeviceAddress.MaximumIndex)
        {
            diagnostic = $"VMT slots must be between {VmtDeviceAddress.MinimumIndex} and " +
                $"{VmtDeviceAddress.MaximumIndex}.";
            return false;
        }

        if (LeftVmtSlot == RightVmtSlot)
        {
            diagnostic = "--left-vmt-slot and --right-vmt-slot must be distinct.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SteamVrSettingsPath))
        {
            diagnostic = "--steamvr-settings requires a non-empty settings-file path.";
            return false;
        }

        if (!double.IsFinite(CaptureDurationSeconds) ||
            CaptureDurationSeconds <= 0d ||
            CaptureDurationSeconds > MaximumCaptureDurationSeconds)
        {
            diagnostic = "--duration must be greater than zero and at most " +
                $"{MaximumCaptureDurationSeconds:F0} seconds.";
            return false;
        }

        if (!double.IsFinite(CaptureRateHz) ||
            CaptureRateHz <= 0d ||
            CaptureRateHz > MaximumCaptureRateHz)
        {
            diagnostic = "--rate must be greater than zero and at most " +
                $"{MaximumCaptureRateHz:F0} Hz.";
            return false;
        }

        if (LogPath is not null && string.IsNullOrWhiteSpace(LogPath))
        {
            diagnostic = "--log must be omitted or contain a non-empty path.";
            return false;
        }

        if (!double.IsFinite(MonitorRateHz) ||
            MonitorRateHz <= 0d ||
            MonitorRateHz > MaximumMonitorRateHz)
        {
            diagnostic = "--monitor-rate must be greater than zero and at most " +
                $"{MaximumMonitorRateHz:F0} Hz.";
            return false;
        }

        if (!double.IsFinite(ReconnectDelaySeconds) ||
            ReconnectDelaySeconds <= 0d ||
            ReconnectDelaySeconds > MaximumReconnectDelaySeconds)
        {
            diagnostic = "--reconnect-delay must be greater than zero and at most " +
                $"{MaximumReconnectDelaySeconds:F0} seconds.";
            return false;
        }

        diagnostic = null;
        return true;
    }

    internal void Validate()
    {
        if (!TryValidate(out var diagnostic))
        {
            throw new ArgumentException(diagnostic, nameof(ProductionCalibrationWizardSessionOptions));
        }
    }
}

public enum ProductionCalibrationWizardStopReason
{
    WizardFailed,
    Cancellation,
    SteamVrStopped,
    RuntimeFailure,
    CleanupFailure,
}

/// <summary>
/// Complete result from calibration through watchdog termination and cleanup.
/// </summary>
public sealed record ProductionCalibrationWizardSessionResult(
    CalibrationWizardResult WizardResult,
    ProductionCalibrationWizardStopReason StopReason,
    string Diagnostic,
    IReadOnlyList<Exception> CleanupFailures)
{
    public int ExitCode => StopReason switch
    {
        ProductionCalibrationWizardStopReason.Cancellation => 0,
        ProductionCalibrationWizardStopReason.SteamVrStopped or
        ProductionCalibrationWizardStopReason.RuntimeFailure => 3,
        ProductionCalibrationWizardStopReason.CleanupFailure => 4,
        _ => WizardResult.CleanupFailures.Count > 0 ? 4 : 2,
    };

    /// <summary>Projects lifecycle completion onto the GUI's wizard result surface.</summary>
    public CalibrationWizardResult ToWizardResult()
    {
        if (!WizardResult.Success)
        {
            return WizardResult;
        }

        var cleanupFailures = WizardResult.CleanupFailures
            .Concat(CleanupFailures)
            .ToArray();
        var safelyStopped = StopReason == ProductionCalibrationWizardStopReason.Cancellation &&
            cleanupFailures.Length == 0;
        var finalState = cleanupFailures.Length == 0
            ? CalibrationWizardState.Stopped
            : CalibrationWizardState.SafeDisable;
        var history = WizardResult.StateHistory
            .Concat([CalibrationWizardState.SafeDisable, finalState])
            .ToArray();
        return WizardResult with
        {
            Success = safelyStopped,
            FinalState = finalState,
            StateHistory = history,
            Diagnostic = $"{WizardResult.Diagnostic} {Diagnostic}",
            CleanupFailures = cleanupFailures,
        };
    }
}

public interface IProductionCalibrationWizardSession : IAsyncDisposable
{
    Task<ProductionCalibrationWizardSessionResult> RunAsync(
        ICalibrationWizardOutput output,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Shared production composition entry point for the console and desktop GUI.
/// Native resources are opened only by this boundary; deterministic tests use
/// the internal backend-injection overload without live runtime calls.
/// </summary>
public static class ProductionCalibrationWizardSessionFactory
{
    public static IProductionCalibrationWizardSession Create(
        ProductionCalibrationWizardSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        JsonLinesLtbLogSink? ownedLog = null;
        try
        {
            ownedLog = options.LogPath is null
                ? null
                : new JsonLinesLtbLogSink(options.LogPath);
            var reconnectDelay = TimeSpan.FromSeconds(options.ReconnectDelaySeconds);
            var runtime = new ProductionReliableDailyUseRuntime(
                options.ProfileStorePath,
                options.SteamVrSettingsPath,
                new VmtDeviceAddress(options.LeftVmtSlot),
                new VmtDeviceAddress(options.RightVmtSlot),
                staleAfter: TimeSpan.FromSeconds(0.5d),
                reconnectDelay);
            return CreateCore(
                options,
                runtime,
                runtime,
                (ILtbLogSink?)ownedLog ?? NullLtbLogSink.Instance,
                ownedLog);
        }
        catch
        {
            ownedLog?.Dispose();
            throw;
        }
    }

    internal static IProductionCalibrationWizardSession CreateForBackend(
        ProductionCalibrationWizardSessionOptions options,
        IProductionCalibrationWizardBackend backend,
        ILtbLogSink? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        options.Validate();
        return CreateCore(
            options,
            backend,
            ownedBackend: null,
            logSink ?? NullLtbLogSink.Instance,
            ownedLog: null);
    }

    private static IProductionCalibrationWizardSession CreateCore(
        ProductionCalibrationWizardSessionOptions options,
        IProductionCalibrationWizardBackend backend,
        IAsyncDisposable? ownedBackend,
        ILtbLogSink logSink,
        IDisposable? ownedLog) =>
        new ProductionCalibrationWizardSession(
            options,
            backend,
            ownedBackend,
            logSink,
            ownedLog);
}

internal sealed class ProductionCalibrationWizardSession :
    IProductionCalibrationWizardSession
{
    private readonly ProductionCalibrationWizardSessionOptions _options;
    private readonly IProductionCalibrationWizardBackend _backend;
    private readonly IAsyncDisposable? _ownedBackend;
    private readonly ILtbLogSink _logSink;
    private readonly IDisposable? _ownedLog;
    private bool _runStarted;
    private bool _disposed;

    public ProductionCalibrationWizardSession(
        ProductionCalibrationWizardSessionOptions options,
        IProductionCalibrationWizardBackend backend,
        IAsyncDisposable? ownedBackend,
        ILtbLogSink logSink,
        IDisposable? ownedLog)
    {
        _options = options;
        _backend = backend;
        _ownedBackend = ownedBackend;
        _logSink = logSink;
        _ownedLog = ownedLog;
    }

    public async Task<ProductionCalibrationWizardSessionResult> RunAsync(
        ICalibrationWizardOutput output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_runStarted)
        {
            throw new InvalidOperationException(
                "A production calibration wizard session can be run only once.");
        }

        _runStarted = true;
        var runtime = new ProductionCalibrationWizardRuntime(
            _backend,
            new ProductionCalibrationWizardOptions
            {
                CaptureDurationSeconds = _options.CaptureDurationSeconds,
                CaptureRateHz = _options.CaptureRateHz,
                DeviceRetryDelay = TimeSpan.FromSeconds(_options.ReconnectDelaySeconds),
            });
        var profileBackend = new FileCalibrationWizardBackend(_options.ProfileStorePath);
        var wizard = new TwoHandCalibrationWizard(
            runtime,
            profileBackend,
            output,
            _logSink);

        CalibrationWizardResult wizardResult;
        try
        {
            wizardResult = await wizard.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProductionCalibrationWizardSessionResult(
                FailedBeforeWizardResult(exception.Message),
                ProductionCalibrationWizardStopReason.WizardFailed,
                $"Production wizard composition failed: {exception.Message}",
                Array.Empty<Exception>());
        }

        if (!wizardResult.Success)
        {
            return new ProductionCalibrationWizardSessionResult(
                wizardResult,
                wizardResult.CleanupFailures.Count > 0
                    ? ProductionCalibrationWizardStopReason.CleanupFailure
                    : wizardResult.Cancelled
                        ? ProductionCalibrationWizardStopReason.Cancellation
                        : ProductionCalibrationWizardStopReason.WizardFailed,
                wizardResult.Diagnostic,
                wizardResult.CleanupFailures);
        }

        ReliableDailyUseResult monitored;
        try
        {
            var activeLease = runtime.ActiveLease ??
                throw new InvalidOperationException(
                    "The production wizard reached Active without retaining its application lease.");
            var watchdog = new ReliableDailyUseCoordinator(
                _backend,
                profileBackend,
                _logSink,
                new ReliableDailyUseOptions
                {
                    MonitorInterval = TimeSpan.FromSeconds(1d / _options.MonitorRateHz),
                    ReconnectRetryDelay = TimeSpan.FromSeconds(
                        _options.ReconnectDelaySeconds),
                });
            monitored = await watchdog
                .MonitorActiveLeaseAsync(activeLease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var cleanupFailures = await SafeDisableAfterHandoffFailureAsync(runtime)
                .ConfigureAwait(false);
            return new ProductionCalibrationWizardSessionResult(
                wizardResult,
                cleanupFailures.Count > 0
                    ? ProductionCalibrationWizardStopReason.CleanupFailure
                    : ProductionCalibrationWizardStopReason.RuntimeFailure,
                cleanupFailures.Count > 0
                    ? $"Production watchdog handoff failed ({exception.Message}); fallback " +
                      "SafeDisable is incomplete."
                    : $"Production watchdog handoff failed ({exception.Message}); fallback " +
                      "SafeDisable completed.",
                cleanupFailures);
        }

        var stopReason = monitored.SafeDisableFailures.Count > 0 ||
            monitored.StopReason == ReliableDailyUseStopReason.SafeDisableFailed
                ? ProductionCalibrationWizardStopReason.CleanupFailure
                : monitored.StopReason switch
                {
                    ReliableDailyUseStopReason.Cancellation =>
                        ProductionCalibrationWizardStopReason.Cancellation,
                    ReliableDailyUseStopReason.SteamVrStopped =>
                        ProductionCalibrationWizardStopReason.SteamVrStopped,
                    _ => ProductionCalibrationWizardStopReason.RuntimeFailure,
                };
        TryReportLifecycleCompletion(output, stopReason, monitored.Diagnostic);
        return new ProductionCalibrationWizardSessionResult(
            wizardResult,
            stopReason,
            monitored.Diagnostic,
            monitored.SafeDisableFailures);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_ownedBackend is not null)
            {
                await _ownedBackend.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _ownedLog?.Dispose();
        }
    }

    private static async Task<IReadOnlyList<Exception>> SafeDisableAfterHandoffFailureAsync(
        ProductionCalibrationWizardRuntime runtime)
    {
        try
        {
            return await runtime.SafeDisableAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            return [cleanupFailure];
        }
    }

    private static CalibrationWizardResult FailedBeforeWizardResult(string diagnostic) =>
        new(
            false,
            false,
            CalibrationWizardState.Stopped,
            [CalibrationWizardState.Stopped],
            Array.Empty<CalibrationWizardProfileView>(),
            diagnostic);

    private static void TryReportLifecycleCompletion(
        ICalibrationWizardOutput output,
        ProductionCalibrationWizardStopReason stopReason,
        string diagnostic)
    {
        try
        {
            output.OnStateChanged(
                CalibrationWizardState.SafeDisable,
                $"production watchdog stopped ({stopReason}); {diagnostic}");
            output.OnStateChanged(
                stopReason == ProductionCalibrationWizardStopReason.CleanupFailure
                    ? CalibrationWizardState.SafeDisable
                    : CalibrationWizardState.Stopped,
                diagnostic);
        }
        catch
        {
            // Output failure after cleanup cannot change the retained safety result.
        }
    }
}
