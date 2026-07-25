using Ltb.MetaLink.Interop;

namespace Ltb.MetaLink.Tests;

public sealed class MetaLinkRuntimeTests
{
    private static readonly string[] InitializeFailureCalls =
        ["Initialize", "LastError", "Dispose"];

    [Theory]
    [InlineData(MetaLinkReadiness.NotInstalled)]
    [InlineData(MetaLinkReadiness.AbiUnavailable)]
    [InlineData(MetaLinkReadiness.RuntimeStopped)]
    [InlineData(MetaLinkReadiness.Faulted)]
    public void FactoryFailureMapsImmediatelyToBothHands(MetaLinkReadiness readiness)
    {
        var clock = new ManualClock(10d);
        var factory = new ScriptedFactory();
        factory.EnqueueFailure(readiness, "synthetic immediate diagnostic");
        using var runtime = Runtime(factory, clock);

        var snapshot = runtime.Poll();

        Assert.Equal(readiness, snapshot.Left.Readiness);
        Assert.Equal(readiness, snapshot.Right.Readiness);
        Assert.Contains("synthetic immediate diagnostic", snapshot.Left.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", snapshot.Left.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionInitializationFailureIsAbiUnsupported()
    {
        var api = TestOvrApi.Live();
        api.InitializeResult = OvrConstants.ErrorLibVersion;
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        using var runtime = Runtime(factory, new ManualClock(10d));

        var snapshot = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.AbiUnavailable, snapshot.Left.Readiness);
        Assert.Equal(InitializeFailureCalls, api.Calls);
    }

    [Fact]
    public void ReadinessDistinguishesNoLinkFromSleepingHeadset()
    {
        var noLinkApi = TestOvrApi.Live();
        noLinkApi.SessionStatus.HmdPresent = 0;
        var noLinkFactory = new ScriptedFactory();
        noLinkFactory.EnqueueApi(noLinkApi);
        using var noLinkRuntime = Runtime(noLinkFactory, new ManualClock(10d));

        var noLink = noLinkRuntime.Poll();

        Assert.Equal(MetaLinkReadiness.HeadsetDisconnected, noLink.Left.Readiness);

        var sleepingApi = TestOvrApi.Live();
        sleepingApi.SessionStatus.HmdMounted = 0;
        var sleepingFactory = new ScriptedFactory();
        sleepingFactory.EnqueueApi(sleepingApi);
        using var sleepingRuntime = Runtime(sleepingFactory, new ManualClock(10d));

        var sleeping = sleepingRuntime.Poll();

        Assert.Equal(MetaLinkReadiness.ControllersUnavailable, sleeping.Left.Readiness);
        Assert.Contains("asleep", sleeping.Left.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadinessIsIndependentPerHandFromMasksAndPoseValidity()
    {
        var api = TestOvrApi.Live();
        api.ConnectedControllerTypes = OvrConstants.ControllerTouch;
        api.Input.ControllerType = OvrConstants.ControllerTouch;
        api.Tracking.LeftHandStatusFlags = OvrConstants.StatusPositionTracked;
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        using var runtime = Runtime(factory, new ManualClock(10d));

        var snapshot = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.ControllersUnavailable, snapshot.Left.Readiness);
        Assert.Equal(MetaLinkReadiness.Ready, snapshot.Right.Readiness);
        Assert.Null(snapshot.Left.Controller);
        Assert.NotNull(snapshot.Right.Controller);
        Assert.Contains("Wake", snapshot.Left.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveReadinessPublishesBothHandsAndLatestSource()
    {
        var api = TestOvrApi.Live();
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        using var runtime = Runtime(factory, new ManualClock(10d));

        var snapshot = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.Ready, snapshot.Left.Readiness);
        Assert.Equal(MetaLinkReadiness.Ready, snapshot.Right.Readiness);
        Assert.True(runtime.TryGetLatest(MetaLinkHand.Left, out var left));
        Assert.Same(snapshot.Left.Controller, left);
        Assert.False(left!.Battery.IsAvailable);
    }

    [Fact]
    public void InputFailureIsControllerUnavailableWithoutDroppingSession()
    {
        var api = TestOvrApi.Live();
        api.InputResult = OvrConstants.ErrorDeviceUnavailable;
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        var clock = new ManualClock(10d);
        using var runtime = Runtime(factory, clock);

        var first = runtime.Poll();
        api.InputResult = 0;
        api.TimeSeconds = 101d;
        api.Tracking.LeftHandPose.TimeInSeconds = 101d;
        api.Tracking.RightHandPose.TimeInSeconds = 102d;
        clock.Seconds = 11d;
        var second = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.ControllersUnavailable, first.Left.Readiness);
        Assert.Equal(MetaLinkReadiness.Ready, second.Left.Readiness);
        Assert.Equal(1, factory.TryCreateCount);
    }

    [Theory]
    [InlineData(OvrConstants.ErrorNoHmd, MetaLinkReadiness.HeadsetDisconnected, "Connect the Quest")]
    [InlineData(OvrConstants.ErrorServiceConnection, MetaLinkReadiness.RuntimeStopped, "runtime service")]
    [InlineData(OvrConstants.ErrorNotInitialized, MetaLinkReadiness.RuntimeStopped, "runtime service")]
    public void CreateFailuresMapToActionableReadiness(
        int result,
        MetaLinkReadiness readiness,
        string remediation)
    {
        var api = TestOvrApi.Live();
        api.CreateResult = result;
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        using var runtime = Runtime(factory, new ManualClock(10d));

        var snapshot = runtime.Poll();

        Assert.Equal(readiness, snapshot.Left.Readiness);
        Assert.Contains(remediation, snapshot.Left.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Null(snapshot.Left.Controller);
    }

    [Fact]
    public void ReadinessLossAtomicallyClearsPreviouslyLiveControllers()
    {
        var api = TestOvrApi.Live();
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        using var runtime = Runtime(factory, new ManualClock(10d));

        Assert.Equal(MetaLinkReadiness.Ready, runtime.Poll().Left.Readiness);
        Assert.True(runtime.TryGetLatest(MetaLinkHand.Left, out _));

        api.SessionStatus.HmdPresent = 0;
        var lost = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.HeadsetDisconnected, lost.Left.Readiness);
        Assert.False(runtime.TryGetLatest(MetaLinkHand.Left, out var neutral));
        Assert.Null(neutral);
    }

    [Fact]
    public void InvalidClockFaultAndResetCannotExposeStaleInput()
    {
        var api = TestOvrApi.Live();
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        var clock = new ManualClock(10d);
        using var runtime = Runtime(factory, clock);

        Assert.Equal(MetaLinkReadiness.Ready, runtime.Poll().Left.Readiness);
        clock.Seconds = double.NaN;

        var faulted = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.Faulted, faulted.Left.Readiness);
        Assert.False(runtime.TryGetLatest(MetaLinkHand.Left, out var afterFault));
        Assert.Null(afterFault);

        runtime.Reset();

        Assert.False(runtime.TryGetLatest(MetaLinkHand.Left, out var afterReset));
        Assert.Null(afterReset);
    }

    [Fact]
    public void UnexpectedFactoryFailureNeutralizesLatestStateAsFaulted()
    {
        var clock = new ManualClock(10d);
        var factory = new ThrowingFactory();
        using var runtime = Runtime(factory, clock);

        var snapshot = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.Faulted, snapshot.Left.Readiness);
        Assert.False(runtime.TryGetLatest(MetaLinkHand.Left, out var latest));
        Assert.Null(latest);
    }

    [Fact]
    public void ReconnectBackoffDelaysThenAllowsAnotherFactoryAttempt()
    {
        var clock = new ManualClock(10d);
        var factory = new ScriptedFactory();
        factory.EnqueueFailure(MetaLinkReadiness.NotInstalled, "not installed");
        factory.EnqueueApi(TestOvrApi.Live());
        using var runtime = Runtime(factory, clock);

        var first = runtime.Poll();
        var delayed = runtime.Poll();
        clock.Seconds = 10.25d;
        var reconnected = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.NotInstalled, first.Left.Readiness);
        Assert.Contains("delayed", delayed.Left.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(MetaLinkReadiness.Ready, reconnected.Left.Readiness);
        Assert.Equal(2, factory.TryCreateCount);
    }

    [Fact]
    public void ResetAndDisposeUseDestroyShutdownDisposeOrder()
    {
        var api = TestOvrApi.Live();
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        factory.EnqueueApi(TestOvrApi.Live());
        var runtime = Runtime(factory, new ManualClock(10d));
        runtime.Poll();

        runtime.Reset();

        Assert.True(api.Calls.IndexOf("Destroy") < api.Calls.IndexOf("Shutdown"));
        Assert.True(api.Calls.IndexOf("Shutdown") < api.Calls.IndexOf("Dispose"));
        Assert.False(runtime.TryGetLatest(MetaLinkHand.Left, out var resetLeft));
        Assert.Null(resetLeft);
        runtime.Dispose();
        runtime.Dispose();
        Assert.True(runtime.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => runtime.Poll());
    }

    [Fact]
    public void DefaultRuntimeDoesNotExecuteWindowsDiscoveryOnLinux()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var runtime = new MetaLinkRuntime();

        var snapshot = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.AbiUnavailable, snapshot.Left.Readiness);
        Assert.Contains("Windows x64", snapshot.Left.Diagnostic, StringComparison.Ordinal);
    }

    private static MetaLinkRuntime Runtime(IOvrNativeApiFactory factory, ManualClock clock) => new(
        factory,
        clock,
        new ExponentialMetaLinkReconnectPolicy(
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1)),
        new MetaClockMapper());

    private sealed class ManualClock(double seconds) : IMetaLinkMonotonicClock
    {
        public double Seconds { get; set; } = seconds;

        public double GetSeconds() => Seconds;
    }

    private sealed class ScriptedFactory : IOvrNativeApiFactory
    {
        private readonly Queue<(IOvrNativeApi? Api, OvrRuntimeLoadFailure? Failure)> _results = new();

        public int TryCreateCount { get; private set; }

        public void EnqueueApi(IOvrNativeApi api) => _results.Enqueue((api, null));

        public void EnqueueFailure(MetaLinkReadiness readiness, string diagnostic) =>
            _results.Enqueue((null, new OvrRuntimeLoadFailure(readiness, diagnostic)));

        public bool TryCreate(out IOvrNativeApi? api, out OvrRuntimeLoadFailure? failure)
        {
            TryCreateCount++;
            if (_results.Count == 0)
            {
                api = null;
                failure = new OvrRuntimeLoadFailure(
                    MetaLinkReadiness.NotInstalled,
                    "script exhausted");
                return false;
            }

            (api, failure) = _results.Dequeue();
            return api is not null;
        }
    }

    private sealed class ThrowingFactory : IOvrNativeApiFactory
    {
        public bool TryCreate(out IOvrNativeApi? api, out OvrRuntimeLoadFailure? failure)
        {
            api = null;
            failure = null;
            throw new InvalidOperationException("synthetic factory fault");
        }
    }

    private sealed class TestOvrApi : IOvrNativeApi
    {
        public List<string> Calls { get; } = [];

        public int InitializeResult { get; set; }

        public int CreateResult { get; set; }

        public int SessionStatusResult { get; set; }

        public int InputResult { get; set; }

        public double TimeSeconds { get; set; } = 100d;

        public uint ConnectedControllerTypes { get; set; }

        public OvrSessionStatus SessionStatus;

        public OvrTrackingState Tracking;

        public OvrInputState Input;

        public static TestOvrApi Live() => new()
        {
            ConnectedControllerTypes = OvrConstants.ControllerTouch,
            SessionStatus = new OvrSessionStatus
            {
                HmdPresent = 1,
                HmdMounted = 1,
            },
            Tracking = InputMappingTests.LiveTracking(),
            Input = new OvrInputState
            {
                ControllerType = OvrConstants.ControllerTouch,
                IndexTriggerLeft = 0.1f,
                IndexTriggerRight = 0.2f,
                HandTriggerLeft = 0.3f,
                HandTriggerRight = 0.4f,
            },
        };

        public int Initialize(ref OvrInitParams parameters)
        {
            Calls.Add("Initialize");
            Assert.Equal(OvrConstants.InitializationFlags, parameters.Flags);
            Assert.Equal(OvrConstants.RequestedMinorVersion, parameters.RequestedMinorVersion);
            return InitializeResult;
        }

        public void Shutdown() => Calls.Add("Shutdown");

        public int Create(out IntPtr session, out OvrGraphicsLuid luid)
        {
            Calls.Add("Create");
            session = CreateResult >= 0 ? new IntPtr(42) : IntPtr.Zero;
            luid = default;
            return CreateResult;
        }

        public void Destroy(IntPtr session)
        {
            Assert.Equal(new IntPtr(42), session);
            Calls.Add("Destroy");
        }

        public double GetTimeInSeconds()
        {
            Calls.Add("Time");
            return TimeSeconds;
        }

        public OvrTrackingState GetTrackingState(IntPtr session, double absoluteTimeSeconds)
        {
            Calls.Add("Tracking");
            Assert.Equal(TimeSeconds, absoluteTimeSeconds);
            return Tracking;
        }

        public int GetInputState(
            IntPtr session,
            uint controllerType,
            out OvrInputState inputState)
        {
            Calls.Add("Input");
            Assert.Equal(OvrConstants.ControllerTouch, controllerType);
            inputState = Input;
            return InputResult;
        }

        public uint GetConnectedControllerTypes(IntPtr session)
        {
            Calls.Add("Controllers");
            return ConnectedControllerTypes;
        }

        public int GetSessionStatus(IntPtr session, out OvrSessionStatus sessionStatus)
        {
            Calls.Add("Status");
            sessionStatus = SessionStatus;
            return SessionStatusResult;
        }

        public string GetLastErrorDiagnostic(int fallbackResult)
        {
            Calls.Add("LastError");
            return $"synthetic LibOVR result {fallbackResult}";
        }

        public void Dispose() => Calls.Add("Dispose");
    }
}
