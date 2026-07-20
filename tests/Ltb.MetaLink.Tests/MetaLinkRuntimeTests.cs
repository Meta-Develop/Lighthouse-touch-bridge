using Ltb.MetaLink.Interop;

namespace Ltb.MetaLink.Tests;

public sealed class MetaLinkRuntimeTests
{
    private static readonly string[] InitializeFailureCalls =
        ["Initialize", "LastError", "Dispose"];

    [Theory]
    [InlineData(MetaLinkReadiness.MetaRuntimeMissing)]
    [InlineData(MetaLinkReadiness.RuntimeAbiUnsupported)]
    [InlineData(MetaLinkReadiness.RuntimeIncompatible)]
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

        Assert.Equal(MetaLinkReadiness.RuntimeAbiUnsupported, snapshot.Left.Readiness);
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

        Assert.Equal(MetaLinkReadiness.LinkNotConnected, noLink.Left.Readiness);

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
        Assert.Equal(MetaLinkReadiness.InputsLive, snapshot.Right.Readiness);
        Assert.Null(snapshot.Left.Controller);
        Assert.NotNull(snapshot.Right.Controller);
    }

    [Fact]
    public void LiveReadinessPublishesBothHandsAndLatestSource()
    {
        var api = TestOvrApi.Live();
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        using var runtime = Runtime(factory, new ManualClock(10d));

        var snapshot = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.InputsLive, snapshot.Left.Readiness);
        Assert.Equal(MetaLinkReadiness.InputsLive, snapshot.Right.Readiness);
        Assert.True(runtime.TryGetLatest(MetaLinkHand.Left, out var left));
        Assert.Same(snapshot.Left.Controller, left);
        Assert.False(left!.Battery.IsAvailable);
    }

    [Fact]
    public void InputFailureIsControllerUnavailableWithoutDroppingSession()
    {
        var api = TestOvrApi.Live();
        api.InputResult = -1007;
        var factory = new ScriptedFactory();
        factory.EnqueueApi(api);
        using var runtime = Runtime(factory, new ManualClock(10d));

        var first = runtime.Poll();
        api.InputResult = 0;
        var second = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.ControllersUnavailable, first.Left.Readiness);
        Assert.Equal(MetaLinkReadiness.InputsLive, second.Left.Readiness);
        Assert.Equal(1, factory.TryCreateCount);
    }

    [Fact]
    public void ReconnectBackoffDelaysThenAllowsAnotherFactoryAttempt()
    {
        var clock = new ManualClock(10d);
        var factory = new ScriptedFactory();
        factory.EnqueueFailure(MetaLinkReadiness.MetaRuntimeMissing, "not installed");
        factory.EnqueueApi(TestOvrApi.Live());
        using var runtime = Runtime(factory, clock);

        var first = runtime.Poll();
        var delayed = runtime.Poll();
        clock.Seconds = 10.25d;
        var reconnected = runtime.Poll();

        Assert.Equal(MetaLinkReadiness.MetaRuntimeMissing, first.Left.Readiness);
        Assert.Contains("delayed", delayed.Left.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(MetaLinkReadiness.InputsLive, reconnected.Left.Readiness);
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

        Assert.Equal(MetaLinkReadiness.MetaRuntimeMissing, snapshot.Left.Readiness);
        Assert.Contains("Windows-only", snapshot.Left.Diagnostic, StringComparison.Ordinal);
    }

    private static MetaLinkRuntime Runtime(ScriptedFactory factory, ManualClock clock) => new(
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
                    MetaLinkReadiness.MetaRuntimeMissing,
                    "script exhausted");
                return false;
            }

            (api, failure) = _results.Dequeue();
            return api is not null;
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
