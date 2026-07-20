using System.Runtime.InteropServices;
using Ltb.MetaLink.Interop;

namespace Ltb.MetaLink;

/// <summary>
/// Serialized Windows LibOVR adapter. Construction and polling are safe on
/// non-Windows hosts: Windows discovery and native loading happen only from a
/// guarded poll attempt and report MetaRuntimeMissing there.
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
            MetaLinkReadiness.MetaRuntimeMissing,
            "Meta runtime has not been probed yet.");
    }

    public bool IsDisposed { get; private set; }

    public MetaLinkRuntimeSnapshot Poll()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var now = ReadClock();
            _sequence++;

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

            try
            {
                _latest = PollConnected(now);
                return _latest;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                ExternalException or
                ArgumentException or
                OverflowException)
            {
                var failure = new OvrRuntimeLoadFailure(
                    MetaLinkReadiness.RuntimeIncompatible,
                    $"LibOVR polling failed: {exception.Message}");
                ScheduleFailure(now, failure);
                CloseNative();
                _latest = FailureSnapshot(
                    _sequence,
                    now,
                    failure.Readiness,
                    failure.Diagnostic);
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
                MetaLinkReadiness.RuntimeIncompatible,
                "Meta runtime factory did not provide a diagnostic.");
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
                var failure = new OvrRuntimeLoadFailure(
                    OvrConstants.IsVersionFailure(initializeResult)
                        ? MetaLinkReadiness.RuntimeAbiUnsupported
                        : MetaLinkReadiness.RuntimeIncompatible,
                    _api.GetLastErrorDiagnostic(initializeResult));
                _api.Dispose();
                _api = null;
                ScheduleFailure(now, failure);
                return failure;
            }

            _initialized = true;
            var createResult = _api.Create(out _session, out _);
            if (!OvrConstants.Succeeded(createResult) || _session == IntPtr.Zero)
            {
                var failure = new OvrRuntimeLoadFailure(
                    OvrConstants.IsVersionFailure(createResult)
                        ? MetaLinkReadiness.RuntimeAbiUnsupported
                        : MetaLinkReadiness.RuntimeIncompatible,
                    _api.GetLastErrorDiagnostic(createResult));
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
                MetaLinkReadiness.RuntimeIncompatible,
                $"LibOVR session initialization failed: {exception.Message}");
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

        if (sessionStatus.DisplayLost != 0 || sessionStatus.ShouldQuit != 0)
        {
            var diagnostic = sessionStatus.DisplayLost != 0
                ? "Meta runtime reports the Link display lost."
                : "Meta runtime requested session shutdown.";
            var failure = new OvrRuntimeLoadFailure(
                MetaLinkReadiness.RuntimeIncompatible,
                diagnostic);
            ScheduleFailure(observedAt, failure);
            CloseNative();
            return FailureSnapshot(_sequence, observedAt, failure.Readiness, diagnostic);
        }

        if (sessionStatus.HmdPresent == 0)
        {
            return FailureSnapshot(
                _sequence,
                observedAt,
                MetaLinkReadiness.LinkNotConnected,
                "Meta runtime is available, but no Quest Link or Air Link headset is connected.");
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

        if (sessionStatus.HmdMounted == 0)
        {
            return FailureSnapshot(
                _sequence,
                observedAt,
                MetaLinkReadiness.ControllersUnavailable,
                "Quest Link is connected, but the headset appears unmounted or asleep; Touch availability is heuristic.");
        }

        if (!OvrConstants.Succeeded(inputResult))
        {
            var diagnostic = _api.GetLastErrorDiagnostic(inputResult);
            return FailureSnapshot(
                _sequence,
                observedAt,
                MetaLinkReadiness.ControllersUnavailable,
                $"Touch input state is unavailable: {diagnostic}");
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
        var diagnostic = $"LibOVR {operation} failed: {_api!.GetLastErrorDiagnostic(result)}";
        var failure = new OvrRuntimeLoadFailure(
            MetaLinkReadiness.RuntimeIncompatible,
            diagnostic);
        ScheduleFailure(observedAt, failure);
        CloseNative();
        return FailureSnapshot(_sequence, observedAt, failure.Readiness, diagnostic);
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
                diagnostic);
        }

        return new MetaLinkHandSnapshot(
            hand,
            MetaLinkReadiness.InputsLive,
            diagnostic,
            snapshot);
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

        api.Dispose();
        _api = null;
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
