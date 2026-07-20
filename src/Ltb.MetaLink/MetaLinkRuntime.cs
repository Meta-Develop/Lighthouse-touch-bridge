using System.Runtime.InteropServices;
using Ltb.MetaLink.Interop;

namespace Ltb.MetaLink;

/// <summary>
/// Serialized Windows LibOVR adapter. Construction and polling are safe on
/// non-Windows hosts: Windows discovery and native loading happen only from a
/// guarded poll attempt and report AbiUnavailable there.
/// </summary>
public sealed class MetaLinkRuntime : IMetaLinkRuntime, IMetaLinkControllerSource
{
    private readonly object _sync = new();
    private readonly IOvrNativeApiFactory _apiFactory;
    private readonly IMetaLinkMonotonicClock _clock;
    private readonly IMetaLinkReconnectPolicy _reconnectPolicy;
    private readonly MetaClockMapper _clockMapper;
    private IOvrNativeApi? _api;
    private IntPtr _session;
    private bool _initialized;
    private int _consecutiveFailures;
    private double _nextAttemptSeconds;
    private long _sequence;
    private OvrRuntimeLoadFailure? _lastFailure;
    private MetaLinkRuntimeSnapshot _latest;

    public MetaLinkRuntime(
        IMetaLinkMonotonicClock? clock = null,
        IMetaLinkReconnectPolicy? reconnectPolicy = null,
        MetaClockMapper? clockMapper = null)
        : this(
            new OvrNativeApiFactory(),
            clock ?? new StopwatchMetaLinkClock(),
            reconnectPolicy ?? new ExponentialMetaLinkReconnectPolicy(),
            clockMapper ?? new MetaClockMapper())
    {
    }

    internal MetaLinkRuntime(
        IOvrNativeApiFactory apiFactory,
        IMetaLinkMonotonicClock clock,
        IMetaLinkReconnectPolicy reconnectPolicy,
        MetaClockMapper clockMapper)
    {
        ArgumentNullException.ThrowIfNull(apiFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(reconnectPolicy);
        ArgumentNullException.ThrowIfNull(clockMapper);
        _apiFactory = apiFactory;
        _clock = clock;
        _reconnectPolicy = reconnectPolicy;
        _clockMapper = clockMapper;
        var now = ReadClock();
        _latest = FailureSnapshot(
            0,
            now,
            MetaLinkReadiness.RuntimeStopped,
            "Meta runtime has not been probed yet. Poll the runtime to begin discovery.");
    }

    public bool IsDisposed { get; private set; }

    public MetaLinkRuntimeSnapshot Poll()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _sequence++;
            double now;
            try
            {
                now = ReadClock();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _latest = FailureSnapshot(
                    _sequence,
                    _latest.ObservedAtMonotonicSeconds,
                    MetaLinkReadiness.Faulted,
                    $"Application monotonic clock failed: {exception.Message} Reset LTB and inspect clock diagnostics.");
                CloseNative();
                return _latest;
            }

            try
            {
                if (_session == IntPtr.Zero)
                {
                    if (now < _nextAttemptSeconds && _lastFailure is not null)
                    {
                        _latest = FailureSnapshot(
                            _sequence,
                            now,
                            _lastFailure.Readiness,
                            $"{_lastFailure.Diagnostic} Retry is delayed by the reconnect policy.");
                        return _latest;
                    }

                    var connectionFailure = TryConnect(now);
                    if (connectionFailure is not null)
                    {
                        _latest = FailureSnapshot(
                            _sequence,
                            now,
                            connectionFailure.Readiness,
                            connectionFailure.Diagnostic);
                        return _latest;
                    }
                }

                _latest = PollConnected(now);
                return _latest;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var failure = new OvrRuntimeLoadFailure(
                    MetaLinkReadiness.Faulted,
                    $"LibOVR polling failed unexpectedly: {exception.Message} Reset LTB and inspect runtime diagnostics.");
                _latest = FailureSnapshot(
                    _sequence,
                    now,
                    failure.Readiness,
                    failure.Diagnostic);
                ScheduleFailure(now, failure);
                CloseNative();
                return _latest;
            }
        }
    }

    public bool TryGetLatest(MetaLinkHand hand, out MetaLinkControllerSnapshot? snapshot)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            snapshot = _latest.ForHand(hand).Controller;
            return snapshot is not null;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _latest = FailureSnapshot(
                _sequence,
                _latest.ObservedAtMonotonicSeconds,
                MetaLinkReadiness.RuntimeStopped,
                "Meta Link runtime was reset and inputs were neutralized. Poll to reconnect.");
            CloseNative();
            _clockMapper.Reset();
            _consecutiveFailures = 0;
            _nextAttemptSeconds = 0d;
            _lastFailure = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (IsDisposed)
            {
                return;
            }

            CloseNative();
            IsDisposed = true;
        }
    }

    private OvrRuntimeLoadFailure? TryConnect(double now)
    {
        if (!_apiFactory.TryCreate(out var api, out var loadFailure))
        {
            var failure = loadFailure ?? new OvrRuntimeLoadFailure(
                MetaLinkReadiness.Faulted,
                "Meta runtime factory did not provide a diagnostic. Restart LTB and inspect runtime diagnostics.");
            ScheduleFailure(now, failure);
            return failure;
        }

        _api = api!;
        try
        {
            var parameters = OvrInitParams.InvisibleSession;
            var initializeResult = _api.Initialize(ref parameters);
            if (!OvrConstants.Succeeded(initializeResult))
            {
                var failure = NativeFailure(
                    initializeResult,
                    "LibOVR initialization",
                    _api.GetLastErrorDiagnostic(initializeResult),
                    MetaLinkReadiness.Faulted);
                _api.Dispose();
                _api = null;
                ScheduleFailure(now, failure);
                return failure;
            }

            _initialized = true;
            var createResult = _api.Create(out _session, out _);
            if (!OvrConstants.Succeeded(createResult) || _session == IntPtr.Zero)
            {
                var failure = NativeFailure(
                    createResult,
                    "LibOVR session creation",
                    _api.GetLastErrorDiagnostic(createResult),
                    MetaLinkReadiness.Faulted);
                CloseNative();
                ScheduleFailure(now, failure);
                return failure;
            }

            _consecutiveFailures = 0;
            _nextAttemptSeconds = 0d;
            _lastFailure = null;
            _clockMapper.Reset();
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ExternalException or ArgumentException)
        {
            var failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.Faulted,
                $"LibOVR session initialization failed unexpectedly: {exception.Message} Reset LTB and inspect runtime diagnostics.");
            CloseNative();
            ScheduleFailure(now, failure);
            return failure;
        }
    }

    private MetaLinkRuntimeSnapshot PollConnected(double observedAt)
    {
        var statusResult = _api!.GetSessionStatus(_session, out var sessionStatus);
        if (!OvrConstants.Succeeded(statusResult))
        {
            return FailConnected(observedAt, statusResult, "session status");
        }

        if (sessionStatus.DisplayLost != 0)
        {
            var failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.HeadsetDisconnected,
                "Meta runtime reports the Link display lost. Reconnect the Quest and start Link or Air Link.");
            ScheduleFailure(observedAt, failure);
            CloseNative();
            return FailureSnapshot(_sequence, observedAt, failure.Readiness, failure.Diagnostic);
        }

        if (sessionStatus.ShouldQuit != 0)
        {
            var failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.RuntimeStopped,
                "Meta runtime requested session shutdown. Restart Meta Horizon Link, then retry.");
            ScheduleFailure(observedAt, failure);
            CloseNative();
            return FailureSnapshot(_sequence, observedAt, failure.Readiness, failure.Diagnostic);
        }

        if (sessionStatus.HmdPresent == 0)
        {
            return FailureSnapshot(
                _sequence,
                observedAt,
                MetaLinkReadiness.HeadsetDisconnected,
                "Meta runtime is available, but no Quest is connected. Connect the headset and start Link or Air Link.");
        }

        if (sessionStatus.HmdMounted == 0)
        {
            return FailureSnapshot(
                _sequence,
                observedAt,
                MetaLinkReadiness.ControllersUnavailable,
                "Quest Link is connected, but the headset or Touch controllers appear asleep. Wake the headset and both controllers.");
        }

        var appBefore = ReadClock();
        var metaNow = _api.GetTimeInSeconds();
        var appAfter = ReadClock();
        _clockMapper.Observe(metaNow, appBefore, appAfter);

        var tracking = _api.GetTrackingState(_session, metaNow);
        var controllerTypes = _api.GetConnectedControllerTypes(_session);
        var inputResult = _api.GetInputState(
            _session,
            OvrConstants.ControllerTouch,
            out var input);

        if (!OvrConstants.Succeeded(inputResult))
        {
            var failure = NativeFailure(
                inputResult,
                "Touch input query",
                _api.GetLastErrorDiagnostic(inputResult),
                MetaLinkReadiness.ControllersUnavailable);
            if (failure.Readiness != MetaLinkReadiness.ControllersUnavailable)
            {
                ScheduleFailure(observedAt, failure);
                CloseNative();
            }

            return FailureSnapshot(
                _sequence,
                observedAt,
                failure.Readiness,
                failure.Diagnostic);
        }

        var left = MapHand(MetaLinkHand.Left, tracking, input, controllerTypes);
        var right = MapHand(MetaLinkHand.Right, tracking, input, controllerTypes);
        return new MetaLinkRuntimeSnapshot(_sequence, observedAt, left, right);
    }

    private MetaLinkRuntimeSnapshot FailConnected(
        double observedAt,
        int result,
        string operation)
    {
        var failure = NativeFailure(
            result,
            $"LibOVR {operation}",
            _api!.GetLastErrorDiagnostic(result),
            MetaLinkReadiness.Faulted);
        ScheduleFailure(observedAt, failure);
        CloseNative();
        return FailureSnapshot(_sequence, observedAt, failure.Readiness, failure.Diagnostic);
    }

    private MetaLinkHandSnapshot MapHand(
        MetaLinkHand hand,
        OvrTrackingState tracking,
        OvrInputState input,
        uint controllerTypes)
    {
        if (!OvrInputMapper.TryMap(
            hand,
            tracking,
            input,
            controllerTypes,
            _clockMapper,
            out var snapshot,
            out var diagnostic))
        {
            return new MetaLinkHandSnapshot(
                hand,
                MetaLinkReadiness.ControllersUnavailable,
                $"{diagnostic} Wake and move the {hand} Touch controller, then retry.");
        }

        return new MetaLinkHandSnapshot(
            hand,
            MetaLinkReadiness.Ready,
            diagnostic,
            snapshot);
    }

    private static OvrRuntimeLoadFailure NativeFailure(
        int result,
        string operation,
        string nativeDiagnostic,
        MetaLinkReadiness fallback)
    {
        var readiness = OvrConstants.IsVersionFailure(result)
            ? MetaLinkReadiness.AbiUnavailable
            : OvrConstants.IsRuntimeStoppedFailure(result)
                ? MetaLinkReadiness.RuntimeStopped
                : OvrConstants.IsHeadsetDisconnectedFailure(result)
                    ? MetaLinkReadiness.HeadsetDisconnected
                    : fallback;
        var remediation = readiness switch
        {
            MetaLinkReadiness.AbiUnavailable =>
                "Repair or update Meta Horizon Link to a LibOVR ABI compatible with SDK 32.0.0.",
            MetaLinkReadiness.RuntimeStopped =>
                "Start or restart the Meta Horizon Link runtime service, then retry.",
            MetaLinkReadiness.HeadsetDisconnected =>
                "Connect the Quest and start Link or Air Link, then retry.",
            MetaLinkReadiness.ControllersUnavailable =>
                "Wake and move both Touch controllers, then retry.",
            _ => "Reset LTB and inspect the Meta runtime diagnostics before retrying.",
        };
        return new OvrRuntimeLoadFailure(
            readiness,
            $"{operation} failed: {nativeDiagnostic} {remediation}");
    }

    private void ScheduleFailure(double now, OvrRuntimeLoadFailure failure)
    {
        _consecutiveFailures++;
        var delay = _reconnectPolicy.GetDelay(_consecutiveFailures);
        _nextAttemptSeconds = now + delay.TotalSeconds;
        _lastFailure = failure;
    }

    private void CloseNative()
    {
        var api = _api;
        if (api is null)
        {
            _session = IntPtr.Zero;
            _initialized = false;
            return;
        }

        if (_session != IntPtr.Zero)
        {
            try
            {
                api.Destroy(_session);
            }
            catch (Exception)
            {
                // Continue shutdown so one native cleanup failure cannot leak the library.
            }
            finally
            {
                _session = IntPtr.Zero;
            }
        }

        if (_initialized)
        {
            try
            {
                api.Shutdown();
            }
            catch (Exception)
            {
                // Continue disposal; diagnostics are returned by the operation that failed.
            }
            finally
            {
                _initialized = false;
            }
        }

        try
        {
            api.Dispose();
        }
        catch (Exception)
        {
            // The latest snapshot is neutral before cleanup; never restore stale input.
        }
        finally
        {
            _api = null;
        }
    }

    private static MetaLinkRuntimeSnapshot FailureSnapshot(
        long sequence,
        double observedAt,
        MetaLinkReadiness readiness,
        string diagnostic) =>
        new(
            sequence,
            observedAt,
            new MetaLinkHandSnapshot(MetaLinkHand.Left, readiness, diagnostic),
            new MetaLinkHandSnapshot(MetaLinkHand.Right, readiness, diagnostic));

    private double ReadClock()
    {
        var value = _clock.GetSeconds();
        if (!double.IsFinite(value) || value < 0d)
        {
            throw new InvalidOperationException("Application monotonic clock returned an invalid value.");
        }

        return value;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
